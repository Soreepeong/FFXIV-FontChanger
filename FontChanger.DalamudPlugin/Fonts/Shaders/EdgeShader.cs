using System;

using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Shaders;

internal sealed unsafe class EdgeShader : IDisposable
{
    public const float MaxSoftness = 8;

    private readonly PixelShaderSwap _swap = new();

    // Null for the game's shader.
    private float? _softness;

    /// <summary>
    /// The plugin's shader draws a round edge of any width unless edges are the game's own 1 px. The game's shader, widened only
    /// by claiming a narrower texture (blocky past about 1.5 px), is the fallback.
    /// </summary>
    /// <remarks><see cref="Ensure"/> gets the shader in.</remarks>
    public void Apply(EdgeSettings value)
    {
        value = value.Drawn();
        this.SetSoftness(value.Enabled && value != new EdgeSettings { Enabled = true } ? value.Softness : null);
    }

    /// <summary>
    /// Once a frame, as the game takes the shader out when it loads its shaders again, and has none before it first loads them.
    /// </summary>
    /// <remarks>If the shader can't be made, the game's stays until another value is applied.</remarks>
    public void Ensure()
    {
        Misc.DebugAssertMainThread();
        if (this._softness is not { } softness)
            return;
        var server = PixelShaderSwap.GetServer();
        if (server is null)
            return;
        try
        {
            this._swap.Ensure(
                server->FontEdgePS,
                (ResourceHandle*)server->FontEdgePSResource,
                "FontEdgePS.shcd",
                "FontChanger.DalamudPlugin.Fonts.Shaders.FontEdgePS.cso",
                (ShaderCode.SoftnessSentinel, softness));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Using the plugin's edge shader failed; edges are widened with the game's");
            this.SetSoftness(null);
        }
    }

    public void Dispose() => this.SetSoftness(null);

    /// <summary>Null restores the game's shader. A shader made for another softness goes.</summary>
    private void SetSoftness(float? value)
    {
        Misc.DebugAssertMainThread();
        if (value == this._softness)
            return;

        var server = PixelShaderSwap.GetServer();
        this._swap.Release(server is null ? null : server->FontEdgePS);
        this._softness = value;
    }
}
