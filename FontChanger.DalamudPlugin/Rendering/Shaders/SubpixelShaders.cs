using System;
using System.Collections.Generic;
using System.Threading;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Shaders;

/// <summary>Subpixel rendering using SRC1_COLOR + INV_SRC_COLOR, replacing the shaders for the text, highlight, and shadow passes.</summary>
internal sealed unsafe partial class SubpixelShaders : IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<PassShader> _passes = [];

    private bool _resolved;

    private Hook<BindShadersDelegate>? _bindShadersHook;
    private Hook<SetDrawStateDelegate>? _setDrawStateHook;

    private volatile bool _enabled;
    private SettingsRecord _settings = new();

    // From the last BindShaders.
    private AtkServer* _server;
    private bool _alternate;
    private bool _passActive;

    // The game's last state, forgotten at EndFrame as its texture may be gone by then.
    private AtkServer* _lastServer;
    private AtkServer.DrawState _lastState;
    private byte _lastDivisor = 1;
    private bool _lastSubpixelTexture;
    private bool _hasState;
    private bool _warnedBlend;
    private PackedBlendStateDesc _stagedBlend;

    private delegate void BindShadersDelegate(AtkServer* server, AtkServer.ShaderPass pass, byte alternate);

    private delegate void SetDrawStateDelegate(AtkServer* server, AtkServer.DrawState* state, byte divisor);

    public bool Active
    {
        get
        {
            Misc.DebugAssertMainThread();
            return this._enabled;
        }
    }

    public SettingsRecord Settings
    {
        get
        {
            Misc.DebugAssertMainThread();
            return this._settings;
        }
        set
        {
            Misc.DebugAssertMainThread();
            if (this._settings.Equals(value))
                return;

            if (value.Order != SubpixelOrder.None && !this._resolved)
                this.Resolve();

            using var _ = this._lock.EnterScope();
            var shaderChanged = value.Level != this._settings.Level || value.Contrast != this._settings.Contrast || value.Gamma != this._settings.Gamma;
            this._settings = value;
            this._enabled = value.Order != SubpixelOrder.None;

            // Next call to BindShaders(Detour) sets up our shaders
            if (!this._enabled || shaderChanged)
                this.RestoreAndReleaseAllInLock();
        }
    }

    public void EndFrame()
    {
        lock (this._lock)
            this._hasState = false;
    }

    public void Dispose()
    {
        this._bindShadersHook?.Dispose();
        this._setDrawStateHook?.Dispose();
        lock (this._lock)
        {
            this.RestoreAndReleaseAllInLock();
            foreach (var p in this._passes)
                p.Swap.Dispose();
        }
    }

    private void Resolve()
    {
        var setDrawState = AtkServer.Addresses.SetDrawState.Value;
        var bindShaders = AtkServer.Addresses.BindShaders.Value;
        lock (this._lock)
        {
            this._passes.Clear();
            this._passes.Add(new(AtkServer.ShaderPass.Font, "FontPS.shcd", false));
            this._passes.Add(new(AtkServer.ShaderPass.FontHighlight, "FontHighlightPS.shcd", true));
            this._passes.Add(new(AtkServer.ShaderPass.FontEmboss, "FontEmbossPS.shcd", false));
        }

        this._setDrawStateHook = Plugin.GameInterop.HookFromAddress<SetDrawStateDelegate>(setDrawState, this.SetDrawStateDetour);
        this._bindShadersHook = Plugin.GameInterop.HookFromAddress<BindShadersDelegate>(bindShaders, this.BindShadersDetour);
        this._setDrawStateHook.Enable();
        this._bindShadersHook.Enable();
        this._resolved = true;
    }

    private void BindShadersDetour(AtkServer* server, AtkServer.ShaderPass pass, byte alternate)
    {
        this._bindShadersHook!.Original(server, pass, alternate);
        using var _ = this._lock.EnterScope();
        try
        {
            this._server = server;
            this._alternate = alternate != 0;
            this._passActive = false;
            if (this._enabled && !this._alternate && this._passes.TryFind(pass, static (p, cmp) => p.Pass == cmp, out var p))
                this._passActive = this.EnsureShaderSwap(server, p);

            // SetDrawState isn't called when only the pass changes.
            if (this._hasState && this._lastServer == server)
            {
                var blend = this.Decide(server, this._lastState, this._lastSubpixelTexture);
                if (!blend.ByteEquals(this._stagedBlend))
                {
                    var state = this._lastState;
                    state.BlendState = blend;
                    this._setDrawStateHook!.Original(server, &state, this._lastDivisor);
                    this._stagedBlend = blend;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Subpixel text failed; turning it off");
            this._enabled = false;
            this.RestoreAndReleaseAllInLock();
        }
    }

    private void SetDrawStateDetour(AtkServer* server, AtkServer.DrawState* state, byte divisor)
    {
        PackedBlendStateDesc blend;
        lock (this._lock)
        {
            this._lastServer = server;
            this._lastState = *state;
            this._lastDivisor = divisor;
            this._lastSubpixelTexture = state->Texture is not null && (state->Texture->ActualWidth & 1) != 0;
            this._hasState = true;
            blend = this.Decide(server, *state, this._lastSubpixelTexture);
            this._stagedBlend = blend;
        }

        if (blend.ByteEquals(state->BlendState))
        {
            this._setDrawStateHook!.Original(server, state, divisor);
            return;
        }

        var original = state->BlendState;
        state->BlendState = blend;
        this._setDrawStateHook!.Original(server, state, divisor);
        state->BlendState = original;
    }

    private PackedBlendStateDesc Decide(AtkServer* server, in AtkServer.DrawState state, bool subpixelTexture)
    {
        this._lock.DebugAssertLockHeld();
        var value = state.BlendState;
        if (!this._enabled || !this._passActive || this._alternate || !subpixelTexture)
            return value;

        if (state.BlendState.SrcBlend != (byte)D3D11_BLEND.D3D11_BLEND_SRC_ALPHA)
        {
            if (!this._warnedBlend)
            {
                this._warnedBlend = true;
                Plugin.Log.Warning("Subpixel text: a glyph is drawn with blend state 0x{Blend:X8}, which can't take subpixel coverage", value.Describe());
            }

            return value;
        }

        value.SrcBlend = (byte)D3D11_BLEND.D3D11_BLEND_SRC1_COLOR;

        // A render texture's alpha is composited later, so its color can't take coverage per channel.
        var toTexture = server->IsRenderingToTexture;
        if (!toTexture && value.DestBlend == (byte)D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA)
            value.DestBlend = (byte)D3D11_BLEND.D3D11_BLEND_INV_SRC_COLOR;
        return value;
    }

    private bool EnsureShaderSwap(AtkServer* server, PassShader p)
    {
        this._lock.DebugAssertLockHeld();
        var current = GetShaderObjectOrNull(server, p.Pass, null);
        p.Swap.ForgetIfReplacedElsewhere(current);
        if (current is null)
            return false;
        if (p.Swap.IsIn(current))
            return true;

        var game = (ID3D11PixelShader*)current->DirectXObject;
        if (game is null || GetShaderObjectOrNull(server, p.Pass, p.File) is null)
            return false;

        if (!p.Swap.HasPluginShader)
            p.Swap.PluginShader = this.CreateShader(game, p.Highlight);
        p.Swap.Install(current);
        return true;
    }

    private static PixelShader* GetShaderObjectOrNull(AtkServer* server, AtkServer.ShaderPass pass, string? file)
    {
        if (server is null)
            return null;

        PixelShader* shader;
        ShaderCodeResourceHandle* resource;
        switch (pass)
        {
            case AtkServer.ShaderPass.Font:
                shader = server->FontPS;
                resource = server->FontPSResource;
                break;
            case AtkServer.ShaderPass.FontHighlight:
                shader = server->FontHightlightPS;
                resource = server->FontHighlightPSResource;
                break;
            case AtkServer.ShaderPass.FontEmboss:
                shader = server->FontEmbossPS;
                resource = server->FontEmbossPSResource;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(pass));
        }

        return file is null || (resource is not null && resource->FileName.EndsWithAsciiIgnoreCase(file)) ? shader : null;
    }

    private ComPtr<ID3D11PixelShader> CreateShader(ID3D11PixelShader* game, bool highlight)
    {
        var name = highlight
                       ? "FontChanger.DalamudPlugin.Rendering.Shaders.FontSubpixelHighlightPS.cso"
                       : "FontChanger.DalamudPlugin.Rendering.Shaders.FontSubpixelPS.cso";
        var code = PixelShaderSwap.ReadShaderCode(name);
        if (!PixelShaderSwap.PatchConstants(code,
                (ClearTypeLevelSentinel, this._settings.Level),
                (ContrastSentinel, this._settings.Contrast),
                (GammaSentinel, this._settings.Gamma)))
            Plugin.Log.Warning($"Placeholder variables missing or repeated: {name}");
        return PixelShaderSwap.CreateShader(game, code);
    }

    private void RestoreAndReleaseAllInLock()
    {
        this._lock.DebugAssertLockHeld();
        foreach (var p in this._passes)
        {
            if (p.Swap.Target is not null)
                p.Swap.Restore(GetShaderObjectOrNull(this._server, p.Pass, null));
            p.Swap.ReleaseShader();
        }

        this._passActive = false;
    }

    private sealed class PassShader(AtkServer.ShaderPass pass, string file, bool highlight)
    {
        public AtkServer.ShaderPass Pass { get; } = pass;

        public string File { get; } = file;

        public bool Highlight { get; } = highlight;

        public PixelShaderSwap Swap { get; } = new();
    }
}
