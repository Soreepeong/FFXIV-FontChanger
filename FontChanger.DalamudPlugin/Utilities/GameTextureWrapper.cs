using System;

using Dalamud.Interface.Textures;

using FontChanger.DalamudPlugin.PluginInterface;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace FontChanger.DalamudPlugin.Utilities;

internal sealed unsafe class GameTextureWrapper : IDisposable
{
    private ComPtr<ID3D11Resource> _resource;
    private KernelTexture* _kernel;

    public GameTextureWrapper(int width, int height, string name, bool format4444 = false)
    {
        var specification = format4444
                                ? new(width, height, (int)DXGI_FORMAT.DXGI_FORMAT_B4G4R4A4_UNORM, width * 2)
                                : RawImageSpecification.Bgra32(width, height);
        using var wrap = Plugin.TextureProvider.CreateEmpty(specification, false, false, name);
        this._kernel = (KernelTexture*)Plugin.TextureProvider.ConvertToKernelTexture(wrap, leaveWrapOpen: true);
        ((ID3D11ShaderResourceView*)wrap.Handle.Handle)->GetResource(this._resource.GetAddressOf());
    }

    ~GameTextureWrapper() => this.ReleaseUnmanagedResources();

    public KernelTexture* Kernel => this._kernel;
    public ID3D11Resource* Resource => this._resource.Get();

    public void Dispose() => this.ReleaseUnmanagedResources();

    private void ReleaseUnmanagedResources()
    {
        if (this._kernel is not null)
        {
            this._kernel->DecRef();
            this._kernel = null;
        }

        this._resource.Dispose();
    }
}
