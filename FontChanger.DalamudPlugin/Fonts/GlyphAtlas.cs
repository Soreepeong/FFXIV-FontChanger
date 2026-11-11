using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace FontChanger.DalamudPlugin.Fonts;

// The font shaders take UVs in texels of the bound texture, so pages may be smaller than the game's; subpixel atlases have odd
// widths (how FontSubpixelPS tells them apart) and take R, G, B for subpixel coverage and A for grayscale.
internal sealed unsafe class GlyphAtlas : IDisposable
{
    /// <summary>The most pages an atlas has, which leaves every copy a texture slot for <see cref="BlankSlot"/>.</summary>
    public const int MaxTextureSlots = AtkFontManager.Font.MaxTextures - 1;
    public const int PlanesPerPage = 4;
    public const int SubpixelPlane = 3;

    private const int Padding = 1;

    private static readonly int[] Bgra8888ChannelOffsets = [2, 1, 0, 3];
    private static readonly int[] Bgra4444ChannelShifts = [8, 4, 0, 12];
    private static readonly byte[] Lookup8To4 = Enumerable.Range(0, 256).Select(v => (byte)(((v * 15) + 127) / 255)).ToArray();

    private readonly List<Page> _pages = [];

    private MyComPtr<ID3D11DeviceContext> _context;

    // Planes are numbered page * PlanesPerPage + plane; those from the frontier on are unused since Clear, those before it full.
    private int _current;
    private int _frontier;

    /// <summary>Sides up to 4096, as glyph positions take 12 bits.</summary>
    public GlyphAtlas(int width, int height, int maxPages, string name, bool subpixel, AtlasTextureFormat format)
    {
        this.Width = width;
        this.Height = height;
        this.MaxPages = Math.Clamp(maxPages, 1, MaxTextureSlots);
        this.Name = name;
        this.Subpixel = subpixel;
        this.Format4444 = format == AtlasTextureFormat.Bgra4444;
        this.BytesPerPixel = this.Format4444 ? 2 : 4;
        this._current = this.FirstPlane;
        this._frontier = this.FirstPlane + this.PlaneStep;
    }

    public enum AllocationResult
    {
        Placed,

        /// <summary>Placed on a page added for it, which the copies drawing from the atlas have no texture slot of yet.</summary>
        PlacedOnNewPage,

        Full,
    }

    public int Width { get; }

    public int Height { get; }

    public string Name { get; }

    public bool Subpixel { get; }

    public int MaxPages { get; }

    /// <summary>
    /// Gets the first texture slot none of the atlas's pages takes, which holds the blank texture in every copy drawing from it.
    /// A glyph with a box but no pixels in the atlas samples it.
    /// </summary>
    /// <remarks>Kept low, as a texture index past a font's texture count has no vertex buffer, should a game font ever draw the glyph.</remarks>
    public int BlankSlot => this.MaxPages;

    private bool Format4444 { get; }

    private int BytesPerPixel { get; }

    private int Pitch => this.Width * this.BytesPerPixel;

    private void SetChannel(byte* texel, int plane, byte value)
    {
        if (!this.Format4444)
        {
            texel[BgraOffset(plane)] = value;
            return;
        }

        var shift = Bgra4444ChannelShifts[plane];
        var t = (ushort*)texel;
        *t = (ushort)((*t & ~(0xF << shift)) | (Lookup8To4[value] << shift));
    }

    // Every channel in a subpixel atlas.
    private void ZeroRect(Page p, int plane, int x, int y, int width, int height)
    {
        for (var row = y; row < y + height; row++)
        {
            var d = p.Shadow + (row * this.Pitch) + (x * this.BytesPerPixel);
            if (this.Subpixel)
            {
                NativeMemory.Clear(d, (nuint)(width * this.BytesPerPixel));
                continue;
            }

            for (var col = 0; col < width; col++)
                this.SetChannel(d + (col * this.BytesPerPixel), plane, 0);
        }

        p.MarkDirty(x, y, x + width, y + height);
    }

    private long DrawnOf(int plane) => this._pages[plane / PlanesPerPage].Drawn[plane % PlanesPerPage];

    public static byte Get4444(ushort texel, int plane) => (byte)(((texel >> Bgra4444ChannelShifts[plane]) & 0xF) * 17);

    public static int BgraOffset(int plane) => Bgra8888ChannelOffsets[plane];

    private int FirstPlane => this.Subpixel ? SubpixelPlane : 0;

    private int PlaneStep => this.Subpixel ? PlanesPerPage : 1;

    public int PageCount => this._pages.Count;

    public KernelTexture* GetKernelTexture(int page) => this._pages[page].Kernel;

    public int MaxPlanes => this.MaxPages * PlanesPerPage;

    public bool CanHold(int width, int height) => width + (2 * Padding) <= this.Width && height + (2 * Padding) <= this.Height;

    /// <summary>Keeps the textures, which fonts refer to; between frames, as no glyph of the old contents may be drawn afterwards.</summary>
    /// <remarks>The old pixels stay until each area is allocated again, which empties it first.</remarks>
    public void Clear()
    {
        Misc.DebugAssertMainThread();
        foreach (var p in this._pages)
        {
            p.CursorsPerPlane.AsSpan().Clear();
            for (var i = 0; i < PlanesPerPage; i++)
                p.Generations[i]++;
            p.Recycled.AsSpan().Fill(true);
            p.Drawn.AsSpan().Clear();
        }

        this._current = this.FirstPlane;
        this._frontier = this.FirstPlane + this.PlaneStep;
    }

    /// <summary>
    /// Between frames. The least recently drawn plane is allocated in from its start, and its glyphs are of an older generation
    /// from now on.
    /// </summary>
    /// <returns>The plane, numbered page * <see cref="PlanesPerPage"/> + plane.</returns>
    public int RecycleLeastRecentlyDrawn()
    {
        Misc.DebugAssertMainThread();
        var victim = this.FirstPlane;
        for (var i = victim + this.PlaneStep; i < this._frontier && i / PlanesPerPage < this._pages.Count; i += this.PlaneStep)
        {
            if (this.DrawnOf(i) < this.DrawnOf(victim))
                victim = i;
        }

        var p = this._pages[victim / PlanesPerPage];
        var plane = victim % PlanesPerPage;
        p.CursorsPerPlane[plane] = default;
        p.Generations[plane]++;
        p.Recycled[plane] = true;
        this._current = victim;
        return victim;
    }

    /// <summary>Marks the plane drawn in <paramref name="frame"/>.</summary>
    /// <returns>False if <paramref name="generation"/> is no longer the plane's, as it was reused since.</returns>
    public bool Touch(int page, int plane, uint generation, long frame)
    {
        var p = this._pages[page];
        if (p.Generations[plane] != generation)
            return false;
        p.Drawn[plane] = frame;
        return true;
    }

    /// <summary>The area is emptied first in a reused plane, and <paramref name="frame"/> marks the plane drawn.</summary>
    public AllocationResult TryAllocate(int width, int height, long frame, out Allocation res)
    {
        Misc.DebugAssertMainThread();
        res = default;
        var w = width + (2 * Padding);
        var h = height + (2 * Padding);
        var pages = this._pages.Count;
        if (!this.CanHold(width, height) || !this.TryEnsurePages(1))
            return AllocationResult.Full;

        while (true)
        {
            var page = this._current / PlanesPerPage;
            var plane = this._current % PlanesPerPage;
            var p = this._pages[page];
            ref var s = ref p.CursorsPerPlane[plane];
            if (s.X + w > this.Width)
                s = new() { Y = s.Y + s.Height };
            if (s.Y + h <= this.Height)
            {
                res = new(page, plane, s.X + Padding, s.Y + Padding, p.Generations[plane]);
                if (p.Recycled[plane])
                    this.ZeroRect(p, plane, s.X, s.Y, w, h);
                p.Drawn[plane] = frame;
                s.X += w;
                s.Height = Math.Max(s.Height, h);
                return this._pages.Count != pages ? AllocationResult.PlacedOnNewPage : AllocationResult.Placed;
            }

            if (this._frontier >= this.MaxPlanes || !this.TryEnsurePages((this._frontier / PlanesPerPage) + 1))
                return AllocationResult.Full;
            this._current = this._frontier;
            this._frontier += this.PlaneStep;
        }
    }

    /// <summary>
    /// Coverage goes at <paramref name="alphaX"/> in the box, cut off past its right edge; in a subpixel atlas,
    /// <paramref name="lcd"/> (3 bytes a pixel) fills R, G and B, else they get alpha.
    /// </summary>
    public void Write(
        int page,
        int plane,
        int x,
        int y,
        int width,
        int height,
        ReadOnlySpan<byte> alpha,
        int alphaStride,
        int alphaX,
        ReadOnlySpan<byte> lcd = default)
    {
        var columns = Math.Min(alphaStride, width - alphaX);
        var p = this._pages[page];
        var dst = p.Shadow + (y * this.Pitch) + (x * this.BytesPerPixel);
        if (this.Subpixel)
        {
            var hasLcd = !lcd.IsEmpty && lcd.Length >= alpha.Length * 3;
            for (var row = 0; row < height; row++)
            {
                var d = dst + (row * this.Pitch) + (alphaX * this.BytesPerPixel);
                for (var col = 0; col < columns; col++)
                {
                    var i = (row * alphaStride) + col;
                    var texel = d + (col * this.BytesPerPixel);
                    this.SetChannel(texel, SubpixelPlane, alpha[i]);
                    for (var c = 0; c < 3; c++)
                        this.SetChannel(texel, c, hasLcd ? lcd[(i * 3) + c] : alpha[i]);
                }
            }
        }
        else
        {
            for (var row = 0; row < height; row++)
            {
                var src = alpha.Slice(row * alphaStride, alphaStride);
                var d = dst + (row * this.Pitch) + (alphaX * this.BytesPerPixel);
                for (var col = 0; col < columns; col++)
                    this.SetChannel(d + (col * this.BytesPerPixel), plane, src[col]);
            }
        }

        p.MarkDirty(x, y, x + width, y + height);
    }

    /// <summary>At Present.</summary>
    public void Upload()
    {
        Misc.DebugAssertMainThread();
        if (!this._context)
            return;
        foreach (var p in this._pages)
        {
            if (p.DirtyRight <= p.DirtyLeft)
                continue;
            var box = new D3D11_BOX
            {
                left = (uint)p.DirtyLeft,
                top = (uint)p.DirtyTop,
                front = 0,
                right = (uint)p.DirtyRight,
                bottom = (uint)p.DirtyBottom,
                back = 1,
            };
            this._context.Value->UpdateSubresource(
                p.Resource.Value,
                0,
                &box,
                p.Shadow + (p.DirtyTop * this.Pitch) + (p.DirtyLeft * this.BytesPerPixel),
                (uint)this.Pitch,
                0);
            p.ClearDirty();
        }
    }

    public void Dispose()
    {
        foreach (var p in this._pages)
            p.Dispose();
        this._pages.Clear();
        this._context.Dispose();
    }

    public bool TryEnsurePages(int count)
    {
        if (count > this.MaxPages)
            return false;

        while (this._pages.Count < count)
        {
            var added = this._pages.Count;
            this._pages.Add(new(this.Width, this.Height, $"FontChanger.DalamudPlugin {this.Name} page {added}", this.Format4444));
            if (!this._context)
                this._context = this._pages[0].Resource.Value->GetImmediateContext();
            Plugin.Log.Information("Added {name} atlas page {n} of {max} ({width} x {height})", this.Name, added + 1, this.MaxPages, this.Width, this.Height);
        }

        return true;
    }

    private sealed class Page : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly OwnedKernelTexture _texture;

        public Page(int width, int height, string name, bool format4444)
        {
            this._width = width;
            this._height = height;
            this.Shadow = (byte*)NativeMemory.AllocZeroed((nuint)(width * (format4444 ? 2 : 4) * height));
            try
            {
                this._texture = new(width, height, name, format4444);
            }
            catch
            {
                NativeMemory.Free(this.Shadow);
                throw;
            }

            this.MarkDirty(0, 0, width, height);
        }

        public KernelTexture* Kernel => this._texture.Kernel;

        public ref readonly MyComPtr<ID3D11Resource> Resource => ref this._texture.Resource;

        public byte* Shadow { get; private set; }

        public RowCursor[] CursorsPerPlane { get; } = new RowCursor[PlanesPerPage];

        /// <summary>Bumped each time the plane is emptied or reused.</summary>
        public uint[] Generations { get; } = new uint[PlanesPerPage];

        /// <summary>Reused or cleared, keeping its earlier pixels where nothing was placed since.</summary>
        public bool[] Recycled { get; } = new bool[PlanesPerPage];

        /// <summary>The frame a glyph in the plane was last drawn or placed in.</summary>
        public long[] Drawn { get; } = new long[PlanesPerPage];

        public int DirtyLeft { get; private set; }

        public int DirtyTop { get; private set; }

        public int DirtyRight { get; private set; }

        public int DirtyBottom { get; private set; }

        public void MarkDirty(int left, int top, int right, int bottom)
        {
            this.DirtyLeft = Math.Min(this.DirtyLeft, left);
            this.DirtyTop = Math.Min(this.DirtyTop, top);
            this.DirtyRight = Math.Max(this.DirtyRight, right);
            this.DirtyBottom = Math.Max(this.DirtyBottom, bottom);
        }

        public void ClearDirty()
        {
            this.DirtyLeft = this._width;
            this.DirtyTop = this._height;
            this.DirtyRight = this.DirtyBottom = 0;
        }

        public void Dispose()
        {
            this._texture.Dispose();
            if (this.Shadow is not null)
            {
                NativeMemory.Free(this.Shadow);
                this.Shadow = null;
            }
        }

        public record struct RowCursor(int X, int Y, int Height);
    }

    /// <summary>Where a glyph was placed.</summary>
    /// <param name="Page">The page.</param>
    /// <param name="Plane">A channel index.</param>
    /// <param name="X">The left inside the padding.</param>
    /// <param name="Y">The top inside the padding.</param>
    /// <param name="Generation">Of the plane, which a glyph placed there keeps. <seealso cref="Touch"/></param>
    public readonly record struct Allocation(int Page, int Plane, int X, int Y, uint Generation);
}
