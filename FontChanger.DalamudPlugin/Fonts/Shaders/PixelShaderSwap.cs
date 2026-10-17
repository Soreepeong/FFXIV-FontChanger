using System;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Fonts.Shaders;

/// <summary>The game may replace the swapped shader at any time, when it reloads its shaders.</summary>
internal sealed unsafe class PixelShaderSwap : IDisposable
{
    private MyComPtr<ID3D11PixelShader> _pluginShader;
    private MyComPtr<ID3D11PixelShader> _originalShader;

    public PixelShader* Target { get; private set; }

    public ref MyComPtr<ID3D11PixelShader> PluginShader => ref this._pluginShader;

    /// <summary><paramref name="game"/> gives the device, which must be the one the game draws with.</summary>
    public static MyComPtr<ID3D11PixelShader> CreateShader(ID3D11PixelShader* game, byte[] code)
    {
        using var device = game->GetDevice();
        var shader = default(MyComPtr<ID3D11PixelShader>);
        fixed (byte* bytes = code)
            device.Value->CreatePixelShader(bytes, (nuint)code.Length, null, shader.GetAddressOf()).ThrowOnError();
        return shader;
    }

    public bool IsIn(PixelShader* target) => target is not null && target == this.Target && *Slot(target) == this.PluginShader.Value;

    public void ForgetIfReplacedElsewhere(PixelShader* current)
    {
        if (this.Target is not null && !this.IsIn(current))
            this.Forget();
    }

    public void Install(PixelShader* target)
    {
        var slot = Slot(target);
        this.Target = target;
        this._originalShader.Attach(*slot);
        this.PluginShader.Value->AddRef();
        *slot = this.PluginShader.Value;
    }

    public void Restore(PixelShader* current)
    {
        if (this.Target is null)
            return;
        if (this.IsIn(current))
        {
            *Slot(current) = this._originalShader.Detach();
            this.PluginShader.Value->Release();
        }

        this.Forget();
    }

    public void Dispose()
    {
        this.Forget();
        this.PluginShader.Dispose();
    }

    private static ID3D11PixelShader** Slot(PixelShader* target) => (ID3D11PixelShader**)&target->DirectXObject;

    private void Forget()
    {
        this.Target = null;
        this._originalShader.Dispose();
    }
}
