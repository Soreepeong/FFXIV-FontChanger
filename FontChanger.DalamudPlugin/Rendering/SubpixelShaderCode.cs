namespace CustomFonts;

/// <summary>FontSubpixelPS.hlsl's settings, patched in with <see cref="EdgeShaderCode.PatchConstants"/>.</summary>
/// <remarks>Also compiled into FontChanger.ShaderGen, which checks the patching against compiling with the values written in.</remarks>
internal static class SubpixelShaderCode
{
    /// <summary>The bits of <c>ClearTypeLevel</c> (0.234567891f).</summary>
    public const uint ClearTypeLevelSentinel = 0x3E703291;

    /// <summary>The bits of <c>Contrast</c> (0.345678912f).</summary>
    public const uint ContrastSentinel = 0x3EB0FCD4;
}
