using System;
using System.IO;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Component.GUI;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace CustomFonts;

/// <summary>
/// Puts a pixel shader of the plugin's (FontEdgePS.hlsl) in place of the game's FontEdgePS, for a round edge outline of
/// any width, set per font by the texture width it claims.
/// </summary>
/// <remarks>
/// The UI's font shaders are indexed by a draw command's pass (<see cref="AtkServerShaderPass.FontEdge"/>); the
/// ID3D11PixelShader inside the edge's is swapped, with the same input signature and bindings (t0, s0). The game makes
/// them again only in LoadShaders, which puts the plugin's back.
/// </remarks>
internal sealed unsafe class EdgeShader : IDisposable
{
    private const string ShaderFile = "FontEdgePS.shcd";

    // The game's LoadShaders, hooked at the first use.
    private bool resolved;

    private IHostHook<LoadShadersDelegate>? loadShadersHook;
    private ID3D11PixelShader* shader;
    private float softness;
    private bool used;

    // The Kernel::PixelShader patched, and the game's ID3D11PixelShader it had.
    private PixelShader* patchedObject;
    private ID3D11PixelShader* original;

    private delegate byte LoadShadersDelegate(AtkServer* server);

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
            var loadShaders = Host.Current.ScanText(AtkServerMembers.LoadShadersSignature);
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
        var target = GetEdgeShaderObject(true);
        if (target is null)
            return false;
        this.shader = Create((ID3D11PixelShader*)target->DirectXObject, this.softness);
        return true;
    }

    /// <summary>Puts the shader in place, if it isn't.</summary>
    private void Install()
    {
        var target = GetEdgeShaderObject(true);
        if (target is null || (target == this.patchedObject && target->DirectXObject == this.shader))
            return;

        // The game's object owns a reference to what it binds: it gets one of the plugin's shader.
        this.patchedObject = target;
        this.original = (ID3D11PixelShader*)target->DirectXObject;
        this.shader->AddRef();
        target->DirectXObject = this.shader;
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
    /// Gets the Kernel::PixelShader of the edge pass, if <paramref name="check"/> only if it is made from FontEdgePS; null
    /// if it isn't there.
    /// </summary>
    private static PixelShader* GetEdgeShaderObject(bool check)
    {
        // The AtkModule the font manager is embedded in.
        var fontManager = GameFontManager.Instance();
        var module = fontManager is null ? null : (AtkModule*)((byte*)fontManager - (nint)(&((AtkModule*)null)->AtkFontManager));
        var server = module is null ? null : module->AtkServer;
        if (server is null)
            return null;
        var file = (ResourceHandle*)server->FontEdgePSResource;
        if (check && (file is null || !GameUi.GetFileName(file).EndsWith(ShaderFile, StringComparison.OrdinalIgnoreCase)))
            return null;
        return server->FontEdgePS;
    }

    /// <summary>
    /// The game made its shaders again (a reload of the UI's shaders and fonts), letting go of the patched object: the
    /// plugin's shader goes in the new one. The game's shader the old one had is the plugin's to release.
    /// </summary>
    private byte LoadShadersDetour(AtkServer* server)
    {
        var result = this.loadShadersHook!.Original(server);
        try
        {
            if (this.patchedObject is not null && GetEdgeShaderObject(false) != this.patchedObject)
            {
                if (this.original is not null)
                    this.original->Release();
                this.patchedObject = null;
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
        if (this.patchedObject is null)
            return;
        if (GetEdgeShaderObject(false) == this.patchedObject && this.patchedObject->DirectXObject == this.shader)
        {
            this.patchedObject->DirectXObject = this.original;
            this.shader->Release();
        }

        this.patchedObject = null;
        this.original = null;
    }
}
