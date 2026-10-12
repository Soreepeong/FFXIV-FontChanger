using System;
using System.Collections.Generic;
using System.IO;

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
    private int pixelShaderFiles;
    private int pixelShaders;
    private int d3dShader;
    private int textureWidth;
    private int renderingToTexture;

    private IHostHook<BindShadersDelegate>? bindShadersHook;
    private IHostHook<SetDrawStateDelegate>? setDrawStateHook;

    private volatile bool enabled;
    private SubpixelSettings settings = new();

    // From the last BindShaders.
    private nint server;
    private int pass = -1;
    private bool alternate;
    private bool passActive;

    // The game's last state, forgotten at EndFrame as its texture may be gone by then.
    private nint lastServer;
    private DrawState lastState;
    private byte lastDivisor = 1;
    private bool lastSubpixelTexture;
    private uint stagedBlend;
    private bool hasState;
    private bool warnedBlend;

    private delegate void BindShadersDelegate(nint server, int pass, byte alternate);

    private delegate void SetDrawStateDelegate(nint server, DrawState* state, byte divisor);

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

    private void Resolve()
    {
        nint bindShaders = 0, setDrawState = 0;
        var found = new List<PassShader>();
        GameLayout.Resolve("Subpixel text", () =>
        {
            GameUi.ResolveResourceHandles();
            bindShaders = GameLayout.Address("AtkServerBindFontShaders");
            setDrawState = GameLayout.Address("AtkServerSetDrawState");
            this.pixelShaderFiles = GameLayout.Get("AtkServer.PixelShaderFiles");
            this.pixelShaders = GameLayout.Get("AtkServer.PixelShaders");
            this.d3dShader = GameLayout.Get("Kernel.PixelShader.D3DShader");
            this.textureWidth = GameLayout.Get("Kernel.Texture.Width");
            this.renderingToTexture = GameLayout.Get("AtkServer.RenderingToTexture");
            found.Add(new(GameLayout.Get("AtkServer.TextPass"), GameLayout.GetString("AtkServer.TextPixelShaderFile"), false));
            found.Add(new(GameLayout.Get("AtkServer.HighlightPass"), GameLayout.GetString("AtkServer.HighlightPixelShaderFile"), true));
            found.Add(new(GameLayout.Get("AtkServer.EmbossPass"), GameLayout.GetString("AtkServer.EmbossPixelShaderFile"), false));
        });

        lock (this.sync)
        {
            this.passes.Clear();
            this.passes.AddRange(found);
        }

        this.setDrawStateHook = Host.Current.Hook<SetDrawStateDelegate>(setDrawState, this.SetDrawStateDetour);
        this.bindShadersHook = Host.Current.Hook<BindShadersDelegate>(bindShaders, this.BindShadersDetour);
        this.setDrawStateHook.Enable();
        this.bindShadersHook.Enable();
        this.resolved = true;
    }

    private void BindShadersDetour(nint server, int pass, byte alternate)
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
                        var state = this.lastState with { Blend = blend };
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

    private void SetDrawStateDetour(nint server, DrawState* state, byte divisor)
    {
        uint blend;
        lock (this.sync)
        {
            this.lastServer = server;
            this.lastState = *state;
            this.lastDivisor = divisor;
            this.lastSubpixelTexture = state->Texture != 0 && (*(uint*)(state->Texture + this.textureWidth) & 1) != 0;
            this.hasState = true;
            blend = this.Decide(server, *state, this.lastSubpixelTexture);
            this.stagedBlend = blend;
        }

        if (blend == state->Blend)
        {
            this.setDrawStateHook!.Original(server, state, divisor);
            return;
        }

        // The caller batches later commands by comparing them with its state.
        var original = state->Blend;
        state->Blend = blend;
        this.setDrawStateHook!.Original(server, state, divisor);
        state->Blend = original;
    }

    private uint Decide(nint server, in DrawState state, bool subpixelTexture)
    {
        if (!this.enabled || !this.passActive || this.alternate || !subpixelTexture)
            return state.Blend;

        if (GetFactor(state.Blend, SrcBlendShift) != (uint)D3D11_BLEND.D3D11_BLEND_SRC_ALPHA)
        {
            if (!this.warnedBlend)
            {
                this.warnedBlend = true;
                Host.Log.Warning("Subpixel text: a glyph is drawn with blend state 0x{Blend:X8}, which can't take subpixel coverage", state.Blend);
            }

            return state.Blend;
        }

        var blend = SetFactor(state.Blend, SrcBlendShift, D3D11_BLEND.D3D11_BLEND_SRC1_COLOR);

        // A render texture's alpha is composited later, so its colour can't take coverage per channel.
        var toTexture = *(byte*)(server + this.renderingToTexture) != 0;
        if (!toTexture && GetFactor(blend, DestBlendShift) == (uint)D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA)
            blend = SetFactor(blend, DestBlendShift, D3D11_BLEND.D3D11_BLEND_INV_SRC_COLOR);
        return blend;
    }

    /// <summary>Puts this one's shader in if the game's is there (again after a UI shader reload).</summary>
    private bool EnsureShader(nint server, PassShader p)
    {
        var current = this.GetShaderObject(server, p.Pass, null);

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

        var slot = (ID3D11PixelShader**)(current + this.d3dShader);
        if (current == p.PatchedObject)
            return *slot == p.Shader;

        if (*slot is null || this.GetShaderObject(server, p.Pass, p.File) == 0)
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
    private nint GetShaderObject(nint server, int pass, string? file)
    {
        if (server == 0)
            return 0;
        if (file is not null)
        {
            var handle = *(nint*)(server + this.pixelShaderFiles + (pass * 8));
            if (handle == 0 || !GameUi.GetFileName(handle).EndsWith(file, StringComparison.OrdinalIgnoreCase))
                return 0;
        }

        return *(nint*)(server + this.pixelShaders + (pass * 8));
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
        var slot = (ID3D11PixelShader**)(p.PatchedObject + this.d3dShader);
        if (this.GetShaderObject(this.server, p.Pass, null) == p.PatchedObject && *slot == p.Shader)
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

    /// <summary>What SetDrawState stages for a batch of UI draw commands.</summary>
    private struct DrawState
    {
        public uint Blend;
        public uint Padding;
        public nint Texture;
        public uint Unknown10;
        public uint Unknown14;
    }

    private sealed class PassShader(int pass, string file, bool highlight)
    {
        public int Pass { get; } = pass;

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
