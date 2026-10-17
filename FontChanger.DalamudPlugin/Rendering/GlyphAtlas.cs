using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace FontChanger.DalamudPlugin.Rendering;

/// <summary>
/// Atlas pages for the rasterized glyphs of a game font family's replaced fonts. A page is a B8G8R8A8 or B4G4R4A4 texture
/// holding four glyph planes, one per channel, as the game's own atlases do; glyphs are written to a CPU copy, and the
/// changed rectangle goes to the GPU from <see cref="Upload"/>. In B4G4R4A4 coverage has 16 levels.
/// </summary>
/// <remarks>
/// The font shaders take UVs as texels over the bound texture's size, so pages may be smaller than the game's atlases;
/// only the edge and glare shaders' texel step comes from the font's claimed texture width, which a replaced font sets
/// from its atlas's width. A replaced font's textures are all its atlas's: page n is texture index n, and the game's
/// glyphs it draws are copied into the atlas first.
/// Subpixel atlases have odd widths (how FontSubpixelPS tells them apart); glyphs take R, G, B for subpixel coverage and
/// A (<see cref="SubpixelPlane"/>) for grayscale.
/// </remarks>
internal sealed unsafe class GlyphAtlas : IDisposable
{
    public const int MaxTextureSlots = AtkFontManager.Font.MaxTextures;
    public const int PlanesPerPage = 4;
    public const int SubpixelPlane = 3;

    private const int Padding = 1;

    private static readonly int[] Bgra8888ChannelOffsets = [2, 1, 0, 3];
    private static readonly int[] Bgra4444ChannelShifts = [8, 4, 0, 12];
    private static readonly byte[] Lookup8To4 = Enumerable.Range(0, 256).Select(v => (byte)(((v * 15) + 127) / 255)).ToArray();

    private readonly List<Page> _pages = [];
    private readonly Lock _sync = new();

    private MyComPtr<ID3D11DeviceContext> _context;

    // Planes are numbered page * PlanesPerPage + plane. Glyphs are allocated in the current one; those from the frontier
    // on haven't been used yet (since Clear), the others before it are full.
    private int _current;
    private int _frontier;

    /// <summary>
    /// Makes an atlas of pages of a size (sides up to 4096: glyph positions take 12 bits; an odd width for a subpixel
    /// atlas), of at most <paramref name="maxPages"/> pages.
    /// </summary>
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

    public event Action? PageAdded;

    public int Width { get; }

    public int Height { get; }

    public string Name { get; }

    public bool Subpixel { get; }

    public int MaxPages { get; }

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

    private byte GetChannel(byte* texel, int plane) => this.Format4444 ? Get4444(*(ushort*)texel, plane) : texel[BgraOffset(plane)];

    /// <summary>Gets a plane's 8-bit coverage in a B4G4R4A4 texel (planes are the R, G, B and A channels).</summary>
    public static byte Get4444(ushort texel, int plane) => (byte)(((texel >> Bgra4444ChannelShifts[plane]) & 0xF) * 17);

    /// <summary>Gets a plane's byte offset in a B8G8R8A8 texel.</summary>
    public static int BgraOffset(int plane) => Bgra8888ChannelOffsets[plane];

    private int FirstPlane => this.Subpixel ? SubpixelPlane : 0;

    private int PlaneStep => this.Subpixel ? PlanesPerPage : 1;

    public int PageCount => this._pages.Count;

    public KernelTexture* GetKernelTexture(int page) => this._pages[page].Kernel;

    public int MaxPlanes => this.MaxPages * PlanesPerPage;

    public static int PaddedArea(int width, int height) => (width + (2 * Padding)) * (height + (2 * Padding));

    /// <summary>Gets whether a glyph of this size, with its padding, fits in one empty plane.</summary>
    public bool CanHold(int width, int height) => width + (2 * Padding) <= this.Width && height + (2 * Padding) <= this.Height;

    /// <summary>
    /// Empties every page, keeping the textures (fonts refer to them). Framework thread, between frames: no glyph
    /// referring to the old contents may be drawn afterwards.
    /// </summary>
    public void Clear()
    {
        Misc.DebugAssertMainThread();
        lock (this._sync)
        {
            foreach (var p in this._pages)
            {
                NativeMemory.Clear(p.Shadow, (nuint)(this.Pitch * this.Height));
                p.CursorsPerPlane.AsSpan().Clear();
                p.MarkDirty(0, 0, this.Width, this.Height);
            }

            this._current = this.FirstPlane;
            this._frontier = this.FirstPlane + this.PlaneStep;
        }
    }

    /// <summary>
    /// Empties one plane, and allocates glyphs in it from now on. Between frames, as for <see cref="Clear"/>; the page is
    /// uploaded whole.
    /// </summary>
    public void ClearPlane(int page, int plane)
    {
        Misc.DebugAssertMainThread();
        lock (this._sync)
        {
            var p = this._pages[page];
            if (this.Subpixel)
            {
                NativeMemory.Clear(p.Shadow, (nuint)(this.Pitch * this.Height));
            }
            else
            {
                for (var row = 0; row < this.Height; row++)
                {
                    var d = p.Shadow + (row * this.Pitch);
                    for (var col = 0; col < this.Width; col++)
                        this.SetChannel(d + (col * this.BytesPerPixel), plane, 0);
                }
            }

            p.CursorsPerPlane[plane] = default;
            p.MarkDirty(0, 0, this.Width, this.Height);
            this._current = (page * PlanesPerPage) + plane;
        }
    }

    /// <summary>
    /// Reserves a <paramref name="width"/> x <paramref name="height"/> rectangle in the current plane, or the next one not
    /// used yet (adding a page if needed). Returns the page, the plane (channel index) and the top-left corner inside the
    /// padding; false if neither has room.
    /// </summary>
    public bool TryAllocate(int width, int height, out int page, out int plane, out int x, out int y)
    {
        page = plane = x = y = 0;
        var w = width + (2 * Padding);
        var h = height + (2 * Padding);
        if (!this.CanHold(width, height))
            return false;

        if (!this.TryEnsurePages(1))
            return false;

        while (true)
        {
            int needed;
            lock (this._sync)
            {
                page = this._current / PlanesPerPage;
                plane = this._current % PlanesPerPage;
                ref var s = ref this._pages[page].CursorsPerPlane[plane];
                if (s.X + w > this.Width)
                    s = new() { Y = s.Y + s.Height };
                if (s.Y + h <= this.Height)
                {
                    x = s.X + Padding;
                    y = s.Y + Padding;
                    s.X += w;
                    s.Height = Math.Max(s.Height, h);
                    return true;
                }

                // The current plane is full: on to one not used yet.
                if (this._frontier >= this.MaxPlanes)
                    return false;
                needed = (this._frontier / PlanesPerPage) + 1;
                if (needed <= this._pages.Count)
                {
                    this._current = this._frontier;
                    this._frontier += this.PlaneStep;
                    continue;
                }
            }

            // Added outside the lock, as adding a page raises PageAdded.
            if (!this.TryEnsurePages(needed))
                return false;
        }
    }

    /// <summary>
    /// Writes 8-bit coverage into a plane of a page, at <paramref name="alphaX"/> in the <paramref name="width"/> by
    /// <paramref name="height"/> box at (x, y); coverage past the box's right edge is cut off. Marks the box for upload.
    /// In a subpixel atlas, <paramref name="lcd"/> (3 bytes a pixel, same layout) fills R, G and B; without it they get alpha.
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
        lock (this._sync)
        {
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
    }

    /// <summary>Reads back the 8-bit coverage of a <paramref name="width"/> by <paramref name="height"/> box of a plane.</summary>
    public byte[] Read(int page, int plane, int x, int y, int width, int height)
    {
        var alpha = new byte[width * height];
        lock (this._sync)
        {
            var src = this._pages[page].Shadow + (y * this.Pitch) + (x * this.BytesPerPixel);
            for (var row = 0; row < height; row++)
            {
                var s = src + (row * this.Pitch);
                for (var col = 0; col < width; col++)
                    alpha[(row * width) + col] = this.GetChannel(s + (col * this.BytesPerPixel), plane);
            }
        }

        return alpha;
    }

    /// <summary>Reads back R, G, B (3 bytes a pixel) of a box of a subpixel atlas.</summary>
    public byte[] ReadLcd(int page, int x, int y, int width, int height)
    {
        var lcd = new byte[width * height * 3];
        lock (this._sync)
        {
            var src = this._pages[page].Shadow + (y * this.Pitch) + (x * this.BytesPerPixel);
            for (var row = 0; row < height; row++)
            {
                var s = src + (row * this.Pitch);
                for (var col = 0; col < width; col++)
                {
                    for (var c = 0; c < 3; c++)
                        lcd[(((row * width) + col) * 3) + c] = this.GetChannel(s + (col * this.BytesPerPixel), c);
                }
            }
        }

        return lcd;
    }

    /// <summary>Copies the changed rectangle of each page to its texture. On the thread that calls Present.</summary>
    public void Upload()
    {
        Misc.DebugAssertMainThread();
        lock (this._sync)
        {
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
    }

    public void Dispose()
    {
        lock (this._sync)
        {
            foreach (var p in this._pages)
                p.Dispose();
            this._pages.Clear();
            this._context.Dispose();
        }
    }

    /// <summary>Adds pages until there are at least <paramref name="count"/>; false if there can't be that many.</summary>
    public bool TryEnsurePages(int count)
    {
        if (count > this.MaxPages)
            return false;

        while (true)
        {
            int added;
            lock (this._sync)
                added = this._pages.Count;
            if (added >= count)
                return true;

            var page = new Page(this.Width, this.Height, $"FontChanger.DalamudPlugin {this.Name} page {added}", this.Format4444);
            lock (this._sync)
            {
                // Another thread added one meanwhile.
                if (this._pages.Count != added)
                {
                    page.Dispose();
                    continue;
                }

                this._pages.Add(page);
                if (!this._context)
                    this._context = page.Resource.Value->GetImmediateContext();
            }

            Plugin.Log.Information("Added {name} atlas page {n} of {max} ({width} x {height})", this.Name, added + 1, this.MaxPages, this.Width, this.Height);
            this.PageAdded?.Invoke();
        }
    }

    private sealed class Page : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly KernelTextureWrapper _texture;

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
}
