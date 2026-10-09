using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace CustomFonts;

/// <summary>
/// Atlas pages for the rasterized glyphs, shared by every replaced font. A page is a B8G8R8A8 texture holding four glyph
/// planes, one per channel, as the game's own atlases do; glyphs are written to a CPU copy, and the changed rectangle
/// goes to the GPU from <see cref="Upload"/>.
/// </summary>
/// <remarks>
/// Pages are as wide as the game's atlases (4096), because FontEdgeVS and FontGlareVS take their texel step from the
/// font's texture width, which a replaced font shares between the game's textures and these. Pages take texture indices
/// <see cref="FirstTextureIndex"/> and up in every replaced font; the game's own textures stay below, for the glyphs
/// left to the game (the private-use icons).
/// </remarks>
internal sealed unsafe class GlyphAtlas : IDisposable
{
    public const int Size = 4096;

    /// <summary>The texture index of the first page in a replaced font: the game's fonts use 7 textures.</summary>
    public const int FirstTextureIndex = 7;

    public const int MaxPages = GameFont.MaxTextures - FirstTextureIndex;

    private const int Pitch = Size * 4;

    // Glyph rectangles are padded by this much on each side: the edge and outline passes sample a texel around a glyph.
    private const int Padding = 1;

    // Byte offset in a B8G8R8A8 texel of the channel a glyph's plane index selects (the shader masks R, G, B, A).
    private static readonly int[] ChannelOffsets = [2, 1, 0, 3];

    private readonly List<Page> pages = [];
    private readonly object sync = new();
    private ID3D11DeviceContext* context;

    // The page glyphs are allocated in; the ones after it are empty (after Clear).
    private int current;

    /// <summary>Raised on the thread that allocates, after a page was added.</summary>
    public event Action? PageAdded;

    public int PageCount => this.pages.Count;

    public Texture* GetKernelTexture(int page) => this.pages[page].Kernel;

    /// <summary>
    /// Adds the first page if there is none. Glyphs refer to page texture indices, and the renderer only has vertex
    /// buffers for indices below a font's texture count, so the page must be there before the first glyph is.
    /// </summary>
    public void EnsurePage()
    {
        if (this.pages.Count == 0 && !this.TryAddPage())
            throw new InvalidOperationException("Could not add an atlas page.");
    }

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
                p.Plane = p.ShelfX = p.ShelfY = p.ShelfHeight = 0;
                p.MarkDirty(0, 0, Size, Size);
            }

            this.current = 0;
        }
    }

    /// <summary>
    /// Reserves a <paramref name="width"/> x <paramref name="height"/> rectangle, adding a page if needed. Returns the
    /// page, the plane (channel index) and the top-left corner inside the padding.
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

        var p = this.pages[this.current];
        if (p.ShelfX + w > Size)
        {
            p.ShelfY += p.ShelfHeight;
            p.ShelfX = 0;
            p.ShelfHeight = 0;
        }

        if (p.ShelfY + h > Size)
        {
            p.ShelfX = p.ShelfY = p.ShelfHeight = 0;
            if (++p.Plane == 4)
            {
                if (this.current + 1 == this.pages.Count && !this.TryAddPage())
                    return false;
                p = this.pages[++this.current];
            }
        }

        page = this.current;
        plane = p.Plane;
        x = p.ShelfX + Padding;
        y = p.ShelfY + Padding;
        p.ShelfX += w;
        p.ShelfHeight = Math.Max(p.ShelfHeight, h);
        return true;
    }

    /// <summary>Writes 8-bit coverage into a plane of a page at (x, y), and marks it for upload.</summary>
    public void Write(int page, int plane, int x, int y, int width, int height, ReadOnlySpan<byte> alpha, int alphaStride, int alphaX)
    {
        var p = this.pages[page];
        lock (this.sync)
        {
            var dst = p.Shadow + (y * Pitch) + (x * 4) + ChannelOffsets[plane];
            for (var row = 0; row < height; row++)
            {
                var src = alpha.Slice(row * alphaStride, alphaStride);
                var d = dst + (row * Pitch) + (alphaX * 4);
                for (var col = 0; col < alphaStride; col++)
                    d[col * 4] = src[col];
            }

            p.MarkDirty(x, y, x + width, y + height);
        }
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
                    p.Shadow + (p.DirtyTop * Pitch) + (p.DirtyLeft * 4),
                    Pitch,
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

        var page = new Page(this.pages.Count);
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

        Plugin.Log.Information("Added atlas page {n}", this.pages.Count);
        this.PageAdded?.Invoke();
        return true;
    }

    private sealed class Page : IDisposable
    {
        public Page(int index)
        {
            this.Shadow = (byte*)NativeMemory.AllocZeroed((nuint)(Pitch * Size));
            this.Wrap = Plugin.TextureProvider.CreateEmpty(
                RawImageSpecification.Bgra32(Size, Size), false, false, $"CustomFonts page {index}");
            this.Kernel = (Texture*)Plugin.TextureProvider.ConvertToKernelTexture(this.Wrap, leaveWrapOpen: true);

            ID3D11ShaderResourceView* srv;
            var iid = IID.IID_ID3D11ShaderResourceView;
            ((IUnknown*)this.Wrap.Handle.Handle)->QueryInterface(&iid, (void**)&srv).ThrowOnError();
            ID3D11Resource* res;
            srv->GetResource(&res);
            srv->Release();
            this.Resource = res;

            // The texture's contents are undefined until written: upload the cleared copy once.
            this.MarkDirty(0, 0, Size, Size);
        }

        public IDalamudTextureWrap Wrap { get; }

        public Texture* Kernel { get; }

        public ID3D11Resource* Resource { get; private set; }

        public byte* Shadow { get; private set; }

        public int Plane { get; set; }

        public int ShelfX { get; set; }

        public int ShelfY { get; set; }

        public int ShelfHeight { get; set; }

        public int DirtyLeft { get; private set; } = Size;

        public int DirtyTop { get; private set; } = Size;

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
            this.DirtyLeft = this.DirtyTop = Size;
            this.DirtyRight = this.DirtyBottom = 0;
        }

        public void Dispose()
        {
            // The game releases a kernel texture some frames after its last reference goes (it is a delayed-release
            // resource), so the frames still in flight can keep sampling it.
            this.Kernel->DecRef();
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
