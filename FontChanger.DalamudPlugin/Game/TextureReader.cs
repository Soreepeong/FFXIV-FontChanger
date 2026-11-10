using System;
using System.Collections.Generic;

using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;

using static TerraFX.Interop.DirectX.D3D11_CPU_ACCESS_FLAG;
using static TerraFX.Interop.DirectX.D3D11_MAP;
using static TerraFX.Interop.DirectX.D3D11_USAGE;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace FontChanger.DalamudPlugin.Game;

/// <summary>Reads the game's textures, each copied to memory at its first read, as each read back waits for the GPU.</summary>
/// <remarks>Must be used on the thread that calls Present, as it uses the immediate context.</remarks>
internal sealed unsafe class TextureReader : IDisposable
{
    private readonly Dictionary<Pointer<KernelTexture>, TextureCopy> _copies = [];

    public static bool CanRead(KernelTexture* texture)
    {
        var resource = (ID3D11Texture2D*)texture->D3D11Texture2D;
        if (resource is null)
            return false;
        D3D11_TEXTURE2D_DESC desc;
        resource->GetDesc(&desc);
        return BytesPerTexel(desc.Format) != 0;
    }

    public byte[] Read(KernelTexture* texture, int x, int y, int width, int height, int plane)
    {
        var alpha = new byte[width * height];
        var stride = width;
        var copy = this.GetCopy(texture);
        width = Math.Min(width, copy.Width - x);
        height = Math.Min(height, copy.Height - y);
        if (width <= 0 || height <= 0 || copy.BytesPerTexel == 0)
            return alpha;

        var pitch = copy.Width * copy.BytesPerTexel;
        var offset = copy.Format is DXGI_FORMAT_R8G8B8A8_UNORM ? plane : GlyphAtlas.BgraOffset(plane);
        for (var row = 0; row < height; row++)
        {
            var src = copy.Texels.AsSpan(((y + row) * pitch) + (x * copy.BytesPerTexel));
            var dst = alpha.AsSpan(row * stride, width);
            if (copy.BytesPerTexel == 2)
            {
                for (var col = 0; col < width; col++)
                    dst[col] = GlyphAtlas.Get4444((ushort)(src[col * 2] | (src[(col * 2) + 1] << 8)), plane);
            }
            else
            {
                for (var col = 0; col < width; col++)
                    dst[col] = src[(col * 4) + offset];
            }
        }

        return alpha;
    }

    public void Clear() => this._copies.Clear();

    public void Dispose() => this.Clear();

    private static int BytesPerTexel(DXGI_FORMAT format) => format switch
    {
        DXGI_FORMAT_B4G4R4A4_UNORM => 2,
        DXGI_FORMAT_B8G8R8A8_UNORM or DXGI_FORMAT_R8G8B8A8_UNORM => 4,
        _ => 0,
    };

    private TextureCopy GetCopy(KernelTexture* texture)
    {
        if (this._copies.TryGetValue(texture, out var known))
            return known;

        var resource = (ID3D11Texture2D*)texture->D3D11Texture2D;
        D3D11_TEXTURE2D_DESC desc;
        resource->GetDesc(&desc);
        var bytes = BytesPerTexel(desc.Format);
        var copy = bytes == 0 ? new TextureCopy(0, 0, 0, desc.Format, []) : ReadAll(resource, desc, bytes);
        this._copies.Add(texture, copy);
        return copy;
    }

    private static TextureCopy ReadAll(ID3D11Texture2D* resource, D3D11_TEXTURE2D_DESC desc, int bytes)
    {
        using var device = resource->GetDevice();
        using var context = default(MyComPtr<ID3D11DeviceContext>);
        fixed (ID3D11DeviceContext** p = context)
            device.Value->GetImmediateContext(p);

        var stagingDesc = new D3D11_TEXTURE2D_DESC
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDesc = new(1, 0),
            Usage = D3D11_USAGE_STAGING,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_READ,
        };
        using var staging = default(MyComPtr<ID3D11Texture2D>);
        device.Value->CreateTexture2D(&stagingDesc, null, staging.GetAddressOf()).ThrowOnError();
        context.Value->CopySubresourceRegion((ID3D11Resource*)staging.Value, 0, 0, 0, 0, (ID3D11Resource*)resource, 0, null);

        var pitch = (int)desc.Width * bytes;
        var texels = new byte[pitch * (int)desc.Height];
        D3D11_MAPPED_SUBRESOURCE mapped;
        context.Value->Map((ID3D11Resource*)staging.Value, 0, D3D11_MAP_READ, 0, &mapped).ThrowOnError();
        try
        {
            for (var row = 0; row < (int)desc.Height; row++)
                new ReadOnlySpan<byte>((byte*)mapped.pData + (row * mapped.RowPitch), pitch).CopyTo(texels.AsSpan(row * pitch));
        }
        finally
        {
            context.Value->Unmap((ID3D11Resource*)staging.Value, 0);
        }

        return new((int)desc.Width, (int)desc.Height, bytes, desc.Format, texels);
    }

    private sealed record TextureCopy(int Width, int Height, int BytesPerTexel, DXGI_FORMAT Format, byte[] Texels);
}
