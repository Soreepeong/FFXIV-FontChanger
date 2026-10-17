using System;

using Dalamud.Interface.Textures;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Game;

internal sealed unsafe class KernelTextureWrapper : IDisposable
{
    private MyComPtr<ID3D11Resource> _resource;

    public KernelTextureWrapper(int width, int height, string name, bool format4444 = false)
    {
        var specification = format4444
                                ? new(width, height, (int)DXGI_FORMAT.DXGI_FORMAT_B4G4R4A4_UNORM, width * 2)
                                : RawImageSpecification.Bgra32(width, height);
        using var wrap = Plugin.TextureProvider.CreateEmpty(specification, false, false, name);
        this.Kernel = (Texture*)Plugin.TextureProvider.ConvertToKernelTexture(wrap, leaveWrapOpen: true);
        fixed (ID3D11Resource** resource = this._resource)
            ((ID3D11ShaderResourceView*)wrap.Handle.Handle)->GetResource(resource);
    }

    ~KernelTextureWrapper() => this.ReleaseUnmanagedResources();

    public Texture* Kernel { get; private set; }

    public ref readonly MyComPtr<ID3D11Resource> Resource => ref this._resource;

    public void Dispose()
    {
        this.ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    private void ReleaseUnmanagedResources()
    {
        if (this.Kernel is not null)
        {
            this.Kernel->DecRef();
            this.Kernel = null;
        }

        this._resource.Dispose();
    }
}
