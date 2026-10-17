// Stands in for the game's shader/sm5/shcd/FontEdgePS.shcd, with its bindings, to dilate by a disc of radius Step.y instead of a fixed one-texel pattern.

// In whole texels on each side of the pixel.
#define MAX_REACH 8

Texture2D g_TextureT : register(t0);
SamplerState g_TextureS : register(s0);

struct PSInput
{
    float4 Position : SV_POSITION;
    float4 Color : COLOR0;

    // One-hot selector of the atlas channel the glyph is in.
    float4 Channel : COLOR1;

    float2 Uv : TEXCOORD0;

    // Samples outside the glyph's rectangle belong to other glyphs.
    float2 RectMin : TEXCOORD1;
    float2 RectMax : TEXCOORD2;

    // (0.5, 1, 0.9, -0.9) times one texel of the font's claimed texture width (FontEdgeVS).
    float4 Step : TEXCOORD3;
};

// A sentinel the plugin and XivAlexander patch in the bytecode, so every use mixes in a run-time value to keep it a lone
// immediate operand.
static const float Softness = 0.123456789f;

// o is in whole texels; the weight is 0 from |o| = rim.z on.
float Weight(float3 rim, float2 o)
{
    return saturate(rim.x - length(o) * rim.y);
}

// The weight is 1 out to (1 - k) * radius and falls linearly, over at least 1 px, to 0 at max(k, 1) * radius within MAX_REACH.
float3 Rim(float radius, float k)
{
    k = k > 1 ? min(k, max((MAX_REACH + 0.5) / radius, 1)) : k;
    float inner = max(1 - k, 0) * radius;
    float outer = max(k, 1) * radius;
    float fade = max(outer - inner, 1);
    float middle = (inner + outer) * 0.5;
    float slope = fade > 1 ? 1 / fade : 1;
    return float3(0.5 + middle * slope, slope, middle + 0.5 * fade);
}

float Coverage(PSInput input, float2 uv)
{
    return dot(g_TextureT.SampleLevel(g_TextureS, uv, 0), input.Channel);
}

// Corrected with the samples' own expression, so that exactly the samples inside the rectangle are taken.
int FirstInside(float uv, float texel, float size, float min, int reach)
{
    int i = (int)clamp(ceil((min - uv) * size), -reach - 1, reach + 1);
    if (uv + (i - 1) * texel >= min)
        i--;
    else if (uv + i * texel < min)
        i++;
    return i;
}

int LastInside(float uv, float texel, float size, float max, int reach)
{
    int i = (int)clamp(floor((max - uv) * size), -reach - 1, reach + 1);
    if (uv + (i + 1) * texel <= max)
        i++;
    else if (uv + i * texel > max)
        i--;
    return i;
}

// Rows go from the centre out (0, -1, 1, -2, 2, ...) because rows further out weigh no more, so the loop can end early.
float Dilate(PSInput input, float2 texel, float2 size, float3 rim, int reach)
{
    int x0 = FirstInside(input.Uv.x, texel.x, size.x, input.RectMin.x, reach);
    int x1 = LastInside(input.Uv.x, texel.x, size.x, input.RectMax.x, reach);
    int y0 = FirstInside(input.Uv.y, texel.y, size.y, input.RectMin.y, reach);
    int y1 = LastInside(input.Uv.y, texel.y, size.y, input.RectMax.y, reach);

    // Texels are UNORM: a sample's coverage is at most this.
    float most = dot(max(input.Channel, 0), 1);

    float alpha = 0;
    [loop]
    for (int i = 0; i <= 2 * reach; i++)
    {
        int y = (i & 1) ? -((i + 1) >> 1) : (i >> 1);
        float fy = y;

        // No later row weighs more; the margin covers the rounding of length().
        if (alpha >= most * saturate(rim.x - abs(fy) * rim.y + 1.0 / 256))
            break;
        if (y < y0 || y > y1)
            continue;

        // sqrt can be one off from the last column weighing more than 0, so Weight itself decides.
        int h = (int)sqrt(max(rim.z * rim.z - fy * fy, 0));
        if (Weight(rim, float2(h + 1, fy)) > 0)
            h++;
        else if (Weight(rim, float2(h, fy)) <= 0)
            h--;
        h = min(h, reach);

        float v = input.Uv.y + fy * texel.y;
        [loop]
        for (int x = max(x0, -h); x <= min(x1, h); x++)
        {
            float2 o = float2(x, fy);
            alpha = max(alpha, Coverage(input, float2(input.Uv.x + o.x * texel.x, v)) * Weight(rim, o));
        }
    }
    return alpha;
}

// R is a literal; zero is a 0 the compiler can't see through, so positions and weights are computed at run time as in the loop.
float DilateSquare(PSInput input, int R, float2 texel, float3 rim, float zero)
{
    float alpha = 0;
    [unroll]
    for (int y = -R; y <= R; y++)
    {
        float fy = y + zero;
        float v = input.Uv.y + fy * texel.y;
        [unroll]
        for (int x = -R; x <= R; x++)
        {
            float fx = x + zero;
            float2 uv = float2(input.Uv.x + fx * texel.x, v);
            float weight = Weight(rim, float2(fx, fy));
            float coverage = Coverage(input, uv);
            if (weight > 0 && all(uv >= input.RectMin) && all(uv <= input.RectMax))
                alpha = max(alpha, coverage * weight);
        }
    }
    return alpha;
}

// c and offset are literals; the components hold texels x = (0, 1), y = (1, 1), z = (1, 0), w = (0, 0).
float4 Gather(int c, float2 at, int2 offset = int2(0, 0))
{
    if (c == 0)
        return g_TextureT.GatherRed(g_TextureS, at, offset);
    if (c == 1)
        return g_TextureT.GatherGreen(g_TextureS, at, offset);
    if (c == 2)
        return g_TextureT.GatherBlue(g_TextureS, at, offset);
    return g_TextureT.GatherAlpha(g_TextureS, at, offset);
}

// The sample at whole-texel offset o blends the texels around corner + o by the same f for every offset.
void Footprint(PSInput input, float2 texel, float2 size, out float2 corner, out float2 f)
{
    float2 p = input.Uv * size - 0.5;
    float2 base = floor(p);
    f = p - base;
    corner = (base + 1) * texel;
}

// c is a literal.
float RowGathered(int c, float alpha, float2 corner, float2 f, float2 texel, float3 rim, float y, float x0, float x1)
{
    // Gathers step two columns, as each holds the footprints of two adjacent samples.
    float2 at = corner + float2(x0, y) * texel;
    float4 g = Gather(c, at);
    float right = lerp(g.z, g.y, f.y);
    alpha = max(alpha, lerp(lerp(g.w, g.x, f.y), right, f.x) * Weight(rim, float2(x0, y)));
    float x = x0 + 1;
    [loop]
    for (; x + 2 < x1; x += 4)
    {
        // Four samples a step, so that their two gathers are fetched together.
        float4 g1 = Gather(c, at + float2(2 * texel.x, 0));
        float4 g2 = Gather(c, at + float2(4 * texel.x, 0));
        at.x += 4 * texel.x;
        float4 columns = lerp(float4(g1.w, g1.z, g2.w, g2.z), float4(g1.x, g1.y, g2.x, g2.y), f.y);
        float4 o = x + float4(0, 1, 2, 3);
        float4 s = lerp(float4(right, columns.xyz), columns, f.x) * saturate(rim.x - sqrt(o * o + y * y) * rim.y);
        alpha = max(alpha, max(max(s.x, s.y), max(s.z, s.w)));
        right = columns.w;
    }
    if (x < x1)
    {
        at.x += 2 * texel.x;
        g = Gather(c, at);
        float next = lerp(g.w, g.x, f.y);
        float after = lerp(g.z, g.y, f.y);
        alpha = max(alpha, max(lerp(right, next, f.x) * Weight(rim, float2(x, y)),
            lerp(next, after, f.x) * Weight(rim, float2(x + 1, y))));
        right = after;
        x += 2;
    }
    if (x == x1)
    {
        at.x += 2 * texel.x;
        g = Gather(c, at);
        alpha = max(alpha, lerp(right, lerp(g.w, g.x, f.y), f.x) * Weight(rim, float2(x, y)));
    }
    return alpha;
}

// c is a literal; one channel's coverage is at most 1, so the loop ends once no row left can raise alpha.
float DilateGathered(PSInput input, int c, float2 texel, float2 size, float3 rim, int reach)
{
    float x0 = FirstInside(input.Uv.x, texel.x, size.x, input.RectMin.x, reach);
    float x1 = LastInside(input.Uv.x, texel.x, size.x, input.RectMax.x, reach);
    float y0 = FirstInside(input.Uv.y, texel.y, size.y, input.RectMin.y, reach);
    float y1 = LastInside(input.Uv.y, texel.y, size.y, input.RectMax.y, reach);
    float2 corner, f;
    Footprint(input, texel, size, corner, f);

    float alpha = 0;
    [loop]
    for (float k = 0; k <= reach; k++)
    {
        if (alpha >= saturate(rim.x - k * rim.y + 1.0 / 256))
            break;

        // The 1/256 margin keeps sqrt's rounding from dropping a column that weighs more than 0.
        float outer = min(floor(sqrt(max(rim.z * rim.z - k * k, 0)) + 1.0 / 256), reach);
        float xa = max(x0, -outer), xb = min(x1, outer);
        if (xa > xb)
            continue;
        if (-k >= y0 && -k <= y1)
            alpha = RowGathered(c, alpha, corner, f, texel, rim, -k, xa, xb);
        if (k > 0 && k >= y0 && k <= y1)
            alpha = RowGathered(c, alpha, corner, f, texel, rim, k, xa, xb);
    }
    return alpha;
}

#include "FontEdgeSquares.hlsli"

float4 main(PSInput input) : SV_TARGET
{
    float width, height;
    g_TextureT.GetDimensions(width, height);
    float2 size = float2(width, height);
    float2 texel = 1 / size;
    float radius = input.Step.y * width;
    float zero = min(input.Step.y, 0);
    // zero's bits are 0, which the compiler can't tell: Softness doesn't fold into other constants.
    float3 rim = Rim(radius, asfloat(asuint(Softness) + asuint(zero)));
    int reach = min((int)ceil(max(radius, rim.z - 0.5)), MAX_REACH);

    // ceil(rim.z) - 1 can be one short of the furthest offset weighing more than 0, so Weight itself decides.
    int r = min((int)ceil(rim.z) - 1, reach);
    if (r < reach && Weight(rim, float2(r + 1 + zero, zero)) > 0)
        r++;

    // Gather (within 1/255 of SampleLevel) reads one channel, so a Channel that isn't one-hot (4) takes the SampleLevel paths.
    float4 channel = input.Channel;
    int c = all(channel == float4(1, 0, 0, 0)) ? 0
        : all(channel == float4(0, 1, 0, 0)) ? 1
        : all(channel == float4(0, 0, 1, 0)) ? 2
        : all(channel == float4(0, 0, 0, 1)) ? 3
        : 4;

    float alpha;
    [branch]
    if (r <= 0)
    {
        alpha = DilateSquare(input, 0, texel, rim, zero);
    }
    else if (c == 4)
    {
        [branch]
        if (r == 1)
            alpha = DilateSquare(input, 1, texel, rim, zero);
        else
            alpha = Dilate(input, texel, size, rim, reach);
    }
    else if (r <= SQUARE_REACH)
    {
        alpha = DilateGatheredSquare(input, c, r, texel, size, rim);
    }
    else
    {
        [branch]
        switch (c)
        {
        case 0:
            alpha = DilateGathered(input, 0, texel, size, rim, reach);
            break;
        case 1:
            alpha = DilateGathered(input, 1, texel, size, rim, reach);
            break;
        case 2:
            alpha = DilateGathered(input, 2, texel, size, rim, reach);
            break;
        default:
            alpha = DilateGathered(input, 3, texel, size, rim, reach);
            break;
        }
    }

    return float4(input.Color.rgb, input.Color.a * alpha);
}
