using System;
using System.Runtime.CompilerServices;
using System.Threading;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Fonts.Shaders;

/// <summary>Per-channel coverage needs the SRC1_COLOR / INV_SRC_COLOR blend, so the text, highlight and shadow passes get shaders that output it.</summary>
internal sealed unsafe partial class SubpixelShaders : IDisposable
{
    private const string SubpixelCode = "FontChanger.DalamudPlugin.Fonts.Shaders.FontSubpixelPS.cso";
    private const string SubpixelHighlightCode = "FontChanger.DalamudPlugin.Fonts.Shaders.FontSubpixelHighlightPS.cso";

    private readonly Lock _lock = new();

    private readonly PassShader[] _passes =
    [
        new(AtkServer.ShaderPass.Font, "FontPS.shcd", SubpixelCode),
        new(AtkServer.ShaderPass.FontHighlight, "FontHighlightPS.shcd", SubpixelHighlightCode),
        new(AtkServer.ShaderPass.FontEmboss, "FontEmbossPS.shcd", SubpixelCode),
    ];

    private Hook<BindShadersDelegate>? _bindShadersHook;
    private Hook<SetDrawStateDelegate>? _setDrawStateHook;

    private volatile bool _enabled;
    private SettingsRecord _settings;

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

    public bool Active => Misc.DebugAssertMainThread(this._enabled);

    /// <summary>The hooks are made the first time it is on.</summary>
    public void Apply(SettingsRecord value)
    {
        Misc.DebugAssertMainThread();
        if (this._settings.Equals(value))
            return;

        if (value.Order != SubpixelOrder.None && this._bindShadersHook is null)
            this.Resolve();

        using var _ = this._lock.EnterScope();
        var shaderChanged = value with { Order = this._settings.Order } != this._settings;
        this._settings = value;
        this._enabled = value.Order != SubpixelOrder.None;

        // The next BindShaders makes the shaders with the new settings.
        if (!this._enabled || shaderChanged)
            this.RestoreAndReleaseAllInLock();
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SameBlend(PackedBlendStateDesc a, PackedBlendStateDesc b) =>
        Unsafe.As<PackedBlendStateDesc, uint>(ref a) == Unsafe.As<PackedBlendStateDesc, uint>(ref b);

    private static string Describe(PackedBlendStateDesc desc) =>
        $"BlendState({(desc.BlendEnable ? "enabled" : "disabled")}" +
        $", op={Describe((D3D11_BLEND_OP)desc.BlendOp)}" +
        $"(src={Describe((D3D11_BLEND)desc.SrcBlend)}" +
        $", desc={Describe((D3D11_BLEND)desc.DestBlend)}" +
        $"), alpha={desc.BlendOpAlpha}" +
        $"(src={Describe((D3D11_BLEND)desc.SrcBlendAlpha)}" +
        $", desc={Describe((D3D11_BLEND)desc.DestBlendAlpha)}" +
        $"))";

    // TerraFX prefixes each member with its enum's name and an underscore (D3D11_BLEND_ZERO).
    private static string Describe<T>(T value)
        where T : struct, Enum =>
        Enum.IsDefined(value) ? value.ToString()[(typeof(T).Name.Length + 1)..] : $"unknown(0x{Convert.ToUInt32(value):08X})";

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

    private void Resolve()
    {
        var setDrawState = AtkServer.Addresses.SetDrawState.Value;
        var bindShaders = AtkServer.Addresses.BindShaders.Value;
        this._setDrawStateHook = Plugin.GameInterop.HookFromAddress<SetDrawStateDelegate>(setDrawState, this.SetDrawStateDetour);
        this._bindShadersHook = Plugin.GameInterop.HookFromAddress<BindShadersDelegate>(bindShaders, this.BindShadersDetour);
        this._setDrawStateHook.Enable();
        this._bindShadersHook.Enable();
    }

    private PassShader? FindPass(AtkServer.ShaderPass pass)
    {
        foreach (var p in this._passes)
        {
            if (p.Pass == pass)
                return p;
        }

        return null;
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
            if (this._enabled && !this._alternate && this.FindPass(pass) is { } p)
                this._passActive = this.EnsureShaderSwap(server, p);

            // SetDrawState isn't called when only the pass changes.
            if (this._hasState && this._lastServer == server)
            {
                var blend = this.Decide(server, this._lastState, this._lastSubpixelTexture);
                if (!SameBlend(blend, this._stagedBlend))
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

        if (SameBlend(blend, state->BlendState))
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
                Plugin.Log.Warning("Subpixel text: a glyph is drawn with blend state 0x{Blend:X8}, which can't take subpixel coverage", Describe(value));
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

        if (!p.Swap.PluginShader)
            p.Swap.PluginShader.Attach(this.CreateShader(game, p.Code).Detach());
        p.Swap.Install(current);
        return true;
    }

    private MyComPtr<ID3D11PixelShader> CreateShader(ID3D11PixelShader* game, string name)
    {
        var code = ShaderCode.Read(name);
        if (!ShaderCode.PatchConstants(code,
                (ShaderCode.ClearTypeLevelSentinel, this._settings.Level),
                (ShaderCode.ContrastSentinel, this._settings.Contrast),
                (ShaderCode.GammaSentinel, this._settings.Gamma),
                (ShaderCode.GrayscaleContrastSentinel, this._settings.GrayscaleContrast)))
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
            p.Swap.PluginShader.Dispose();
        }

        this._passActive = false;
    }

    private sealed record PassShader(AtkServer.ShaderPass Pass, string File, string Code)
    {
        public PixelShaderSwap Swap { get; } = new();
    }
}
