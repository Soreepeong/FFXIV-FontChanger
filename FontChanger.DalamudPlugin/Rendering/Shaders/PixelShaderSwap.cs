using System;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Shaders;

/// <summary>
/// A pixel shader of the plugin's, put in place of the game's in a Kernel::PixelShader. The object owns a reference to
/// what it binds: while swapped in, it holds one of the plugin's shader, and this holds the one it had of the game's.
/// </summary>
internal sealed unsafe partial class PixelShaderSwap : IDisposable
{
    // Fields, as ComPtr is a mutable struct.
    private ComPtr<ID3D11PixelShader> _shader;
    private ComPtr<ID3D11PixelShader> _original;

    /// <summary>Gets the object the shader was put in, or null.</summary>
    public PixelShader* Target { get; private set; }

    /// <summary>Gets whether the plugin's shader is made.</summary>
    public bool HasShader => !this._shader.IsNull;

    /// <summary>Makes a pixel shader on the device of a game's shader.</summary>
    public static ComPtr<ID3D11PixelShader> CreateShader(ID3D11PixelShader* game, byte[] code)
    {
        using var device = default(ComPtr<ID3D11Device>);
        game->GetDevice(device.GetAddressOf());
        var shader = default(ComPtr<ID3D11PixelShader>);
        fixed (byte* bytes = code)
            device.Get()->CreatePixelShader(bytes, (nuint)code.Length, null, shader.GetAddressOf()).ThrowOnError();
        return shader;
    }

    /// <summary>Takes the plugin's shader, releasing the one before. Restore first if it is swapped in.</summary>
    public void SetShader(ComPtr<ID3D11PixelShader> shader)
    {
        this._shader.Dispose();
        this._shader = shader;
    }

    /// <summary>Releases the plugin's shader, to be made again. Restore first if it is swapped in.</summary>
    public void ReleaseShader() => this._shader.Dispose();

    /// <summary>Gets whether <paramref name="target"/> is the object the shader was put in, and still binds it.</summary>
    public bool IsIn(PixelShader* target) => target is not null && target == this.Target && *Slot(target) == this._shader.Get();

    /// <summary>
    /// Forgets the object the shader was put in unless it is <paramref name="current"/> and still binds the shader: a UI
    /// shader reload let go of it (a new object may take its address, or the object may bind a new shader), so the game's
    /// shader it had is the plugin's to release.
    /// </summary>
    public void ForgetIfReplaced(PixelShader* current)
    {
        if (this.Target is null || this.IsIn(current))
            return;
        this.Target = null;
        this._original.Dispose();
    }

    /// <summary>Puts the shader in an object, taking over its reference to the game's.</summary>
    public void Install(PixelShader* target)
    {
        var slot = Slot(target);
        this.Target = target;
        this._original.Attach(*slot);
        this._shader.CopyTo(slot).ThrowOnError();
    }

    /// <summary>
    /// Gives the game its shader back if <paramref name="current"/>, the object there now, is the one the shader was put in
    /// and still binds it.
    /// </summary>
    public void Restore(PixelShader* current)
    {
        if (this.Target is null)
            return;
        if (this.IsIn(current))
        {
            *Slot(current) = this._original.Detach();
            this._shader.Get()->Release();
        }

        // Otherwise the slot doesn't hold ours any more, so the game shader's reference is ours to release.
        this.Target = null;
        this._original.Dispose();
    }

    /// <summary>Releases what is held; an object still binding the plugin's shader keeps its own reference to it.</summary>
    public void Dispose()
    {
        this.Target = null;
        this._original.Dispose();
        this._shader.Dispose();
    }

    private static ID3D11PixelShader** Slot(PixelShader* target) => (ID3D11PixelShader**)&target->DirectXObject;
}
