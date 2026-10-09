// Replacement for the game's FontEdgePS (shader/sm5/shcd/FontEdgePS.shcd): the same inputs and bindings, but the edge
// is the glyph's coverage dilated by a disc, antialiased at its rim, instead of the game's fixed pattern of about one
// texel. The disc's radius comes from the vertex data: FontEdgeVS passes one texel of the font's claimed texture width
// (Step.y), which the plugin sets per font to radius / (atlas width); the atlas's own texel comes from the texture.

// Most whole texels sampled on each side of the pixel.
#define MAX_REACH 8

Texture2D g_TextureT : register(t0);
SamplerState g_TextureS : register(s0);

struct PSInput
{
    float4 Position : SV_POSITION;

    // The edge color.
    float4 Color : COLOR0;

    // Selects the channel the glyph is in (R, G, B or A of the atlas).
    float4 Channel : COLOR1;

    float2 Uv : TEXCOORD0;

    // The glyph's rectangle in the atlas: samples outside it belong to other glyphs.
    float2 RectMin : TEXCOORD1;
    float2 RectMax : TEXCOORD2;

    // (0.5, 1, 0.9, -0.9) times one texel of the claimed width (FontEdgeVS).
    float4 Step : TEXCOORD3;
};

float Coverage(PSInput input, float2 uv)
{
    if (any(uv < input.RectMin) || any(uv > input.RectMax))
        return 0;
    return dot(g_TextureT.SampleLevel(g_TextureS, uv, 0), input.Channel);
}

float4 main(PSInput input) : SV_TARGET
{
    float width, height;
    g_TextureT.GetDimensions(width, height);
    float2 texel = float2(1 / width, 1 / height);
    float radius = input.Step.y * width;
    int reach = min((int)ceil(radius), MAX_REACH);

    float alpha = 0;
    [loop]
    for (int y = -reach; y <= reach; y++)
    {
        [loop]
        for (int x = -reach; x <= reach; x++)
        {
            // Weighted down at the rim of the disc, for a round, antialiased outline.
            float weight = saturate(radius + 0.5 - length(float2(x, y)));
            if (weight > 0)
                alpha = max(alpha, Coverage(input, input.Uv + (float2(x, y) * texel)) * weight);
        }
    }

    return float4(input.Color.rgb, input.Color.a * alpha);
}
