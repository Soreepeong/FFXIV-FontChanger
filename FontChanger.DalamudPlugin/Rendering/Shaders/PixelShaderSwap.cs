using System;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Rendering.Shaders;

/// <summary>Helper for swapping the pixel shaders of the game that the game may swap out anytime.</summary>
internal sealed unsafe partial class PixelShaderSwap : IDisposable
{
    private MyComPtr<ID3D11PixelShader> _pluginShader;
    private MyComPtr<ID3D11PixelShader> _originalShader;

    public PixelShader* Target { get; private set; }

    public ref MyComPtr<ID3D11PixelShader> PluginShader => ref this._pluginShader;

    /// <summary>Makes a shader of code on the device of the game's shader.</summary>
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
        if (this.Target is null || this.IsIn(current))
            return;
        this.Target = null;
        this._originalShader.Dispose();
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

        this.Target = null;
        this._originalShader.Dispose();
    }

    public void Dispose()
    {
        this.Target = null;
        this._originalShader.Dispose();
        this.PluginShader.Dispose();
    }

    private static ID3D11PixelShader** Slot(PixelShader* target) => (ID3D11PixelShader**)&target->DirectXObject;
}
