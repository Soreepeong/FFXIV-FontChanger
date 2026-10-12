using System;

using Dalamud.Hooking;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace CustomFonts;

/// <summary>The font replacement's host, through Dalamud's services.</summary>
internal sealed class DalamudHost : IHost
{
    public DalamudHost()
    {
        this.Log = new LogAdapter(Plugin.Log);
        this.AssemblyDirectory = Plugin.PluginInterface.AssemblyLocation.DirectoryName!;
    }

    public IHostLog Log { get; }

    public string AssemblyDirectory { get; }

    public nint ScanText(string signature) =>
        Plugin.SigScanner.TryScanText(signature, out var address)
            ? address
            : throw new InvalidOperationException($"The signature {signature} isn't in this version of the game.");

    public unsafe nint GetStaticAddress(string signature, int offset)
    {
        var rel32 = this.ScanText(signature) + offset;
        return rel32 + 4 + *(int*)rel32;
    }

    public IHostHook<T> Hook<T>(nint address, T detour)
        where T : Delegate =>
        new HookAdapter<T>(Plugin.GameInterop.HookFromAddress(address, detour));

    public IHostTexture CreateTexture(int width, int height, string name) => new Texture(width, height, name);

    private sealed class LogAdapter(IPluginLog log) : IHostLog
    {
        public void Debug(string template, params object?[] values) => log.Debug(template, values!);

        public void Information(string template, params object?[] values) => log.Information(template, values!);

        public void Warning(string template, params object?[] values) => log.Warning(template, values!);

        public void Warning(Exception? exception, string template, params object?[] values) => log.Warning(exception, template, values!);

        public void Error(Exception? exception, string template, params object?[] values) => log.Error(exception, template, values!);
    }

    private sealed class HookAdapter<T>(Hook<T> hook) : IHostHook<T>
        where T : Delegate
    {
        public T Original => hook.Original;

        public void Enable() => hook.Enable();

        public void Dispose() => hook.Dispose();
    }

    /// <summary>A texture of Dalamud's, converted to a Kernel::Texture.</summary>
    private sealed unsafe class Texture : IHostTexture
    {
        private readonly IDalamudTextureWrap wrap;

        public Texture(int width, int height, string name)
        {
            this.wrap = Plugin.TextureProvider.CreateEmpty(RawImageSpecification.Bgra32(width, height), false, false, name);
            this.Kernel = Plugin.TextureProvider.ConvertToKernelTexture(this.wrap, leaveWrapOpen: true);

            ID3D11ShaderResourceView* srv;
            var iid = IID.IID_ID3D11ShaderResourceView;
            ((IUnknown*)this.wrap.Handle.Handle)->QueryInterface(&iid, (void**)&srv).ThrowOnError();
            ID3D11Resource* res;
            srv->GetResource(&res);
            srv->Release();
            this.Resource = res;
        }

        public nint Kernel { get; }

        public ID3D11Resource* Resource { get; private set; }

        public void Dispose()
        {
            // The game releases a kernel texture some frames after its last reference goes (it is a delayed-release
            // resource), so the frames still in flight can keep sampling it.
            ((KernelTexture*)this.Kernel)->DecRef();
            if (this.Resource is not null)
            {
                this.Resource->Release();
                this.Resource = null;
            }

            this.wrap.Dispose();
        }
    }
}
