using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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
/// Dual-source blending (SRC1_COLOR / INV_SRC_COLOR) of subpixel pages drawn by the text, highlight and shadow passes,
/// whose shaders become FontSubpixelPS.
/// </summary>
internal sealed unsafe class SubpixelTextShaders : IDisposable
{
    // PackedBlendStateDesc fields hold D3D11_BLEND minus one.
    private const int SrcBlendShift = 4;
    private const int DestBlendShift = 8;
    private const uint BlendMask = 0xF;

    private readonly object _sync = new();
    private readonly List<PassShader> _passes = [];

    private bool _resolved;

    private Hook<BindShadersDelegate>? _bindShadersHook;
    private Hook<SetDrawStateDelegate>? _setDrawStateHook;

    private volatile bool _enabled;
    private SubpixelSettings _settings = new();

    // From the last BindShaders.
    private AtkServer* _server;
    private bool _alternate;
    private bool _passActive;

    // The game's last state, forgotten at EndFrame as its texture may be gone by then.
    private AtkServer* _lastServer;
    private AtkServer.DrawState _lastState;
    private byte _lastDivisor = 1;
    private bool _lastSubpixelTexture;
    private uint _stagedBlend;
    private bool _hasState;
    private bool _warnedBlend;

    private delegate void BindShadersDelegate(AtkServer* server, AtkServer.ShaderPass pass, byte alternate);

    private delegate void SetDrawStateDelegate(AtkServer* server, AtkServer.DrawState* state, byte divisor);

    /// <summary>Gets whether it is on, with the hooks in place. Framework thread.</summary>
    public bool Active => this._enabled;

    /// <summary>Framework thread. Throws if a signature isn't found; Order None puts the game's shaders back.</summary>
    public void Set(SubpixelSettings value)
    {
        if (value.Order != SubpixelOrder.None && !this._resolved)
            this.Resolve();

        lock (this._sync)
        {
            var shaderChanged = value.Level != this._settings.Level || value.Contrast != this._settings.Contrast || value.Gamma != this._settings.Gamma;
            this._settings = value;
            this._enabled = value.Order != SubpixelOrder.None;

            // Shaders go in (again) at the next BindShaders of their pass.
            if (!this._enabled || shaderChanged)
            {
                this.RestoreAll();
                foreach (var p in this._passes)
                    p.Swap.ReleaseShader();
            }
        }
    }

    /// <summary>Between frames, after the UI's draw commands are processed.</summary>
    public void EndFrame()
    {
        lock (this._sync)
            this._hasState = false;
    }

    public void Dispose()
    {
        this._bindShadersHook?.Dispose();
        this._setDrawStateHook?.Dispose();
        lock (this._sync)
        {
            this.RestoreAll();
            foreach (var p in this._passes)
                p.Swap.Dispose();
        }
    }

    private static uint GetFactor(uint blend, int shift) => ((blend >> shift) & BlendMask) + 1;

    private static uint SetFactor(uint blend, int shift, D3D11_BLEND factor) =>
        (blend & ~(BlendMask << shift)) | (((uint)factor - 1) << shift);

    // FFXIVClientStructs' draw state holds the descriptor, not its bits; the blend state is those 32 bits.
    private static uint BlendValue(in AtkServer.DrawState state) =>
        Unsafe.As<PackedBlendStateDesc, uint>(ref Unsafe.AsRef(in state.BlendState));

    private static ref uint BlendBits(ref AtkServer.DrawState state) => ref Unsafe.As<PackedBlendStateDesc, uint>(ref state.BlendState);

    private void Resolve()
    {
        var setDrawState = AtkServer.Addresses.SetDrawState.Value;
        var bindShaders = AtkServer.Addresses.BindShaders.Value;
        lock (this._sync)
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
        lock (this._sync)
        {
            try
            {
                this._server = server;
                this._alternate = alternate != 0;
                this._passActive = false;
                if (this._enabled && !this._alternate)
                {
                    foreach (var p in this._passes)
                    {
                        if (p.Pass != pass)
                            continue;
                        this._passActive = this.EnsureShader(server, p);
                        break;
                    }
                }

                // SetDrawState isn't called when only the pass changes.
                if (this._hasState && this._lastServer == server)
                {
                    var blend = this.Decide(server, this._lastState, this._lastSubpixelTexture);
                    if (blend != this._stagedBlend)
                    {
                        var state = this._lastState;
                        BlendBits(ref state) = blend;
                        this._setDrawStateHook!.Original(server, &state, this._lastDivisor);
                        this._stagedBlend = blend;
                    }
                }
            }
            catch (Exception ex)
            {
                // The shaders go back too, as the blend no longer changes.
                Plugin.Log.Error(ex, "Subpixel text failed; turning it off");
                this._enabled = false;
                this.RestoreAll();
            }
        }
    }

    private void SetDrawStateDetour(AtkServer* server, AtkServer.DrawState* state, byte divisor)
    {
        uint blend;
        lock (this._sync)
        {
            this._lastServer = server;
            this._lastState = *state;
            this._lastDivisor = divisor;
            this._lastSubpixelTexture = state->Texture is not null && (state->Texture->ActualWidth & 1) != 0;
            this._hasState = true;
            blend = this.Decide(server, *state, this._lastSubpixelTexture);
            this._stagedBlend = blend;
        }

        if (blend == BlendValue(*state))
        {
            this._setDrawStateHook!.Original(server, state, divisor);
            return;
        }

        // The caller batches later commands by comparing them with its state.
        var original = BlendValue(*state);
        BlendBits(ref *state) = blend;
        this._setDrawStateHook!.Original(server, state, divisor);
        BlendBits(ref *state) = original;
    }

    private uint Decide(AtkServer* server, in AtkServer.DrawState state, bool subpixelTexture)
    {
        var value = BlendValue(state);
        if (!this._enabled || !this._passActive || this._alternate || !subpixelTexture)
            return value;

        if (GetFactor(value, SrcBlendShift) != (uint)D3D11_BLEND.D3D11_BLEND_SRC_ALPHA)
        {
            if (!this._warnedBlend)
            {
                this._warnedBlend = true;
                Plugin.Log.Warning("Subpixel text: a glyph is drawn with blend state 0x{Blend:X8}, which can't take subpixel coverage", value);
            }

            return value;
        }

        var blend = SetFactor(value, SrcBlendShift, D3D11_BLEND.D3D11_BLEND_SRC1_COLOR);

        // A render texture's alpha is composited later, so its colour can't take coverage per channel.
        var toTexture = server->IsRenderingToTexture;
        if (!toTexture && GetFactor(blend, DestBlendShift) == (uint)D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA)
            blend = SetFactor(blend, DestBlendShift, D3D11_BLEND.D3D11_BLEND_INV_SRC_COLOR);
        return blend;
    }

    /// <summary>Puts this one's shader in if the game's is there (again after a UI shader reload).</summary>
    private bool EnsureShader(AtkServer* server, PassShader p)
    {
        var current = GetShaderObject(server, p.Pass, null);
        p.Swap.ForgetIfReplaced(current);
        if (current is null)
            return false;
        if (p.Swap.IsIn(current))
            return true;

        var game = (ID3D11PixelShader*)current->DirectXObject;
        if (game is null || GetShaderObject(server, p.Pass, p.File) is null)
            return false;

        if (!p.Swap.HasShader)
            p.Swap.SetShader(this.CreateShader(game, p.Highlight));
        p.Swap.Install(current);
        return true;
    }

    /// <summary>Gets a pass's Kernel::PixelShader; with <paramref name="file"/>, only if it is made from that file.</summary>
    private static PixelShader* GetShaderObject(AtkServer* server, AtkServer.ShaderPass pass, string? file)
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

        if (file is not null && (resource is null || !((ResourceHandle*)resource)->FileName.ToString().EndsWith(file, StringComparison.OrdinalIgnoreCase)))
            return null;
        return shader;
    }

    /// <summary>Makes FontSubpixelPS, or its highlight variant, with the settings' level, contrast and gamma.</summary>
    private ComPtr<ID3D11PixelShader> CreateShader(ID3D11PixelShader* game, bool highlight)
    {
        var code = PixelShaderSwap.ReadShaderCode(
            highlight ? "FontChanger.DalamudPlugin.Rendering.Shaders.FontSubpixelHighlightPS.cso" : "FontChanger.DalamudPlugin.Rendering.Shaders.FontSubpixelPS.cso");
        if (PixelShaderSwap.PatchConstants(
                code,
                [
                    (SubpixelShaderCode.ClearTypeLevelSentinel, this._settings.Level),
                    (SubpixelShaderCode.ContrastSentinel, this._settings.Contrast),
                    (SubpixelShaderCode.GammaSentinel, this._settings.Gamma),
                ]) < 3)
            Plugin.Log.Warning("FontSubpixelPS.cso doesn't have all of its settings to set; text is drawn with the values it was compiled with");
        return PixelShaderSwap.CreateShader(game, code);
    }

    private void RestoreAll()
    {
        foreach (var p in this._passes)
        {
            if (p.Swap.Target is not null)
                p.Swap.Restore(GetShaderObject(this._server, p.Pass, null));
        }

        this._passActive = false;
    }

    private sealed class PassShader(AtkServer.ShaderPass pass, string file, bool highlight)
    {
        public AtkServer.ShaderPass Pass { get; } = pass;

        public string File { get; } = file;

        public bool Highlight { get; } = highlight;

        /// <summary>FontSubpixelPS, and the pass's Kernel::PixelShader it is put in.</summary>
        public PixelShaderSwap Swap { get; } = new();
    }
}
