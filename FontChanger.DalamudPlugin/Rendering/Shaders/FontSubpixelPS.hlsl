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

// IDWriteRenderingParams::GetGamma: the display's, which blending corrects for; 1 to 2.2.
static const float Gamma = 0.456789123f;

// Glyphs are drawn with coverage to the power of 1 / their element's gamma, and presets tuned 1.4 as if blending were
// gamma-correct; this takes their coverage back to that of a gamma of 1.
static const float BaselineGamma = 1.4f;

// DirectWrite's blending, as Windows Terminal's dwrite_helpers.hlsl and .cpp have it (DWrite_ClearTypeBlend,
// DWrite_GrayscaleBlend).
// DWrite_GetGammaRatios' table for gammas 1.0 to 2.2 in steps of 0.1, and its normalization (x and z by 0x10000 / 255²,
// y and w by 0x100 / 255).
static const float4 GammaRatioScale = float4(65536.0 / 65025, 256.0 / 255, 65536.0 / 65025, 256.0 / 255);
static const float4 GammaRatios[13] =
{
    float4(0.0000f, 0.0000f, 0.0000f, 0.0000f),
    float4(0.0166f, -0.0807f, 0.2227f, -0.0751f),
    float4(0.0350f, -0.1760f, 0.4325f, -0.1370f),
    float4(0.0543f, -0.2821f, 0.6302f, -0.1876f),
    float4(0.0739f, -0.3963f, 0.8167f, -0.2287f),
    float4(0.0933f, -0.5161f, 0.9926f, -0.2616f),
    float4(0.1121f, -0.6395f, 1.1588f, -0.2877f),
    float4(0.1300f, -0.7649f, 1.3159f, -0.3080f),
    float4(0.1469f, -0.8911f, 1.4644f, -0.3234f),
    float4(0.1627f, -1.0170f, 1.6051f, -0.3347f),
    float4(0.1773f, -1.1420f, 1.7385f, -0.3426f),
    float4(0.1908f, -1.2652f, 1.8650f, -0.3476f),
    float4(0.2031f, -1.3864f, 1.9851f, -0.3501f),
};

// DirectWrite takes the nearest row; between rows, this interpolates, so gamma changes smoothly.
float4 GetGammaRatios(float gamma)
{
    float position = clamp(gamma, 1, 2.2) * 10 - 10;
    uint row = min((uint)position, 11);
    return lerp(GammaRatios[row], GammaRatios[row + 1], position - row) * GammaRatioScale;
}

// DWrite_ApplyLightOnDarkContrastAdjustment: no contrast for light text, full from a lightness of 0.5 down.
float AdjustContrastLightOnDark(float contrast, float3 color)
{
    return contrast * saturate(dot(color, float3(0.30f, 0.59f, 0.11f) * -4.0f) + 3.0f);
}

// DWrite_ApplyAlphaCorrection: coverage that, blended as the encoded values are, approximates blending in linear light,
// by the text color alone.
float3 CorrectAlpha(float3 a, float3 f, float4 g)
{
    return a + a * (1 - a) * ((g.x * f + g.y) * a + (g.z * f + g.w));
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

    // ClearType corrects each subpixel by the color's own channel, grayscale by the color's intensity.
    texel = pow(texel, BaselineGamma);
    float3 coverage = unscaled ? lerp(texel.aaa, texel.rgb, level) : texel.aaa;
    float3 corrected = unscaled ? color : dot(color, float3(0.25f, 0.5f, 0.25f));
    k = AdjustContrastLightOnDark(k, color);
    coverage = coverage * (k + 1) / (coverage * k + 1);
    coverage = saturate(CorrectAlpha(coverage, corrected, GetGammaRatios(gamma)));
    float alpha = input.Color.a;
    output.Color = float4(coverage * alpha, max(max(coverage.r, coverage.g), coverage.b) * alpha);
    return output;
}
