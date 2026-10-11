using System;
using System.IO;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace CustomFonts;

/// <summary>
/// Puts a pixel shader of the plugin's (FontEdgePS.hlsl) in place of the game's FontEdgePS, for a round edge outline of
/// any width, set per font by the texture width it claims.
/// </summary>
/// <remarks>
/// The UI's font shaders are indexed by a draw command's pass (5 is the edge); the ID3D11PixelShader inside the edge's is
/// swapped, with the same input signature and bindings (t0, s0). The game makes them again only in LoadShaders, which puts the plugin's back.
/// </remarks>
internal sealed unsafe class EdgeShader : IDisposable
{
    // The edge pass and its pixel shader, reached through AtkModule, AtkServer and Kernel::PixelShader; resolved at the first use.
    private bool resolved;
    private int edgePass;
    private string shaderFile = string.Empty;
    private int moduleFontManager;
    private int moduleServer;
    private int pixelShaderFiles;
    private int pixelShaders;
    private int d3dShader;

    private IHostHook<LoadShadersDelegate>? loadShadersHook;
    private ID3D11PixelShader* shader;
    private float softness;
    private bool used;

    // The Kernel::PixelShader patched, and the game's ID3D11PixelShader it had.
    private nint patchedObject;
    private ID3D11PixelShader* original;

    private delegate byte LoadShadersDelegate(nint server);

    /// <summary>
    /// Uses the plugin's shader (its radius comes from each font's claimed texture width) with an edge softness, or the
    /// game's. Framework thread. Throws if the shader can't be made; a new softness makes it again.
    /// </summary>
    public void Set(bool use, float softness = 0)
    {
        this.used = use;
        if (!use)
        {
            this.Restore();
            return;
        }

        if (!this.resolved)
        {
            nint loadShaders = 0;
            GameLayout.Resolve("The edge shader", () =>
            {
                GameFontManager.Resolve();
                GameUi.ResolveResourceHandles();
                this.edgePass = GameLayout.Get("AtkServer.EdgePass");
                this.shaderFile = GameLayout.GetString("AtkServer.EdgePixelShaderFile");
                loadShaders = GameLayout.Address("AtkModuleReloadFontShaders", "AtkServer.LoadShaders");
                this.moduleFontManager = GameLayout.Get("AtkModule.AtkFontManager");
                this.moduleServer = GameLayout.Get("AtkModule.AtkServer");
                this.pixelShaderFiles = GameLayout.Get("AtkServer.PixelShaderFiles");
                this.pixelShaders = GameLayout.Get("AtkServer.PixelShaders");
                this.d3dShader = GameLayout.Get("Kernel.PixelShader.D3DShader");
            });
            this.loadShadersHook = Host.Current.Hook<LoadShadersDelegate>(loadShaders, this.LoadShadersDetour);
            this.loadShadersHook.Enable();
            this.resolved = true;
        }

        if (this.shader is not null && softness != this.softness)
        {
            this.Restore();
            this.shader->Release();
            this.shader = null;
        }

        this.softness = softness;

        // Before the game has loaded its shaders, LoadShadersDetour installs it once it does.
        if (this.shader is null && !this.CreateFromGame())
            return;

        this.Install();
    }

    /// <summary>Makes the shader on the device of the game's; false while the game hasn't loaded its edge shader.</summary>
    private bool CreateFromGame()
    {
        var target = this.GetEdgeShaderObject(true);
        if (target == 0)
            return false;
        this.shader = Create(*(ID3D11PixelShader**)(target + this.d3dShader), this.softness);
        return true;
    }

    /// <summary>Puts the shader in place, if it isn't.</summary>
    private void Install()
    {
        var target = this.GetEdgeShaderObject(true);
        if (target == 0 || (target == this.patchedObject && *(ID3D11PixelShader**)(target + this.d3dShader) == this.shader))
            return;

        // The game's object owns a reference to what it binds: it gets one of the plugin's shader.
        this.patchedObject = target;
        this.original = *(ID3D11PixelShader**)(target + this.d3dShader);
        this.shader->AddRef();
        *(ID3D11PixelShader**)(target + this.d3dShader) = this.shader;
    }

    public void Dispose()
    {
        this.loadShadersHook?.Dispose();
        this.Restore();
        if (this.shader is not null)
        {
            this.shader->Release();
            this.shader = null;
        }
    }

    /// <summary>
    /// Gets the Kernel::PixelShader of the edge pass, if <paramref name="check"/> only if it is made from FontEdgePS; 0
    /// if it isn't there.
    /// </summary>
    private nint GetEdgeShaderObject(bool check)
    {
        var fontManager = GameFontManager.Instance();
        var server = fontManager is null ? 0 : *(nint*)((nint)fontManager - this.moduleFontManager + this.moduleServer);
        if (server == 0)
            return 0;
        var file = *(nint*)(server + this.pixelShaderFiles + (this.edgePass * 8));
        if (check && (file == 0 || !GameUi.GetFileName(file).EndsWith(this.shaderFile, StringComparison.OrdinalIgnoreCase)))
            return 0;
        return *(nint*)(server + this.pixelShaders + (this.edgePass * 8));
    }

    /// <summary>
    /// The game made its shaders again (a reload of the UI's shaders and fonts), letting go of the patched object: the
    /// plugin's shader goes in the new one. The game's shader the old one had is the plugin's to release.
    /// </summary>
    private byte LoadShadersDetour(nint server)
    {
        var result = this.loadShadersHook!.Original(server);
        try
        {
            if (this.patchedObject != 0 && this.GetEdgeShaderObject(false) != this.patchedObject)
            {
                if (this.original is not null)
                    this.original->Release();
                this.patchedObject = 0;
                this.original = null;
            }

            if (this.used && (this.shader is not null || this.CreateFromGame()))
                this.Install();
        }
        catch (Exception ex)
        {
            Host.Log.Error(ex, "Installing the edge shader again failed");
        }

        return result;
    }

    /// <summary>
    /// Makes the plugin's shader on the device of the game's; its bytecode is compiled at build time, and the softness
    /// patched in (<see cref="EdgeShaderCode"/>).
    /// </summary>
    private static ID3D11PixelShader* Create(ID3D11PixelShader* game, float softness)
    {
        using var stream = typeof(EdgeShader).Assembly.GetManifestResourceStream("CustomFonts.Rendering.FontEdgePS.cso")
            ?? throw new FileNotFoundException("FontEdgePS.cso isn't embedded.");
        var code = new byte[stream.Length];
        stream.ReadExactly(code);
        if (EdgeShaderCode.Patch(code, softness) == 0)
            Host.Log.Error(null, "FontEdgePS.cso has no Softness to set; the edge is drawn with the value it was compiled with");

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

    /// <summary>Gives the game its shader back, if the object still binds the plugin's.</summary>
    private void Restore()
    {
        if (this.patchedObject == 0)
            return;
        var slot = (ID3D11PixelShader**)(this.patchedObject + this.d3dShader);
        if (this.GetEdgeShaderObject(false) == this.patchedObject && *slot == this.shader)
        {
            *slot = this.original;
            this.shader->Release();
        }

        this.patchedObject = 0;
        this.original = null;
    }
}
