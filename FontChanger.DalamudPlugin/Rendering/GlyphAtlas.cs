using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

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
    /// <summary>
    /// Makes an atlas of pages of a size (sides up to 4096: glyph positions take 12 bits; an odd width for a subpixel
    /// atlas), of at most <paramref name="maxPages"/> pages.
    /// </summary>
    public GlyphAtlas(int width, int height, int maxPages, string name, bool subpixel, AtlasTextureFormat format = AtlasTextureFormat.Bgra8888)
    {
        this.Width = width;
        this.Height = height;
        this.MaxPages = Math.Clamp(maxPages, 1, MaxTextureSlots);
        this.Name = name;
        this.Subpixel = subpixel;
        this.Format4444 = format == AtlasTextureFormat.Bgra4444;
        this.Texel = this.Format4444 ? 2 : 4;
        this._current = this.FirstPlane;
        this._frontier = this.FirstPlane + this.PlaneStep;
    }

    public int Width { get; }

    public int Height { get; }

    public string Name { get; }

    public bool Subpixel { get; }

    /// <summary>Gets the number of texture slots of a font, the most pages an atlas can have.</summary>
    public const int MaxTextureSlots = AtkFontManager.Font.MaxTextures;

    public int MaxPages { get; }

    /// <summary>Gets whether pages are B4G4R4A4, not B8G8R8A8.</summary>
    public bool Format4444 { get; }

    /// <summary>Gets the bytes a texel takes.</summary>
    private int Texel { get; }

    private int Pitch => this.Width * this.Texel;

    // Glyph rectangles are padded by this much on each side: the edge and outline passes sample a texel around a glyph.
    private const int Padding = 1;

    // Byte offset in a B8G8R8A8 texel of the channel a glyph's plane index selects (the shader masks R, G, B, A).
    private static readonly int[] ChannelOffsets = [2, 1, 0, 3];

    // The bit shift of the same in a B4G4R4A4 texel.
    private static readonly int[] ChannelShifts = [8, 4, 0, 12];

    // 8-bit coverage as 4 bits, rounded; read back as 17 times that.
    private static readonly byte[] Quantized = Enumerable.Range(0, 256).Select(v => (byte)(((v * 15) + 127) / 255)).ToArray();

    private void SetChannel(byte* texel, int plane, byte value)
    {
        if (!this.Format4444)
        {
            texel[ChannelOffsets[plane]] = value;
            return;
        }

        var shift = ChannelShifts[plane];
        var t = (ushort*)texel;
        *t = (ushort)((*t & ~(0xF << shift)) | (Quantized[value] << shift));
    }

    private byte GetChannel(byte* texel, int plane) =>
        this.Format4444 ? (byte)(((*(ushort*)texel >> ChannelShifts[plane]) & 0xF) * 17) : texel[ChannelOffsets[plane]];

    /// <summary>The planes of a page: one per channel.</summary>
    public const int PlanesPerPage = 4;

    public const int SubpixelPlane = 3;

    private int FirstPlane => this.Subpixel ? SubpixelPlane : 0;

    private int PlaneStep => this.Subpixel ? PlanesPerPage : 1;

    private readonly List<Page> _pages = [];
    private readonly object _sync = new();
    private ComPtr<ID3D11DeviceContext> _context;

    // Planes are numbered page * PlanesPerPage + plane. Glyphs are allocated in the current one; those from the frontier
    // on haven't been used yet (since Clear), the others before it are full.
    private int _current;
    private int _frontier;

    /// <summary>Raised on the thread that allocates, after a page was added.</summary>
    public event Action? PageAdded;

    public int PageCount => this._pages.Count;

    public KernelTexture* GetKernelTexture(int page) => this._pages[page].Kernel;

    /// <summary>Adds the first page if there is none.</summary>
    public void EnsurePage()
    {
        if (!this.TryEnsurePages(1))
            throw new InvalidOperationException("Could not add an atlas page.");
    }

    /// <summary>Gets the number of planes there can be.</summary>
    public int MaxPlanes => this.MaxPages * PlanesPerPage;

    /// <summary>Gets the area a glyph's box takes in a plane, with its padding.</summary>
    public static int PaddedArea(int width, int height) => (width + (2 * Padding)) * (height + (2 * Padding));

    /// <summary>Gets whether a glyph of this size, with its padding, fits in one empty plane.</summary>
    public bool CanHold(int width, int height) => width + (2 * Padding) <= this.Width && height + (2 * Padding) <= this.Height;

    /// <summary>
    /// Empties every page, keeping the textures (fonts refer to them). Framework thread, between frames: no glyph
    /// referring to the old contents may be drawn afterwards.
    /// </summary>
    public void Clear()
    {
        lock (this._sync)
        {
            foreach (var p in this._pages)
            {
                NativeMemory.Clear(p.Shadow, (nuint)(this.Pitch * this.Height));
                p.Shelves.AsSpan().Clear();
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
                        this.SetChannel(d + (col * this.Texel), plane, 0);
                }
            }

            p.Shelves[plane] = default;
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
                ref var s = ref this._pages[page].Shelves[plane];
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
            if (this.Subpixel)
            {
                var hasLcd = !lcd.IsEmpty && lcd.Length >= alpha.Length * 3;
                var dst = p.Shadow + (y * this.Pitch) + (x * this.Texel);
                for (var row = 0; row < height; row++)
                {
                    var d = dst + (row * this.Pitch) + (alphaX * this.Texel);
                    for (var col = 0; col < columns; col++)
                    {
                        var i = (row * alphaStride) + col;
                        var texel = d + (col * this.Texel);
                        this.SetChannel(texel, SubpixelPlane, alpha[i]);
                        for (var c = 0; c < 3; c++)
                            this.SetChannel(texel, c, hasLcd ? lcd[(i * 3) + c] : alpha[i]);
                    }
                }
            }
            else
            {
                var dst = p.Shadow + (y * this.Pitch) + (x * this.Texel);
                for (var row = 0; row < height; row++)
                {
                    var src = alpha.Slice(row * alphaStride, alphaStride);
                    var d = dst + (row * this.Pitch) + (alphaX * this.Texel);
                    for (var col = 0; col < columns; col++)
                        this.SetChannel(d + (col * this.Texel), plane, src[col]);
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
            var src = this._pages[page].Shadow + (y * this.Pitch) + (x * this.Texel);
            for (var row = 0; row < height; row++)
            {
                var s = src + (row * this.Pitch);
                for (var col = 0; col < width; col++)
                    alpha[(row * width) + col] = this.GetChannel(s + (col * this.Texel), plane);
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
            var src = this._pages[page].Shadow + (y * this.Pitch) + (x * this.Texel);
            for (var row = 0; row < height; row++)
            {
                var s = src + (row * this.Pitch);
                for (var col = 0; col < width; col++)
                {
                    for (var c = 0; c < 3; c++)
                        lcd[(((row * width) + col) * 3) + c] = this.GetChannel(s + (col * this.Texel), c);
                }
            }
        }

        return lcd;
    }

    /// <summary>Copies the changed rectangle of each page to its texture. On the thread that calls Present.</summary>
    public void Upload()
    {
        lock (this._sync)
        {
            if (this._context.IsNull)
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
                this._context.Get()->UpdateSubresource(
                    p.Resource,
                    0,
                    &box,
                    p.Shadow + (p.DirtyTop * this.Pitch) + (p.DirtyLeft * this.Texel),
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
    private bool TryEnsurePages(int count)
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
                if (this._context.IsNull)
                {
                    using var device = default(ComPtr<ID3D11Device>);
                    page.Resource->GetDevice(device.GetAddressOf());
                    device.Get()->GetImmediateContext(this._context.GetAddressOf());
                }
            }

            Plugin.Log.Information("Added {name} atlas page {n} of {max} ({width} x {height})", this.Name, added + 1, this.MaxPages, this.Width, this.Height);
            this.PageAdded?.Invoke();
        }
    }

    /// <summary>A plane's shelf: glyphs go left to right from (X, Y), and the next shelf starts below the tallest.</summary>
    private struct Shelf
    {
        public int X;
        public int Y;
        public int Height;
    }

    private sealed class Page : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private readonly GameTextureWrapper _texture;

        public Page(int width, int height, string name, bool format4444)
        {
            this._width = width;
            this._height = height;
            this.Shadow = (byte*)NativeMemory.AllocZeroed((nuint)(width * (format4444 ? 2 : 4) * height));
            try
            {
                this._texture = new GameTextureWrapper(width, height, name, format4444);
            }
            catch
            {
                NativeMemory.Free(this.Shadow);
                throw;
            }

            // The texture's contents are undefined until written: upload the cleared copy once.
            this.MarkDirty(0, 0, width, height);
        }

        /// <summary>Gets the page as a Kernel::Texture*.</summary>
        public KernelTexture* Kernel => this._texture.Kernel;

        public ID3D11Resource* Resource => this._texture.Resource;

        public byte* Shadow { get; private set; }

        /// <summary>The shelf glyphs are put on in each plane.</summary>
        public Shelf[] Shelves { get; } = new Shelf[PlanesPerPage];

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
    }
}
