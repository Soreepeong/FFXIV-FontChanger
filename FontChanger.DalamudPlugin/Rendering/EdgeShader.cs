using System;
using System.IO;
using System.Text;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.UI;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DirectX;

namespace CustomFonts;

/// <summary>
/// Puts a pixel shader of the plugin's (FontEdgePS.hlsl) in place of the game's FontEdgePS, for a round edge outline of
/// any width, set per font by the texture width it claims.
/// </summary>
/// <remarks>
/// The UI's font shaders are nine vertex/pixel shader pairs (FUN_14063A760) in an object at AtkModule +0x130: shader file
/// resource handles at +0x10 (VS) and +0x58 (PS), and the Kernel shaders made from them at +0xE8 and +0x130, indexed by a
/// draw command's pass (4 text, 5 edge, 6 glare, 7 highlight, 8 emboss). A Kernel::PixelShader binds its
/// ID3D11PixelShader at +0x60 (ImmediateContextDX11.SetPixelShader), which is swapped here; the replacement has the same
/// input signature and bindings (t0, s0), so the shader's binding list stays valid.
/// </remarks>
internal sealed unsafe class EdgeShader : IDisposable
{
    private const int ShadersOffset = 0x130;
    private const int PixelShaderFilesOffset = 0x58;
    private const int PixelShadersOffset = 0x130;
    private const int EdgePass = 5;
    private const int D3DShaderOffset = 0x60;

    private ID3D11PixelShader* shader;

    // The Kernel::PixelShader patched, and the game's ID3D11PixelShader it had.
    private nint patchedObject;
    private ID3D11PixelShader* original;

    /// <summary>
    /// Uses the plugin's shader (its radius comes from each font's claimed texture width), or the game's. Framework
    /// thread. Throws if the shader can't be made.
    /// </summary>
    public void Set(bool use)
    {
        if (!use)
        {
            this.Restore();
            return;
        }

        if (this.shader is null)
            this.shader = Compile();
        this.EnsureInstalled();
    }

    /// <summary>Installs the shader again if the game made its shaders again (a device reset). Framework thread.</summary>
    public void EnsureInstalled()
    {
        if (this.shader is null)
            return;

        // The file name is only checked when the object changes.
        var target = GetEdgeShaderObject(false);
        if (target != 0 && target == this.patchedObject && *(ID3D11PixelShader**)(target + D3DShaderOffset) == this.shader)
            return;
        target = GetEdgeShaderObject(true);
        if (target == 0)
            return;

        // The game's object owns a reference to what it binds: it gets one of the plugin's shader.
        this.patchedObject = target;
        this.original = *(ID3D11PixelShader**)(target + D3DShaderOffset);
        this.shader->AddRef();
        *(ID3D11PixelShader**)(target + D3DShaderOffset) = this.shader;
    }

    public void Dispose()
    {
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
    private static nint GetEdgeShaderObject(bool check)
    {
        var module = RaptureAtkModule.Instance();
        var shaders = module is null ? 0 : *(nint*)((nint)module + ShadersOffset);
        if (shaders == 0)
            return 0;
        var file = *(ResourceHandle**)(shaders + PixelShaderFilesOffset + (EdgePass * 8));
        if (check && (file is null || !file->FileName.ToString().EndsWith("FontEdgePS.shcd", StringComparison.OrdinalIgnoreCase)))
            return 0;
        return *(nint*)(shaders + PixelShadersOffset + (EdgePass * 8));
    }

    private static ID3D11PixelShader* Compile()
    {
        using var stream = typeof(EdgeShader).Assembly.GetManifestResourceStream("CustomFonts.Rendering.FontEdgePS.hlsl")
            ?? throw new FileNotFoundException("FontEdgePS.hlsl isn't embedded.");
        using var reader = new StreamReader(stream);
        var source = Encoding.UTF8.GetBytes(reader.ReadToEnd());

        ID3DBlob* code = null;
        ID3DBlob* errors = null;
        try
        {
            fixed (byte* src = source)
            fixed (byte* entry = "main\0"u8)
            fixed (byte* target = "ps_5_0\0"u8)
            {
                var hr = D3DCompile(src, (nuint)source.Length, null, null, null, (sbyte*)entry, (sbyte*)target, D3DCOMPILE.D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
                if (hr.FAILED)
                {
                    var message = errors is null ? string.Empty : new string((sbyte*)errors->GetBufferPointer(), 0, (int)errors->GetBufferSize());
                    throw new InvalidOperationException($"Compiling the edge shader failed ({hr.Value:X8}): {message}");
                }
            }

            var device = (ID3D11Device*)Device.Instance()->D3D11Forwarder;
            ID3D11PixelShader* shader;
            device->CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(), null, &shader).ThrowOnError();
            return shader;
        }
        finally
        {
            if (code is not null)
                code->Release();
            if (errors is not null)
                errors->Release();
        }
    }

    /// <summary>Gives the game its shader back, if the object still binds the plugin's.</summary>
    private void Restore()
    {
        if (this.patchedObject == 0)
            return;
        var slot = (ID3D11PixelShader**)(this.patchedObject + D3DShaderOffset);
        if (GetEdgeShaderObject(false) == this.patchedObject && *slot == this.shader)
        {
            *slot = this.original;
            this.shader->Release();
        }

        this.patchedObject = 0;
        this.original = null;
    }
}
