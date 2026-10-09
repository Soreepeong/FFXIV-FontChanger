using System;
using System.Collections.Generic;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.D3D11_CPU_ACCESS_FLAG;
using static TerraFX.Interop.DirectX.D3D11_MAP;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace CustomFonts;

/// <summary>
/// Reads glyph coverage out of the game's font textures on the GPU, through a staging texture per texture format. On the
/// thread that calls Present.
/// </summary>
internal sealed unsafe class GameTextureReader : IDisposable
{
    // A game glyph's fields are bytes, so a glyph fits.
    private const int StagingSize = 256;

    // A plane's bit shift in a B4G4R4A4 texel, and its byte offset in a B8G8R8A8 or R8G8B8A8 texel: planes are the R, G,
    // B and A channels, as in the atlas.
    private static readonly int[] Shifts4444 = [8, 4, 0, 12];
    private static readonly int[] OffsetsBgra = [2, 1, 0, 3];
    private static readonly int[] OffsetsRgba = [0, 1, 2, 3];

    private readonly Dictionary<DXGI_FORMAT, nint> staging = [];
    private ID3D11DeviceContext* context;

    /// <summary>Gets whether a texture's pixels can be read: it has a format game font textures use.</summary>
    public static bool CanRead(nint texture)
    {
        var resource = (ID3D11Texture2D*)GameUi.GetD3D11Texture(texture);
        if (resource is null)
            return false;
        D3D11_TEXTURE2D_DESC desc;
        resource->GetDesc(&desc);
        return BytesPerTexel(desc.Format) != 0;
    }

    /// <summary>Reads a rectangle of a plane of a texture as 8-bit coverage.</summary>
    public byte[] Read(nint texture, int x, int y, int width, int height, int plane)
    {
        var alpha = new byte[width * height];
        var stride = width;
        var resource = (ID3D11Texture2D*)GameUi.GetD3D11Texture(texture);
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
        this.context->CopySubresourceRegion(staging, 0, 0, 0, 0, (ID3D11Resource*)resource, 0, &box);

        D3D11_MAPPED_SUBRESOURCE mapped;
        this.context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped).ThrowOnError();
        try
        {
            for (var row = 0; row < height; row++)
            {
                var src = (byte*)mapped.pData + (row * mapped.RowPitch);
                var dst = alpha.AsSpan(row * stride, width);
                if (bytes == 2)
                {
                    var shift = Shifts4444[plane];
                    for (var col = 0; col < width; col++)
                        dst[col] = (byte)(((((ushort*)src)[col] >> shift) & 0xF) * 17);
                }
                else
                {
                    var offset = (desc.Format is DXGI_FORMAT_R8G8B8A8_UNORM ? OffsetsRgba : OffsetsBgra)[plane];
                    for (var col = 0; col < width; col++)
                        dst[col] = src[(col * 4) + offset];
                }
            }
        }
        finally
        {
            this.context->Unmap(staging, 0);
        }

        return alpha;
    }

    public void Dispose()
    {
        foreach (var texture in this.staging.Values)
            ((ID3D11Texture2D*)texture)->Release();
        this.staging.Clear();
        if (this.context is not null)
        {
            this.context->Release();
            this.context = null;
        }
    }

    private static int BytesPerTexel(DXGI_FORMAT format) => format switch
    {
        DXGI_FORMAT_B4G4R4A4_UNORM => 2,
        DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT_R8G8B8A8_UNORM => 4,
        _ => 0,
    };

    private ID3D11Resource* GetStaging(ID3D11Texture2D* source, DXGI_FORMAT format)
    {
        if (this.staging.TryGetValue(format, out var existing))
            return (ID3D11Resource*)existing;

        ID3D11Device* device;
        source->GetDevice(&device);
        try
        {
            if (this.context is null)
            {
                ID3D11DeviceContext* ctx;
                device->GetImmediateContext(&ctx);
                this.context = ctx;
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
            ID3D11Texture2D* texture;
            device->CreateTexture2D(&desc, null, &texture).ThrowOnError();
            this.staging.Add(format, (nint)texture);
            return (ID3D11Resource*)texture;
        }
        finally
        {
            device->Release();
        }
    }
}
