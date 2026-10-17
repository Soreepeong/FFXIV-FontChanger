using System;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Fonts.Shaders;

internal sealed unsafe class EdgeShader : IDisposable
{
    public const float MaxSoftness = 8;

    private static readonly nint ModuleFontManagerOffset = typeof(AtkModule).FieldOffsetOf(nameof(AtkModule.AtkFontManager));

    private readonly PixelShaderSwap _swap = new();

    private Hook<LoadShadersDelegate>? _loadShadersHook;
    private float _softness;
    private bool _used;

    private delegate byte LoadShadersDelegate(AtkServer* server);

    /// <summary>Null restores the game's shader.</summary>
    public void SetSoftness(float? value)
    {
        Misc.DebugAssertMainThread();

        this._used = value.HasValue;
        if (value is not { } softness)
        {
            this.Restore();
            return;
        }

        if (this._loadShadersHook is null)
        {
            var loadShaders = AtkServer.Addresses.LoadShaders.Value;
            this._loadShadersHook = Plugin.GameInterop.HookFromAddress<LoadShadersDelegate>(loadShaders, this.LoadShadersDetour);
            this._loadShadersHook.Enable();
        }

        if (this._swap.PluginShader && softness != this._softness)
        {
            this.Restore();
            this._swap.PluginShader.Dispose();
        }

        this._softness = softness;

        // Before the game has loaded its shaders, LoadShadersDetour installs it once it does.
        if (this.CreateFromGame())
            this.Install();
    }

    public void Dispose()
    {
        this._loadShadersHook?.Dispose();
        this.Restore();
        this._swap.Dispose();
    }

    private static PixelShader* GetEdgeShaderObjectOrNull(bool checkFontEdgePs)
    {
        var stage = AtkStage.Instance();
        var fontManager = stage is null ? null : stage->AtkFontManager;
        var module = fontManager is null ? null : (AtkModule*)((byte*)fontManager - ModuleFontManagerOffset);
        var server = module is null ? null : module->AtkServer;
        if (server is null)
            return null;
        var file = (ResourceHandle*)server->FontEdgePSResource;
        return checkFontEdgePs && (file is null || !file->FileName.EndsWithAsciiIgnoreCase("FontEdgePS.shcd")) ? null : server->FontEdgePS;
    }

    private bool CreateFromGame()
    {
        if (this._swap.PluginShader)
            return true;
        var target = GetEdgeShaderObjectOrNull(true);
        if (target is null)
            return false;

        var code = ShaderCode.Read("FontChanger.DalamudPlugin.Fonts.Shaders.FontEdgePS.cso");
        if (!ShaderCode.PatchConstants(code, (ShaderCode.SoftnessSentinel, this._softness)))
            Plugin.Log.Error(null, "Missing placeholder variables");
        this._swap.PluginShader.Attach(PixelShaderSwap.CreateShader((ID3D11PixelShader*)target->DirectXObject, code).Detach());
        return true;
    }

    private void Install()
    {
        var target = GetEdgeShaderObjectOrNull(true);
        if (target is not null && !this._swap.IsIn(target))
            this._swap.Install(target);
    }

    private void Restore()
    {
        if (this._swap.Target is not null)
            this._swap.Restore(GetEdgeShaderObjectOrNull(false));
    }

    private byte LoadShadersDetour(AtkServer* server)
    {
        var result = this._loadShadersHook!.Original(server);
        try
        {
            this._swap.ForgetIfReplacedElsewhere(GetEdgeShaderObjectOrNull(false));
            if (this._used && this.CreateFromGame())
                this.Install();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Installing the edge shader again failed");
        }

        return result;
    }
}
