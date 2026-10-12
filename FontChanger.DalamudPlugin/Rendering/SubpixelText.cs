using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Component.GUI;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace CustomFonts;

/// <summary>Subpixel text settings: the stripe order, and Windows' ClearType level and enhanced contrast.</summary>
internal readonly record struct SubpixelSettings
{
    public SubpixelSettings()
    {
    }

    public SubpixelOrder Order { get; init; }

    /// <summary>Gets the ClearType level, 0 to 1.</summary>
    public float Level { get; init; } = 1;

    /// <summary>Gets k of <c>coverage * (k + 1) / (coverage * k + 1)</c>.</summary>
    public float Contrast { get; init; } = 0.5f;

    /// <summary>
    /// Gets the level and contrast from Windows for the window's monitor; <see cref="SubpixelMode.System"/> takes the stripe
    /// order from there too.
    /// </summary>
    public static unsafe SubpixelSettings Of(SubpixelMode mode, nint window)
    {
        var res = new SubpixelSettings();
        if (mode == SubpixelMode.Off)
            return res;

        var geometry = DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_RGB;
        IDWriteFactory* factory = null;
        IDWriteRenderingParams* parameters = null;
        try
        {
            var iid = IID.IID_IDWriteFactory;
            DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &iid, (IUnknown**)&factory).ThrowOnError();
            var monitor = window == 0 ? default : MonitorFromWindow((HWND)window, MONITOR.MONITOR_DEFAULTTONEAREST);
            if (monitor != default)
                factory->CreateMonitorRenderingParams(monitor, &parameters).ThrowOnError();
            else
                factory->CreateRenderingParams(&parameters).ThrowOnError();
            geometry = parameters->GetPixelGeometry();
            res = res with
            {
                Level = Math.Clamp(parameters->GetClearTypeLevel(), 0, 1),
                Contrast = Math.Max(parameters->GetEnhancedContrast(), 0),
            };
        }
        catch (Exception ex)
        {
            Host.Log.Warning(ex, "Windows' ClearType settings couldn't be read; using the defaults");
        }
        finally
        {
            if (parameters is not null)
                parameters->Release();
            if (factory is not null)
                factory->Release();
        }

        switch (mode)
        {
            case SubpixelMode.System:
                uint smoothing = 0, type = 0;
                if (!SystemParametersInfoW(SPI.SPI_GETFONTSMOOTHING, 0, &smoothing, 0) || smoothing == 0
                    || !SystemParametersInfoW(SPI.SPI_GETFONTSMOOTHINGTYPE, 0, &type, 0) || type != FE_FONTSMOOTHINGCLEARTYPE)
                    return res;
                return res with
                {
                    Order = geometry switch
                    {
                        DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_RGB => SubpixelOrder.Rgb,
                        DWRITE_PIXEL_GEOMETRY.DWRITE_PIXEL_GEOMETRY_BGR => SubpixelOrder.Bgr,
                        _ => SubpixelOrder.None,
                    },
                };
            case SubpixelMode.Rgb:
                return res with { Order = SubpixelOrder.Rgb };
            case SubpixelMode.Bgr:
                return res with { Order = SubpixelOrder.Bgr };
            default:
                return res;
        }
    }
}

/// <summary>
/// Dual-source blending (SRC1_COLOR / INV_SRC_COLOR) of subpixel pages drawn by the text, highlight and shadow passes,
/// whose shaders become FontSubpixelPS.
/// </summary>
internal sealed unsafe class SubpixelText : IDisposable
{
    // PackedBlendStateDesc fields hold D3D11_BLEND minus one.
    private const int SrcBlendShift = 4;
    private const int DestBlendShift = 8;
    private const uint BlendMask = 0xF;

    private readonly object sync = new();
    private readonly List<PassShader> passes = [];

    private bool resolved;

    private IHostHook<BindShadersDelegate>? bindShadersHook;
    private IHostHook<SetDrawStateDelegate>? setDrawStateHook;

    private volatile bool enabled;
    private SubpixelSettings settings = new();

    // From the last BindShaders.
    private AtkServer* server;
    private AtkServer.ShaderPass pass = (AtkServer.ShaderPass)(-1);
    private bool alternate;
    private bool passActive;

    // The game's last state, forgotten at EndFrame as its texture may be gone by then.
    private AtkServer* lastServer;
    private AtkServer.DrawState lastState;
    private byte lastDivisor = 1;
    private bool lastSubpixelTexture;
    private uint stagedBlend;
    private bool hasState;
    private bool warnedBlend;

    private delegate void BindShadersDelegate(AtkServer* server, AtkServer.ShaderPass pass, byte alternate);

    private delegate void SetDrawStateDelegate(AtkServer* server, AtkServer.DrawState* state, byte divisor);

    /// <summary>Gets whether it is on, with the hooks in place. Framework thread.</summary>
    public bool Active => this.enabled;

    /// <summary>Gets FontSubpixelPS's bytecode with the settings' level and contrast.</summary>
    public static byte[] GetShaderCode(SubpixelSettings settings, bool highlight)
    {
        var name = highlight ? "CustomFonts.Rendering.FontSubpixelHighlightPS.cso" : "CustomFonts.Rendering.FontSubpixelPS.cso";
        using var stream = typeof(SubpixelText).Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"{name} isn't embedded.");
        var code = new byte[stream.Length];
        stream.ReadExactly(code);
        if (EdgeShaderCode.PatchConstants(code, [(SubpixelShaderCode.ClearTypeLevelSentinel, settings.Level), (SubpixelShaderCode.ContrastSentinel, settings.Contrast)]) < 2)
            Host.Log.Warning("FontSubpixelPS.cso doesn't have both of its settings to set; text is drawn with the values it was compiled with");
        return code;
    }

    /// <summary>Framework thread. Throws if a signature isn't found; Order None puts the game's shaders back.</summary>
    public void Set(SubpixelSettings value)
    {
        if (value.Order != SubpixelOrder.None && !this.resolved)
            this.Resolve();

        lock (this.sync)
        {
            var shaderChanged = value.Level != this.settings.Level || value.Contrast != this.settings.Contrast;
            this.settings = value;
            this.enabled = value.Order != SubpixelOrder.None;

            // Shaders go in (again) at the next BindShaders of their pass.
            if (!this.enabled || shaderChanged)
            {
                this.RestoreAll();
                foreach (var p in this.passes)
                    p.ReleaseShader();
            }
        }
    }

    /// <summary>Between frames, after the UI's draw commands are processed.</summary>
    public void EndFrame()
    {
        lock (this.sync)
            this.hasState = false;
    }

    public void Dispose()
    {
        this.bindShadersHook?.Dispose();
        this.setDrawStateHook?.Dispose();
        lock (this.sync)
        {
            this.RestoreAll();
            foreach (var p in this.passes)
                p.ReleaseShader();
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
        lock (this.sync)
        {
            this.passes.Clear();
            this.passes.Add(new(AtkServer.ShaderPass.Font, "FontPS.shcd", false));
            this.passes.Add(new(AtkServer.ShaderPass.FontHighlight, "FontHighlightPS.shcd", true));
            this.passes.Add(new(AtkServer.ShaderPass.FontEmboss, "FontEmbossPS.shcd", false));
        }

        this.setDrawStateHook = Host.Current.Hook<SetDrawStateDelegate>(setDrawState, this.SetDrawStateDetour);
        this.bindShadersHook = Host.Current.Hook<BindShadersDelegate>(bindShaders, this.BindShadersDetour);
        this.setDrawStateHook.Enable();
        this.bindShadersHook.Enable();
        this.resolved = true;
    }

    private void BindShadersDetour(AtkServer* server, AtkServer.ShaderPass pass, byte alternate)
    {
        this.bindShadersHook!.Original(server, pass, alternate);
        lock (this.sync)
        {
            try
            {
                this.server = server;
                this.pass = pass;
                this.alternate = alternate != 0;
                this.passActive = false;
                if (this.enabled && !this.alternate)
                {
                    foreach (var p in this.passes)
                    {
                        if (p.Pass != pass)
                            continue;
                        this.passActive = this.EnsureShader(server, p);
                        break;
                    }
                }

                // SetDrawState isn't called when only the pass changes.
                if (this.hasState && this.lastServer == server)
                {
                    var blend = this.Decide(server, this.lastState, this.lastSubpixelTexture);
                    if (blend != this.stagedBlend)
                    {
                        var state = this.lastState;
                        BlendBits(ref state) = blend;
                        this.setDrawStateHook!.Original(server, &state, this.lastDivisor);
                        this.stagedBlend = blend;
                    }
                }
            }
            catch (Exception ex)
            {
                // The shaders go back too, as the blend no longer changes.
                Host.Log.Error(ex, "Subpixel text failed; turning it off");
                this.enabled = false;
                this.RestoreAll();
            }
        }
    }

    private void SetDrawStateDetour(AtkServer* server, AtkServer.DrawState* state, byte divisor)
    {
        uint blend;
        lock (this.sync)
        {
            this.lastServer = server;
            this.lastState = *state;
            this.lastDivisor = divisor;
            this.lastSubpixelTexture = state->Texture is not null && (state->Texture->ActualWidth & 1) != 0;
            this.hasState = true;
            blend = this.Decide(server, *state, this.lastSubpixelTexture);
            this.stagedBlend = blend;
        }

        if (blend == BlendValue(*state))
        {
            this.setDrawStateHook!.Original(server, state, divisor);
            return;
        }

        // The caller batches later commands by comparing them with its state.
        var original = BlendValue(*state);
        BlendBits(ref *state) = blend;
        this.setDrawStateHook!.Original(server, state, divisor);
        BlendBits(ref *state) = original;
    }

    private uint Decide(AtkServer* server, in AtkServer.DrawState state, bool subpixelTexture)
    {
        var value = BlendValue(state);
        if (!this.enabled || !this.passActive || this.alternate || !subpixelTexture)
            return value;

        if (GetFactor(value, SrcBlendShift) != (uint)D3D11_BLEND.D3D11_BLEND_SRC_ALPHA)
        {
            if (!this.warnedBlend)
            {
                this.warnedBlend = true;
                Host.Log.Warning("Subpixel text: a glyph is drawn with blend state 0x{Blend:X8}, which can't take subpixel coverage", value);
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

        // A UI shader reload drops the patched object, so its game shader is ours to release.
        if (p.PatchedObject != 0 && current != p.PatchedObject)
        {
            if (p.Original is not null)
                p.Original->Release();
            p.PatchedObject = 0;
            p.Original = null;
        }

        if (current == 0)
            return false;

        var slot = (ID3D11PixelShader**)&((PixelShader*)current)->DirectXObject;
        if (current == p.PatchedObject)
            return *slot == p.Shader;

        if (*slot is null || GetShaderObject(server, p.Pass, p.File) == 0)
            return false;

        if (p.Shader is null)
            p.Shader = this.CreateShader(*slot, p.Highlight);

        // The object owns a reference to its shader.
        p.PatchedObject = current;
        p.Original = *slot;
        p.Shader->AddRef();
        *slot = p.Shader;
        return true;
    }

    /// <summary>Gets a pass's Kernel::PixelShader; with <paramref name="file"/>, only if it is made from that file.</summary>
    private static nint GetShaderObject(AtkServer* server, AtkServer.ShaderPass pass, string? file)
    {
        if (server is null)
            return 0;
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
            return 0;
        return (nint)shader;
    }

    private ID3D11PixelShader* CreateShader(ID3D11PixelShader* game, bool highlight)
    {
        var code = GetShaderCode(this.settings, highlight);
        ID3D11Device* device;
        game->GetDevice(&device);
        try
        {
            ID3D11PixelShader* shader;
            fixed (byte* bytes = code)
                device->CreatePixelShader(bytes, (nuint)code.Length, null, &shader).ThrowOnError();
            return shader;
        }
        finally
        {
            device->Release();
        }
    }

    private void Restore(PassShader p)
    {
        if (p.PatchedObject == 0)
            return;
        var slot = (ID3D11PixelShader**)&((PixelShader*)p.PatchedObject)->DirectXObject;
        if (GetShaderObject(this.server, p.Pass, null) == p.PatchedObject && *slot == p.Shader)
        {
            *slot = p.Original;
            p.Shader->Release();
        }

        p.PatchedObject = 0;
        p.Original = null;
    }

    private void RestoreAll()
    {
        foreach (var p in this.passes)
            this.Restore(p);
        this.passActive = false;
    }

    private sealed class PassShader(AtkServer.ShaderPass pass, string file, bool highlight)
    {
        public AtkServer.ShaderPass Pass { get; } = pass;

        public string File { get; } = file;

        public bool Highlight { get; } = highlight;

        public nint PatchedObject { get; set; }

        // Raw: it goes back into the slot.
        public ID3D11PixelShader* Original { get; set; }

        public ID3D11PixelShader* Shader { get; set; }

        public void ReleaseShader()
        {
            if (this.Shader is null)
                return;
            this.Shader->Release();
            this.Shader = null;
        }
    }
}
