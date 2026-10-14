using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Shaders;

/// <summary>
/// Puts a pixel shader of the plugin's (FontEdgePS.hlsl) in place of the game's FontEdgePS, for a round edge outline of
/// any width, set per font by the texture width it claims.
/// </summary>
internal sealed unsafe class EdgeShader : IDisposable
{
    private const string ShaderFile = "FontEdgePS.shcd";

    // AtkModule.AtkFontManager's offset in the module; from the field's attribute (see FieldOffsets).
    private static readonly nint ModuleFontManagerOffset = typeof(AtkModule).FieldOffsetOf(nameof(AtkModule.AtkFontManager));

    // The plugin's shader, and the Kernel::PixelShader it is put in.
    private readonly PixelShaderSwap _swap = new();

    // The game's LoadShaders, hooked at the first use.
    private bool _resolved;

    private Hook<LoadShadersDelegate>? _loadShadersHook;
    private float _softness;
    private bool _used;

    private delegate byte LoadShadersDelegate(AtkServer* server);

    /// <summary>
    /// Uses the plugin's shader (its radius comes from each font's claimed texture width) with an edge softness, or the
    /// game's. Framework thread. Throws if the shader can't be made; a new softness makes it again.
    /// </summary>
    public void Set(bool use, float softness = 0)
    {
        this._used = use;
        if (!use)
        {
            this.Restore();
            return;
        }

        if (!this._resolved)
        {
            var loadShaders = AtkServer.Addresses.LoadShaders.Value;
            this._loadShadersHook = Plugin.GameInterop.HookFromAddress<LoadShadersDelegate>(loadShaders, this.LoadShadersDetour);
            this._loadShadersHook.Enable();
            this._resolved = true;
        }

        if (this._swap.HasShader && softness != this._softness)
        {
            this.Restore();
            this._swap.ReleaseShader();
        }

        this._softness = softness;

        // Before the game has loaded its shaders, LoadShadersDetour installs it once it does.
        if (!this._swap.HasShader && !this.CreateFromGame())
            return;

        this.Install();
    }

    /// <summary>Makes the shader on the device of the game's; false while the game hasn't loaded its edge shader.</summary>
    private bool CreateFromGame()
    {
        var target = GetEdgeShaderObject(true);
        if (target is null)
            return false;
        this._swap.SetShader(Create((ID3D11PixelShader*)target->DirectXObject, this._softness));
        return true;
    }

    /// <summary>Puts the shader in place, if it isn't.</summary>
    private void Install()
    {
        var target = GetEdgeShaderObject(true);
        if (target is not null && !this._swap.IsIn(target))
            this._swap.Install(target);
    }

    public void Dispose()
    {
        this._loadShadersHook?.Dispose();
        this.Restore();
        this._swap.Dispose();
    }

    /// <summary>
    /// Gets the Kernel::PixelShader of the edge pass, if <paramref name="check"/> only if it is made from FontEdgePS; null
    /// if it isn't there.
    /// </summary>
    private static PixelShader* GetEdgeShaderObject(bool check)
    {
        // The AtkModule the font manager is embedded in.
        var stage = AtkStage.Instance();
        var fontManager = stage is null ? null : stage->AtkFontManager;
        var module = fontManager is null ? null : (AtkModule*)((byte*)fontManager - ModuleFontManagerOffset);
        var server = module is null ? null : module->AtkServer;
        if (server is null)
            return null;
        var file = (ResourceHandle*)server->FontEdgePSResource;
        if (check && (file is null || !file->GetFileName().EndsWith(ShaderFile, StringComparison.OrdinalIgnoreCase)))
            return null;
        return server->FontEdgePS;
    }

    /// <summary>
    /// The game made its shaders again (a reload of the UI's shaders and fonts), letting go of the patched object: the
    /// plugin's shader goes in the new one. The game's shader the old one had is the plugin's to release.
    /// </summary>
    private byte LoadShadersDetour(AtkServer* server)
    {
        var result = this._loadShadersHook!.Original(server);
        try
        {
            this._swap.ForgetIfReplaced(GetEdgeShaderObject(false));
            if (this._used && (this._swap.HasShader || this.CreateFromGame()))
                this.Install();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Installing the edge shader again failed");
        }

        return result;
    }

    /// <summary>
    /// Makes the plugin's shader on the device of the game's; its bytecode is compiled at build time, and the softness
    /// patched in (<see cref="EdgeShaderCode"/>).
    /// </summary>
    private static ComPtr<ID3D11PixelShader> Create(ID3D11PixelShader* game, float softness)
    {
        var code = PixelShaderSwap.ReadShaderCode("FontChanger.DalamudPlugin.Rendering.Shaders.FontEdgePS.cso");
        if (PixelShaderSwap.PatchConstants(code, [(EdgeShaderCode.Sentinel, softness)]) == 0)
            Plugin.Log.Error(null, "FontEdgePS.cso has no Softness to set; the edge is drawn with the value it was compiled with");
        return PixelShaderSwap.CreateShader(game, code);
    }

    /// <summary>Gives the game its shader back, if the object still binds the plugin's.</summary>
    private void Restore()
    {
        if (this._swap.Target is not null)
            this._swap.Restore(GetEdgeShaderObject(false));
    }
}
