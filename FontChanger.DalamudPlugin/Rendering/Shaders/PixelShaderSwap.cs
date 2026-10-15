using System;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Shaders;

/// <summary>Helper for swapping the pixel shaders of the game that the game may swap out anytime.</summary>
internal sealed unsafe partial class PixelShaderSwap : IDisposable
{
    private ComPtr<ID3D11PixelShader> _originalShader;

    public PixelShader* Target { get; private set; }

    public bool HasPluginShader => this.PluginShader is not null;

    public ID3D11PixelShader* PluginShader
    {
        get;
        set
        {
            if (field == value)
                return;
            if (field is not null)
                field->Release();
            field = value;
        }
    }

    public static ComPtr<ID3D11PixelShader> CreateShader(ID3D11PixelShader* game, byte[] code)
    {
        using var device = default(ComPtr<ID3D11Device>);
        game->GetDevice(device.GetAddressOf());
        var shader = default(ComPtr<ID3D11PixelShader>);
        fixed (byte* bytes = code)
            device.Get()->CreatePixelShader(bytes, (nuint)code.Length, null, shader.GetAddressOf()).ThrowOnError();
        return shader;
    }

    public void ReleaseShader() => this.PluginShader = null;

    public bool IsIn(PixelShader* target) => target is not null && target == this.Target && *Slot(target) == this.PluginShader;

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
        this.PluginShader->AddRef();
        *slot = this.PluginShader;
    }

    public void Restore(PixelShader* current)
    {
        if (this.Target is null)
            return;
        if (this.IsIn(current))
        {
            *Slot(current) = this._originalShader.Detach();
            this.PluginShader->Release();
        }

        this.Target = null;
        this._originalShader.Dispose();
    }

    public void Dispose()
    {
        this.Target = null;
        this._originalShader.Dispose();
        this.PluginShader = null;
    }

    private static ID3D11PixelShader** Slot(PixelShader* target) => (ID3D11PixelShader**)&target->DirectXObject;
}
