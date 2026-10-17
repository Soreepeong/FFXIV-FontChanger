using System;
using System.Collections.Generic;

using FontChanger.DalamudPlugin.Rendering;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;

using static TerraFX.Interop.DirectX.D3D11_CPU_ACCESS_FLAG;
using static TerraFX.Interop.DirectX.D3D11_MAP;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace FontChanger.DalamudPlugin.Game;

/// <summary>
/// Reads glyph coverage out of the game's font textures on the GPU, through a staging texture per texture format. On the
/// thread that calls Present.
/// </summary>
internal sealed unsafe class TextureReader : IDisposable
{
    // A game glyph's fields are bytes, so a glyph fits.
    private const int StagingSize = 256;

    private readonly Dictionary<DXGI_FORMAT, MyComPtr<ID3D11Texture2D>> _staging = [];
    private MyComPtr<ID3D11DeviceContext> _context;

    /// <summary>Gets whether a texture's pixels can be read: it has a format game font textures use.</summary>
    public static bool CanRead(KernelTexture* texture)
    {
        var resource = (ID3D11Texture2D*)texture->D3D11Texture2D;
        if (resource is null)
            return false;
        D3D11_TEXTURE2D_DESC desc;
        resource->GetDesc(&desc);
        return BytesPerTexel(desc.Format) != 0;
    }

    /// <summary>Reads a rectangle of a plane of a texture as 8-bit coverage.</summary>
    public byte[] Read(KernelTexture* texture, int x, int y, int width, int height, int plane)
    {
        var alpha = new byte[width * height];
        var stride = width;
        var resource = (ID3D11Texture2D*)texture->D3D11Texture2D;
        D3D11_TEXTURE2D_DESC desc;
        resource->GetDesc(&desc);
        width = Math.Min(width, (int)desc.Width - x);
        height = Math.Min(height, (int)desc.Height - y);
        var bytes = BytesPerTexel(desc.Format);
        if (width <= 0 || height <= 0 || width > StagingSize || height > StagingSize || bytes == 0)
            return alpha;

        var staging = this.GetStaging(resource, desc.Format);
        var box = new D3D11_BOX
        {
            left = (uint)x,
            top = (uint)y,
            front = 0,
            right = (uint)(x + width),
            bottom = (uint)(y + height),
            back = 1,
        };
        this._context.Value->CopySubresourceRegion(staging, 0, 0, 0, 0, (ID3D11Resource*)resource, 0, &box);

        D3D11_MAPPED_SUBRESOURCE mapped;
        this._context.Value->Map(staging, 0, D3D11_MAP_READ, 0, &mapped).ThrowOnError();
        try
        {
            for (var row = 0; row < height; row++)
            {
                var src = (byte*)mapped.pData + (row * mapped.RowPitch);
                var dst = alpha.AsSpan(row * stride, width);
                if (bytes == 2)
                {
                    for (var col = 0; col < width; col++)
                        dst[col] = GlyphAtlas.Get4444(((ushort*)src)[col], plane);
                }
                else
                {
                    var offset = desc.Format is DXGI_FORMAT_R8G8B8A8_UNORM ? plane : GlyphAtlas.BgraOffset(plane);
                    for (var col = 0; col < width; col++)
                        dst[col] = src[(col * 4) + offset];
                }
            }
        }
        finally
        {
            this._context.Value->Unmap(staging, 0);
        }

        return alpha;
    }

    public void Dispose()
    {
        foreach (var texture in this._staging.Values)
            texture.Value->Release();
        this._staging.Clear();
        this._context.Dispose();
    }

    private static int BytesPerTexel(DXGI_FORMAT format) => format switch
    {
        DXGI_FORMAT_B4G4R4A4_UNORM => 2,
        DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT_R8G8B8A8_UNORM => 4,
        _ => 0,
    };

    private ID3D11Resource* GetStaging(ID3D11Texture2D* source, DXGI_FORMAT format)
    {
        if (this._staging.TryGetValue(format, out var existing))
            return (ID3D11Resource*)existing.Value;

        using var device = source->GetDevice();
        if (!this._context)
        {
            fixed (ID3D11DeviceContext** context = this._context)
                device.Value->GetImmediateContext(context);
        }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = StagingSize,
            Height = StagingSize,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new(1, 0),
            Usage = D3D11_USAGE_STAGING,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_READ,
        };
        var texture = default(MyComPtr<ID3D11Texture2D>);
        device.Value->CreateTexture2D(&desc, null, texture.GetAddressOf()).ThrowOnError();
        this._staging.Add(format, texture);
        return (ID3D11Resource*)texture.Value;
    }
}
