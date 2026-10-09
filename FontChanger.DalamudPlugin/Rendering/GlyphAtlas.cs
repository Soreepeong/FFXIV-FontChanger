using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;


using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace CustomFonts;

/// <summary>
/// Atlas pages for the rasterized glyphs of a game font family's replaced fonts. A page is a square B8G8R8A8 texture
/// holding four glyph planes, one per channel, as the game's own atlases do; glyphs are written to a CPU copy, and the
/// changed rectangle goes to the GPU from <see cref="Upload"/>.
/// </summary>
/// <remarks>
/// The font shaders take UVs as texels over the bound texture's size, so pages may be smaller than the game's atlases;
/// only the edge and glare shaders' texel step comes from the font's claimed texture width, which a replaced font sets
/// from its atlas's size. Pages take texture indices <see cref="FirstTextureIndex"/> and up in every replaced font; the
/// game's own textures stay below, for the glyphs left to the game (the private-use icons).
/// </remarks>
internal sealed unsafe class GlyphAtlas : IDisposable
{
    /// <summary>Makes an atlas of square pages of a side length (up to 4096: glyph positions take 12 bits).</summary>
    public GlyphAtlas(int size, string name)
    {
        this.Size = size;
        this.Name = name;
    }

    /// <summary>Gets the pages' side length.</summary>
    public int Size { get; }

    public string Name { get; }

    /// <summary>The texture index of the first page in a replaced font: the game's fonts use 7 textures.</summary>
    public const int FirstTextureIndex = 7;

    public static int MaxPages => GameFont.MaxTextures - FirstTextureIndex;

    private int Pitch => this.Size * 4;

    // Glyph rectangles are padded by this much on each side: the edge and outline passes sample a texel around a glyph.
    private const int Padding = 1;

    // Byte offset in a B8G8R8A8 texel of the channel a glyph's plane index selects (the shader masks R, G, B, A).
    private static readonly int[] ChannelOffsets = [2, 1, 0, 3];

    /// <summary>The planes of a page: one per channel.</summary>
    public const int PlanesPerPage = 4;

    private readonly List<Page> pages = [];
    private readonly object sync = new();
    private ID3D11DeviceContext* context;

    // Planes are numbered page * PlanesPerPage + plane. Glyphs are allocated in the current one; those from the frontier
    // on haven't been used yet (since Clear), the others before it are full.
    private int current;
    private int frontier = 1;

    /// <summary>Raised on the thread that allocates, after a page was added.</summary>
    public event Action? PageAdded;

    public int PageCount => this.pages.Count;

    public nint GetKernelTexture(int page) => this.pages[page].Kernel;

    /// <summary>
    /// Adds the first page if there is none. Glyphs refer to page texture indices, and the renderer only has vertex
    /// buffers for indices below a font's texture count, so the page must be there before the first glyph is.
    /// </summary>
    public void EnsurePage()
    {
        if (this.pages.Count == 0 && !this.TryAddPage())
            throw new InvalidOperationException("Could not add an atlas page.");
    }

    /// <summary>Gets the number of planes there can be.</summary>
    public static int MaxPlanes => MaxPages * PlanesPerPage;

    /// <summary>Gets the area a glyph's box takes in a plane, with its padding.</summary>
    public static int PaddedArea(int width, int height) => (width + (2 * Padding)) * (height + (2 * Padding));

    /// <summary>
    /// Empties every page, keeping the textures (fonts refer to them). Framework thread, between frames: no glyph
    /// referring to the old contents may be drawn afterwards.
    /// </summary>
    public void Clear()
    {
        lock (this.sync)
        {
            foreach (var p in this.pages)
            {
                NativeMemory.Clear(p.Shadow, (nuint)(Pitch * Size));
                p.Shelves.AsSpan().Clear();
                p.MarkDirty(0, 0, Size, Size);
            }

            this.current = 0;
            this.frontier = 1;
        }
    }

    /// <summary>
    /// Empties one plane, and allocates glyphs in it from now on. Between frames, as for <see cref="Clear"/>; the page is
    /// uploaded whole.
    /// </summary>
    public void ClearPlane(int page, int plane)
    {
        lock (this.sync)
        {
            var p = this.pages[page];
            var channel = ChannelOffsets[plane];
            for (var row = 0; row < Size; row++)
            {
                var d = p.Shadow + (row * Pitch) + channel;
                for (var col = 0; col < Size; col++)
                    d[col * 4] = 0;
            }

            p.Shelves[plane] = default;
            p.MarkDirty(0, 0, Size, Size);
            this.current = (page * PlanesPerPage) + plane;
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
        if (w > Size || h > Size)
            return false;

        if (this.pages.Count == 0 && !this.TryAddPage())
            return false;

        while (true)
        {
            page = this.current / PlanesPerPage;
            plane = this.current % PlanesPerPage;
            ref var s = ref this.pages[page].Shelves[plane];
            if (s.X + w > Size)
                s = new() { Y = s.Y + s.Height };
            if (s.Y + h <= Size)
            {
                x = s.X + Padding;
                y = s.Y + Padding;
                s.X += w;
                s.Height = Math.Max(s.Height, h);
                return true;
            }

            // The current plane is full: on to one not used yet.
            if (this.frontier >= MaxPlanes)
                return false;
            if (this.frontier / PlanesPerPage >= this.pages.Count && !this.TryAddPage())
                return false;
            this.current = this.frontier++;
        }
    }

    /// <summary>
    /// Writes 8-bit coverage into a plane of a page, at <paramref name="alphaX"/> in the <paramref name="width"/> by
    /// <paramref name="height"/> box at (x, y); coverage past the box's right edge is cut off. Marks the box for upload.
    /// </summary>
    public void Write(int page, int plane, int x, int y, int width, int height, ReadOnlySpan<byte> alpha, int alphaStride, int alphaX)
    {
        var p = this.pages[page];
        var columns = Math.Min(alphaStride, width - alphaX);
        lock (this.sync)
        {
            var dst = p.Shadow + (y * Pitch) + (x * 4) + ChannelOffsets[plane];
            for (var row = 0; row < height; row++)
            {
                var src = alpha.Slice(row * alphaStride, alphaStride);
                var d = dst + (row * Pitch) + (alphaX * 4);
                for (var col = 0; col < columns; col++)
                    d[col * 4] = src[col];
            }

            p.MarkDirty(x, y, x + width, y + height);
        }
    }

    /// <summary>Reads back the 8-bit coverage of a <paramref name="width"/> by <paramref name="height"/> box of a plane.</summary>
    public byte[] Read(int page, int plane, int x, int y, int width, int height)
    {
        var alpha = new byte[width * height];
        lock (this.sync)
        {
            var src = this.pages[page].Shadow + (y * Pitch) + (x * 4) + ChannelOffsets[plane];
            for (var row = 0; row < height; row++)
            {
                var s = src + (row * Pitch);
                for (var col = 0; col < width; col++)
                    alpha[(row * width) + col] = s[col * 4];
            }
        }

        return alpha;
    }

    /// <summary>Copies the changed rectangle of each page to its texture. On the thread that calls Present.</summary>
    public void Upload()
    {
        if (this.context is null)
            return;
        lock (this.sync)
        {
            foreach (var p in this.pages)
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
                this.context->UpdateSubresource(
                    p.Resource,
                    0,
                    &box,
                    p.Shadow + (p.DirtyTop * this.Pitch) + (p.DirtyLeft * 4),
                    (uint)this.Pitch,
                    0);
                p.ClearDirty();
            }
        }
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            foreach (var p in this.pages)
                p.Dispose();
            this.pages.Clear();
            if (this.context is not null)
            {
                this.context->Release();
                this.context = null;
            }
        }
    }

    private bool TryAddPage()
    {
        if (this.pages.Count >= MaxPages)
            return false;

        var page = new Page(this.Size, $"CustomFonts {this.Name} page {this.pages.Count}");
        lock (this.sync)
        {
            this.pages.Add(page);
            if (this.context is null)
            {
                ID3D11Device* device;
                page.Resource->GetDevice(&device);
                ID3D11DeviceContext* ctx;
                device->GetImmediateContext(&ctx);
                device->Release();
                this.context = ctx;
            }
        }

        Plugin.Log.Information("Added {name} atlas page {n} ({size} x {size})", this.Name, this.pages.Count, this.Size);
        this.PageAdded?.Invoke();
        return true;
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
        private readonly int size;

        public Page(int size, string name)
        {
            this.size = size;
            this.DirtyLeft = this.DirtyTop = size;
            this.Shadow = (byte*)NativeMemory.AllocZeroed((nuint)(size * 4 * size));
            this.Wrap = Plugin.TextureProvider.CreateEmpty(RawImageSpecification.Bgra32(size, size), false, false, name);
            this.Kernel = Plugin.TextureProvider.ConvertToKernelTexture(this.Wrap, leaveWrapOpen: true);

            ID3D11ShaderResourceView* srv;
            var iid = IID.IID_ID3D11ShaderResourceView;
            ((IUnknown*)this.Wrap.Handle.Handle)->QueryInterface(&iid, (void**)&srv).ThrowOnError();
            ID3D11Resource* res;
            srv->GetResource(&res);
            srv->Release();
            this.Resource = res;

            // The texture's contents are undefined until written: upload the cleared copy once.
            this.MarkDirty(0, 0, size, size);
        }

        public IDalamudTextureWrap Wrap { get; }

        /// <summary>Gets the page as a Kernel::Texture*.</summary>
        public nint Kernel { get; }

        public ID3D11Resource* Resource { get; private set; }

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
            this.DirtyLeft = this.DirtyTop = this.size;
            this.DirtyRight = this.DirtyBottom = 0;
        }

        public void Dispose()
        {
            // The game releases a kernel texture some frames after its last reference goes (it is a delayed-release
            // resource), so the frames still in flight can keep sampling it. DecRef is ReferencedClassBase's vf3, as it
            // has always been; no code site pins it down well enough for a signature.
            ((delegate* unmanaged<nint, void>)(*(nint**)this.Kernel)[3])(this.Kernel);
            if (this.Resource is not null)
            {
                this.Resource->Release();
                this.Resource = null;
            }

            this.Wrap.Dispose();
            if (this.Shadow is not null)
            {
                NativeMemory.Free(this.Shadow);
                this.Shadow = null;
            }
        }
    }
}
