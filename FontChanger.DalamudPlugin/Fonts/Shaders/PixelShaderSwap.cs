using System;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Fonts.Shaders;

/// <summary>The game may replace the swapped shader at any time, when it reloads its shaders.</summary>
internal sealed unsafe class PixelShaderSwap
{
    private MyComPtr<ID3D11PixelShader> _pluginShader;
    private MyComPtr<ID3D11PixelShader> _originalShader;
    private PixelShader* _target;

    /// <summary>The server whose shaders draw the UI, null while the game has none.</summary>
    public static AtkServer* GetServer()
    {
        var stage = AtkStage.Instance();
        var fontManager = stage is null ? null : stage->AtkFontManager;
        var module = fontManager is null ? null : fontManager->Module;
        return module is null ? null : module->AtkServer;
    }

    public bool IsIn(PixelShader* target) => target is not null && target == this._target && *Slot(target) == this._pluginShader.Value;

    /// <summary>
    /// Gets the plugin's shader into <paramref name="current"/>, making it first if it isn't made yet. A swap the game replaced
    /// is forgotten.
    /// </summary>
    /// <param name="current">The game's shader object, null while it has none.</param>
    /// <param name="resource">What the game loaded its shader from.</param>
    /// <param name="file">The file that must be, as the plugin's shader stands in for that one only.</param>
    /// <param name="code">The embedded shader's name.</param>
    /// <param name="constants">What the shader's sentinels become.</param>
    public void Ensure(PixelShader* current, ResourceHandle* resource, string file, string code, params ReadOnlySpan<(uint Sentinel, float Value)> constants)
    {
        if (this._target is not null && !this.IsIn(current))
            this.Forget();
        if (current is null || this.IsIn(current))
            return;

        var game = (ID3D11PixelShader*)current->DirectXObject;
        if (game is null || resource is null || !resource->FileName.EndsWithAsciiIgnoreCase(file))
            return;

        if (!this._pluginShader)
        {
            var bytes = ShaderCode.Read(code);
            if (!ShaderCode.PatchConstants(bytes, constants))
                Plugin.Log.Warning("Placeholder variables missing or repeated: {name}", code);
            this._pluginShader.Attach(CreateShader(game, bytes).Detach());
        }

        var slot = Slot(current);
        this._target = current;
        this._originalShader.Attach(*slot);
        this._pluginShader.Value->AddRef();
        *slot = this._pluginShader.Value;
    }

    /// <summary>Puts the game's shader back if the plugin's is still in <paramref name="current"/>, and lets the plugin's go.</summary>
    public void Release(PixelShader* current)
    {
        if (this.IsIn(current))
        {
            *Slot(current) = this._originalShader.Detach();
            this._pluginShader.Value->Release();
        }

        this.Forget();
        this._pluginShader.Dispose();
    }

    /// <summary><paramref name="game"/> gives the device, which must be the one the game draws with.</summary>
    private static MyComPtr<ID3D11PixelShader> CreateShader(ID3D11PixelShader* game, byte[] code)
    {
        using var device = game->GetDevice();
        var shader = default(MyComPtr<ID3D11PixelShader>);
        fixed (byte* bytes = code)
            device.Value->CreatePixelShader(bytes, (nuint)code.Length, null, shader.GetAddressOf()).ThrowOnError();
        return shader;
    }

    private static ID3D11PixelShader** Slot(PixelShader* target) => (ID3D11PixelShader**)&target->DirectXObject;

    private void Forget()
    {
        this._target = null;
        this._originalShader.Dispose();
    }
}
