// FontPS/FontEmbossPS (FontHighlightPS with HIGHLIGHT) as the game has them on even-width textures; on odd-width subpixel pages, per-subpixel
// coverage in Color.rgb and the text color in Blend.rgb, for SubpixelText's SRC1_COLOR / INV_SRC_COLOR blend.
// FontChanger.ShaderGen --subpixel compiles it, and with HIGHLIGHT defined; XivAlexander compiles it with fxc the same way.

Texture2D g_TextureT : register(t0);
SamplerState g_TextureS : register(s0);

// FontVS's outputs, in the same order.
struct PSInput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;
    float4 Channel : COLOR1;
    float2 Uv : TEXCOORD0;
};

struct PSOutput
{
    float4 Color : SV_Target0;
    float4 Blend : SV_Target1;
};

// Sentinels SubpixelText replaces in the bytecode, as FontEdgePS.hlsl's Softness.
// IDWriteRenderingParams::GetClearTypeLevel, 0 to 1.
static const float ClearTypeLevel = 0.234567891f;

// IDWriteRenderingParams::GetEnhancedContrast: coverage * (k + 1) / (coverage * k + 1).
static const float Contrast = 0.345678912f;

// IDWriteRenderingParams::GetGamma: the display's, which blending corrects for.
static const float Gamma = 0.456789123f;

// Glyphs are drawn with coverage to the power of 1 / their element's gamma, and presets tuned 1.4 as if blending were
// gamma-correct; this takes their coverage back to that of a gamma of 1.
static const float BaselineGamma = 1.4f;

// Coverage that, blended as the encoded values are, gives what blending in linear light would; exact on a background of
// the opposite luminance, which is assumed, as Direct2D and Skia do.
float3 CorrectGamma(float3 coverage, float luminance, float gamma)
{
    float background = 1 - luminance;
    float span = luminance - background;
    float3 blended = lerp(pow(background, gamma), pow(luminance, gamma), coverage);
    return abs(span) < 1.0 / 256 ? coverage : saturate((pow(blended, 1 / gamma) - background) / span);
}

PSOutput main(PSInput input)
{
    PSOutput output;
#ifdef HIGHLIGHT
    float3 color = 1 - input.Color.rgb;
#else
    float3 color = input.Color.rgb;
#endif
    output.Blend = float4(color, 1);

    uint width, height;
    g_TextureT.GetDimensions(width, height);
    float4 texel = g_TextureT.SampleLevel(g_TextureS, input.Uv, 0);

    // Before the branch: derivatives need the whole quad.
    float2 size = float2(width, height);
    float2 perPixelX = ddx(input.Uv) * size;
    float2 perPixelY = ddy(input.Uv) * size;

    [branch]
    if ((width & 1) == 0)
    {
        output.Color = float4(color, dot(texel, input.Channel) * input.Color.a);
        return output;
    }

    // 0 at run time, unknown to the compiler, so the sentinels stay immediate operands.
    float zero = min(input.Uv.x, 0);
    float level = saturate(asfloat(asuint(ClearTypeLevel) + asuint(zero)));
    float k = max(asfloat(asuint(Contrast) + asuint(zero)), 0);
    float gamma = max(asfloat(asuint(Gamma) + asuint(zero)), 1);

    bool unscaled = all(abs(abs(perPixelX) + abs(perPixelY) - 1) < 1.0 / 64);

    texel = pow(texel, BaselineGamma);
    float3 coverage = unscaled ? lerp(texel.aaa, texel.rgb, level) : texel.aaa;
    coverage = coverage * (k + 1) / (coverage * k + 1);
    coverage = CorrectGamma(coverage, dot(color, float3(0.2126, 0.7152, 0.0722)), gamma);
    float alpha = input.Color.a;
    output.Color = float4(coverage * alpha, max(max(coverage.r, coverage.g), coverage.b) * alpha);
    return output;
}
