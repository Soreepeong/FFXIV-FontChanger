// Replacement for the game's FontEdgePS (shader/sm5/shcd/FontEdgePS.shcd): the same inputs and bindings, but the edge
// is the glyph's coverage dilated by a disc, antialiased at its rim, instead of the game's fixed pattern of about one
// texel. The radius comes from the vertex data (Step.y, one texel of the font's claimed texture width).
//
// alpha is the most of Coverage(Uv + o) * Weight(o) over the whole-texel offsets o up to MAX_REACH, within the glyph's
// rectangle; Weight falls linearly with |o| as Softness sets. One-hot channels are blended from Gather, within 1/255 of SampleLevel.
// The plugin's build compiles this with FontChanger.ShaderGen, and embeds the bytecode.

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

// The edge's softness k, a distinctive value that the plugin and XivAlexander replace in the bytecode: every use of it
// is with a value known only at run time, so that it stays an immediate operand of its own.
static const float Softness = 0.123456789f;

// The weight of the sample at offset o (whole texels): saturate(rim.x - |o| * rim.y), which is 0 from |o| = rim.z on.
float Weight(float3 rim, float2 o)
{
    return saturate(rim.x - length(o) * rim.y);
}

// The weight's terms for softness k: 1 to (1 - k) * radius, falling linearly over k * radius (at least 1 px, centred
// there) to 0 at radius (k <= 1) or k * radius (k > 1, as far as MAX_REACH takes it). k = 0 is a disc antialiased at
// its rim: rim = (radius + 0.5, 1, radius + 0.5).
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

// The sample's coverage, for any Channel.
float Coverage(PSInput input, float2 uv)
{
    return dot(g_TextureT.SampleLevel(g_TextureS, uv, 0), input.Channel);
}

// The first offset whose sample is at or past min (RectMin): estimated, then corrected by one either way with the
// samples' own expression, so that exactly the samples inside the rectangle are taken.
int FirstInside(float uv, float texel, float size, float min, int reach)
{
    int i = (int)clamp(ceil((min - uv) * size), -reach - 1, reach + 1);
    if (uv + (i - 1) * texel >= min)
        i--;
    else if (uv + i * texel < min)
        i++;
    return i;
}

// The last offset whose sample is at or before max (RectMax).
int LastInside(float uv, float texel, float size, float max, int reach)
{
    int i = (int)clamp(floor((max - uv) * size), -reach - 1, reach + 1);
    if (uv + (i + 1) * texel <= max)
        i++;
    else if (uv + i * texel > max)
        i--;
    return i;
}

// Any Channel: rows from the centre out (0, -1, 1, -2, 2, ...), each within the disc and the rectangle's columns. Rows
// further out weigh no more, so once alpha reaches the most a row can give, the loop ends.
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

        // The most any sample of this row or the rows after it weighs (the margin covers the rounding of length()).
        if (alpha >= most * saturate(rim.x - abs(fy) * rim.y + 1.0 / 256))
            break;
        if (y < y0 || y > y1)
            continue;

        // The row's columns weighing more than 0 are |x| <= h: estimated, then corrected by one either way.
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

// Any Channel, a reach of R (a literal, 0 or 1), unrolled, with the rectangle test per sample. zero is a 0 the compilers
// can't see through, so that positions and weights are computed at run time, as in the loop.
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

// One channel (c, a literal) of the 2x2 texels at a texel corner, offset by whole texels (a literal): x = (0, 1),
// y = (1, 1), z = (1, 0), w = (0, 0).
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

// The bilinear footprints of the samples at whole-texel offsets from Uv: offset o blends the texels around the corner
// at corner + o by f, the same for every offset.
void Footprint(PSInput input, float2 texel, float2 size, out float2 corner, out float2 f)
{
    float2 p = input.Uv * size - 0.5;
    float2 base = floor(p);
    f = p - base;
    corner = (base + 1) * texel;
}

// Row y's samples x0 .. x1 of channel c (a literal), blended from gathers along the row.
float RowGathered(int c, float alpha, float2 corner, float2 f, float2 texel, float3 rim, float y, float x0, float x1)
{
    // The first gather holds sample x0's footprint; each further one, two columns on, the next two samples'.
    float2 at = corner + float2(x0, y) * texel;
    float4 g = Gather(c, at);
    float right = lerp(g.z, g.y, f.y);
    alpha = max(alpha, lerp(lerp(g.w, g.x, f.y), right, f.x) * Weight(rim, float2(x0, y)));
    float x = x0 + 1;
    [loop]
    for (; x + 2 < x1; x += 4)
    {
        // Samples x .. x + 3 from the last column so far and two gathers, fetched together.
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

// Channel c (a literal), from gathers: rows from the centre out, until no row left can raise alpha (coverage is at most
// 1). Rows -k and k share their setup; |x| <= outer is a superset of the columns weighing more than 0.
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

    // The furthest whole offset weighing more than 0, from the weight itself.
    int r = min((int)ceil(rim.z) - 1, reach);
    if (r < reach && Weight(rim, float2(r + 1 + zero, zero)) > 0)
        r++;

    // The channel the glyph is in, 0 to 3 for a one-hot Channel; 4 for any other.
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
