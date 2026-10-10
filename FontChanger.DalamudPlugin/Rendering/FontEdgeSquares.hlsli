// SQUARE_REACH and DilateGatheredSquare for FontEdgePS.hlsl, written by FontChanger.ShaderGen --write-include; do
// not edit by hand.

#define SQUARE_REACH 4

// Reaches 1 to 4 for a one-hot Channel c (a literal), one function per reach: the (2R + 1)^2 samples
// around Uv from the (R + 1)^2 gathers at the texel corners around them, every texel gathered once.
float DilateGathered1(PSInput input, int c, float2 texel, float2 size, float rim)
{
    float2 corner, f;
    Footprint(input, texel, size, corner, f);
    float u0 = input.Uv.x - texel.x, v0 = input.Uv.y - texel.y;
    bool column0 = u0 >= input.RectMin.x && u0 <= input.RectMax.x;
    bool row0 = v0 >= input.RectMin.y && v0 <= input.RectMax.y;
    float u1 = input.Uv.x, v1 = input.Uv.y;
    bool column1 = u1 >= input.RectMin.x && u1 <= input.RectMax.x;
    bool row1 = v1 >= input.RectMin.y && v1 <= input.RectMax.y;
    float u2 = input.Uv.x + texel.x, v2 = input.Uv.y + texel.y;
    bool column2 = u2 >= input.RectMin.x && u2 <= input.RectMax.x;
    bool row2 = v2 >= input.RectMin.y && v2 <= input.RectMax.y;
    float4 g0_0 = Gather(c, corner, int2(-1, -1));
    float4 g0_2 = Gather(c, corner, int2(1, -1));
    float4 g2_0 = Gather(c, corner, int2(-1, 1));
    float4 g2_2 = Gather(c, corner, int2(1, 1));
    float alpha = 0;
    float k0_0 = lerp(g0_0.w, g0_0.x, f.y);
    float k0_1 = lerp(g0_0.z, g0_0.y, f.y);
    float k0_2 = lerp(g0_2.w, g0_2.x, f.y);
    float k0_3 = lerp(g0_2.z, g0_2.y, f.y);
    float w2 = saturate(rim - 1.414213562);
    if (row0 && column0)
        alpha = max(alpha, lerp(k0_0, k0_1, f.x) * w2);
    float w1 = saturate(rim - 1.000000000);
    if (row0 && column1)
        alpha = max(alpha, lerp(k0_1, k0_2, f.x) * w1);
    if (row0 && column2)
        alpha = max(alpha, lerp(k0_2, k0_3, f.x) * w2);
    float k1_0 = lerp(g0_0.x, g2_0.w, f.y);
    float k1_1 = lerp(g0_0.y, g2_0.z, f.y);
    float k1_2 = lerp(g0_2.x, g2_2.w, f.y);
    float k1_3 = lerp(g0_2.y, g2_2.z, f.y);
    if (row1 && column0)
        alpha = max(alpha, lerp(k1_0, k1_1, f.x) * w1);
    float w0 = saturate(rim - 0.000000000);
    if (row1 && column1)
        alpha = max(alpha, lerp(k1_1, k1_2, f.x) * w0);
    if (row1 && column2)
        alpha = max(alpha, lerp(k1_2, k1_3, f.x) * w1);
    float k2_0 = lerp(g2_0.w, g2_0.x, f.y);
    float k2_1 = lerp(g2_0.z, g2_0.y, f.y);
    float k2_2 = lerp(g2_2.w, g2_2.x, f.y);
    float k2_3 = lerp(g2_2.z, g2_2.y, f.y);
    if (row2 && column0)
        alpha = max(alpha, lerp(k2_0, k2_1, f.x) * w2);
    if (row2 && column1)
        alpha = max(alpha, lerp(k2_1, k2_2, f.x) * w1);
    if (row2 && column2)
        alpha = max(alpha, lerp(k2_2, k2_3, f.x) * w2);
    return alpha;
}

float DilateGathered2(PSInput input, int c, float2 texel, float2 size, float rim)
{
    float2 corner, f;
    Footprint(input, texel, size, corner, f);
    float u0 = input.Uv.x - 2 * texel.x, v0 = input.Uv.y - 2 * texel.y;
    bool column0 = u0 >= input.RectMin.x && u0 <= input.RectMax.x;
    bool row0 = v0 >= input.RectMin.y && v0 <= input.RectMax.y;
    float u1 = input.Uv.x - texel.x, v1 = input.Uv.y - texel.y;
    bool column1 = u1 >= input.RectMin.x && u1 <= input.RectMax.x;
    bool row1 = v1 >= input.RectMin.y && v1 <= input.RectMax.y;
    float u2 = input.Uv.x, v2 = input.Uv.y;
    bool column2 = u2 >= input.RectMin.x && u2 <= input.RectMax.x;
    bool row2 = v2 >= input.RectMin.y && v2 <= input.RectMax.y;
    float u3 = input.Uv.x + texel.x, v3 = input.Uv.y + texel.y;
    bool column3 = u3 >= input.RectMin.x && u3 <= input.RectMax.x;
    bool row3 = v3 >= input.RectMin.y && v3 <= input.RectMax.y;
    float u4 = input.Uv.x + 2 * texel.x, v4 = input.Uv.y + 2 * texel.y;
    bool column4 = u4 >= input.RectMin.x && u4 <= input.RectMax.x;
    bool row4 = v4 >= input.RectMin.y && v4 <= input.RectMax.y;
    float4 g0_0 = Gather(c, corner, int2(-2, -2));
    float4 g0_2 = Gather(c, corner, int2(0, -2));
    float4 g0_4 = Gather(c, corner, int2(2, -2));
    float4 g2_0 = Gather(c, corner, int2(-2, 0));
    float4 g2_2 = Gather(c, corner, int2(0, 0));
    float4 g2_4 = Gather(c, corner, int2(2, 0));
    float4 g4_0 = Gather(c, corner, int2(-2, 2));
    float4 g4_2 = Gather(c, corner, int2(0, 2));
    float4 g4_4 = Gather(c, corner, int2(2, 2));
    float alpha = 0;
    float k0_0 = lerp(g0_0.w, g0_0.x, f.y);
    float k0_1 = lerp(g0_0.z, g0_0.y, f.y);
    float k0_2 = lerp(g0_2.w, g0_2.x, f.y);
    float k0_3 = lerp(g0_2.z, g0_2.y, f.y);
    float k0_4 = lerp(g0_4.w, g0_4.x, f.y);
    float k0_5 = lerp(g0_4.z, g0_4.y, f.y);
    float w8 = saturate(rim - 2.828427125);
    if (row0 && column0)
        alpha = max(alpha, lerp(k0_0, k0_1, f.x) * w8);
    float w5 = saturate(rim - 2.236067977);
    if (row0 && column1)
        alpha = max(alpha, lerp(k0_1, k0_2, f.x) * w5);
    float w4 = saturate(rim - 2.000000000);
    if (row0 && column2)
        alpha = max(alpha, lerp(k0_2, k0_3, f.x) * w4);
    if (row0 && column3)
        alpha = max(alpha, lerp(k0_3, k0_4, f.x) * w5);
    if (row0 && column4)
        alpha = max(alpha, lerp(k0_4, k0_5, f.x) * w8);
    float k1_0 = lerp(g0_0.x, g2_0.w, f.y);
    float k1_1 = lerp(g0_0.y, g2_0.z, f.y);
    float k1_2 = lerp(g0_2.x, g2_2.w, f.y);
    float k1_3 = lerp(g0_2.y, g2_2.z, f.y);
    float k1_4 = lerp(g0_4.x, g2_4.w, f.y);
    float k1_5 = lerp(g0_4.y, g2_4.z, f.y);
    if (row1 && column0)
        alpha = max(alpha, lerp(k1_0, k1_1, f.x) * w5);
    float w2 = saturate(rim - 1.414213562);
    if (row1 && column1)
        alpha = max(alpha, lerp(k1_1, k1_2, f.x) * w2);
    float w1 = saturate(rim - 1.000000000);
    if (row1 && column2)
        alpha = max(alpha, lerp(k1_2, k1_3, f.x) * w1);
    if (row1 && column3)
        alpha = max(alpha, lerp(k1_3, k1_4, f.x) * w2);
    if (row1 && column4)
        alpha = max(alpha, lerp(k1_4, k1_5, f.x) * w5);
    float k2_0 = lerp(g2_0.w, g2_0.x, f.y);
    float k2_1 = lerp(g2_0.z, g2_0.y, f.y);
    float k2_2 = lerp(g2_2.w, g2_2.x, f.y);
    float k2_3 = lerp(g2_2.z, g2_2.y, f.y);
    float k2_4 = lerp(g2_4.w, g2_4.x, f.y);
    float k2_5 = lerp(g2_4.z, g2_4.y, f.y);
    if (row2 && column0)
        alpha = max(alpha, lerp(k2_0, k2_1, f.x) * w4);
    if (row2 && column1)
        alpha = max(alpha, lerp(k2_1, k2_2, f.x) * w1);
    if (row2 && column2)
        alpha = max(alpha, lerp(k2_2, k2_3, f.x));
    if (row2 && column3)
        alpha = max(alpha, lerp(k2_3, k2_4, f.x) * w1);
    if (row2 && column4)
        alpha = max(alpha, lerp(k2_4, k2_5, f.x) * w4);
    float k3_0 = lerp(g2_0.x, g4_0.w, f.y);
    float k3_1 = lerp(g2_0.y, g4_0.z, f.y);
    float k3_2 = lerp(g2_2.x, g4_2.w, f.y);
    float k3_3 = lerp(g2_2.y, g4_2.z, f.y);
    float k3_4 = lerp(g2_4.x, g4_4.w, f.y);
    float k3_5 = lerp(g2_4.y, g4_4.z, f.y);
    if (row3 && column0)
        alpha = max(alpha, lerp(k3_0, k3_1, f.x) * w5);
    if (row3 && column1)
        alpha = max(alpha, lerp(k3_1, k3_2, f.x) * w2);
    if (row3 && column2)
        alpha = max(alpha, lerp(k3_2, k3_3, f.x) * w1);
    if (row3 && column3)
        alpha = max(alpha, lerp(k3_3, k3_4, f.x) * w2);
    if (row3 && column4)
        alpha = max(alpha, lerp(k3_4, k3_5, f.x) * w5);
    float k4_0 = lerp(g4_0.w, g4_0.x, f.y);
    float k4_1 = lerp(g4_0.z, g4_0.y, f.y);
    float k4_2 = lerp(g4_2.w, g4_2.x, f.y);
    float k4_3 = lerp(g4_2.z, g4_2.y, f.y);
    float k4_4 = lerp(g4_4.w, g4_4.x, f.y);
    float k4_5 = lerp(g4_4.z, g4_4.y, f.y);
    if (row4 && column0)
        alpha = max(alpha, lerp(k4_0, k4_1, f.x) * w8);
    if (row4 && column1)
        alpha = max(alpha, lerp(k4_1, k4_2, f.x) * w5);
    if (row4 && column2)
        alpha = max(alpha, lerp(k4_2, k4_3, f.x) * w4);
    if (row4 && column3)
        alpha = max(alpha, lerp(k4_3, k4_4, f.x) * w5);
    if (row4 && column4)
        alpha = max(alpha, lerp(k4_4, k4_5, f.x) * w8);
    return alpha;
}

float DilateGathered3(PSInput input, int c, float2 texel, float2 size, float rim)
{
    float2 corner, f;
    Footprint(input, texel, size, corner, f);
    float u0 = input.Uv.x - 3 * texel.x, v0 = input.Uv.y - 3 * texel.y;
    bool column0 = u0 >= input.RectMin.x && u0 <= input.RectMax.x;
    bool row0 = v0 >= input.RectMin.y && v0 <= input.RectMax.y;
    float u1 = input.Uv.x - 2 * texel.x, v1 = input.Uv.y - 2 * texel.y;
    bool column1 = u1 >= input.RectMin.x && u1 <= input.RectMax.x;
    bool row1 = v1 >= input.RectMin.y && v1 <= input.RectMax.y;
    float u2 = input.Uv.x - texel.x, v2 = input.Uv.y - texel.y;
    bool column2 = u2 >= input.RectMin.x && u2 <= input.RectMax.x;
    bool row2 = v2 >= input.RectMin.y && v2 <= input.RectMax.y;
    float u3 = input.Uv.x, v3 = input.Uv.y;
    bool column3 = u3 >= input.RectMin.x && u3 <= input.RectMax.x;
    bool row3 = v3 >= input.RectMin.y && v3 <= input.RectMax.y;
    float u4 = input.Uv.x + texel.x, v4 = input.Uv.y + texel.y;
    bool column4 = u4 >= input.RectMin.x && u4 <= input.RectMax.x;
    bool row4 = v4 >= input.RectMin.y && v4 <= input.RectMax.y;
    float u5 = input.Uv.x + 2 * texel.x, v5 = input.Uv.y + 2 * texel.y;
    bool column5 = u5 >= input.RectMin.x && u5 <= input.RectMax.x;
    bool row5 = v5 >= input.RectMin.y && v5 <= input.RectMax.y;
    float u6 = input.Uv.x + 3 * texel.x, v6 = input.Uv.y + 3 * texel.y;
    bool column6 = u6 >= input.RectMin.x && u6 <= input.RectMax.x;
    bool row6 = v6 >= input.RectMin.y && v6 <= input.RectMax.y;
    float4 g0_0 = Gather(c, corner, int2(-3, -3));
    float4 g0_2 = Gather(c, corner, int2(-1, -3));
    float4 g0_4 = Gather(c, corner, int2(1, -3));
    float4 g0_6 = Gather(c, corner, int2(3, -3));
    float4 g2_0 = Gather(c, corner, int2(-3, -1));
    float4 g2_2 = Gather(c, corner, int2(-1, -1));
    float4 g2_4 = Gather(c, corner, int2(1, -1));
    float4 g2_6 = Gather(c, corner, int2(3, -1));
    float4 g4_0 = Gather(c, corner, int2(-3, 1));
    float4 g4_2 = Gather(c, corner, int2(-1, 1));
    float4 g4_4 = Gather(c, corner, int2(1, 1));
    float4 g4_6 = Gather(c, corner, int2(3, 1));
    float4 g6_0 = Gather(c, corner, int2(-3, 3));
    float4 g6_2 = Gather(c, corner, int2(-1, 3));
    float4 g6_4 = Gather(c, corner, int2(1, 3));
    float4 g6_6 = Gather(c, corner, int2(3, 3));
    float alpha = 0;
    float k0_0 = lerp(g0_0.w, g0_0.x, f.y);
    float k0_1 = lerp(g0_0.z, g0_0.y, f.y);
    float k0_2 = lerp(g0_2.w, g0_2.x, f.y);
    float k0_3 = lerp(g0_2.z, g0_2.y, f.y);
    float k0_4 = lerp(g0_4.w, g0_4.x, f.y);
    float k0_5 = lerp(g0_4.z, g0_4.y, f.y);
    float k0_6 = lerp(g0_6.w, g0_6.x, f.y);
    float k0_7 = lerp(g0_6.z, g0_6.y, f.y);
    float w13 = saturate(rim - 3.605551275);
    if (row0 && column1)
        alpha = max(alpha, lerp(k0_1, k0_2, f.x) * w13);
    float w10 = saturate(rim - 3.162277660);
    if (row0 && column2)
        alpha = max(alpha, lerp(k0_2, k0_3, f.x) * w10);
    float w9 = saturate(rim - 3.000000000);
    if (row0 && column3)
        alpha = max(alpha, lerp(k0_3, k0_4, f.x) * w9);
    if (row0 && column4)
        alpha = max(alpha, lerp(k0_4, k0_5, f.x) * w10);
    if (row0 && column5)
        alpha = max(alpha, lerp(k0_5, k0_6, f.x) * w13);
    float k1_0 = lerp(g0_0.x, g2_0.w, f.y);
    float k1_1 = lerp(g0_0.y, g2_0.z, f.y);
    float k1_2 = lerp(g0_2.x, g2_2.w, f.y);
    float k1_3 = lerp(g0_2.y, g2_2.z, f.y);
    float k1_4 = lerp(g0_4.x, g2_4.w, f.y);
    float k1_5 = lerp(g0_4.y, g2_4.z, f.y);
    float k1_6 = lerp(g0_6.x, g2_6.w, f.y);
    float k1_7 = lerp(g0_6.y, g2_6.z, f.y);
    if (row1 && column0)
        alpha = max(alpha, lerp(k1_0, k1_1, f.x) * w13);
    float w8 = saturate(rim - 2.828427125);
    if (row1 && column1)
        alpha = max(alpha, lerp(k1_1, k1_2, f.x) * w8);
    float w5 = saturate(rim - 2.236067977);
    if (row1 && column2)
        alpha = max(alpha, lerp(k1_2, k1_3, f.x) * w5);
    float w4 = saturate(rim - 2.000000000);
    if (row1 && column3)
        alpha = max(alpha, lerp(k1_3, k1_4, f.x) * w4);
    if (row1 && column4)
        alpha = max(alpha, lerp(k1_4, k1_5, f.x) * w5);
    if (row1 && column5)
        alpha = max(alpha, lerp(k1_5, k1_6, f.x) * w8);
    if (row1 && column6)
        alpha = max(alpha, lerp(k1_6, k1_7, f.x) * w13);
    float k2_0 = lerp(g2_0.w, g2_0.x, f.y);
    float k2_1 = lerp(g2_0.z, g2_0.y, f.y);
    float k2_2 = lerp(g2_2.w, g2_2.x, f.y);
    float k2_3 = lerp(g2_2.z, g2_2.y, f.y);
    float k2_4 = lerp(g2_4.w, g2_4.x, f.y);
    float k2_5 = lerp(g2_4.z, g2_4.y, f.y);
    float k2_6 = lerp(g2_6.w, g2_6.x, f.y);
    float k2_7 = lerp(g2_6.z, g2_6.y, f.y);
    if (row2 && column0)
        alpha = max(alpha, lerp(k2_0, k2_1, f.x) * w10);
    if (row2 && column1)
        alpha = max(alpha, lerp(k2_1, k2_2, f.x) * w5);
    if (row2 && column2)
        alpha = max(alpha, lerp(k2_2, k2_3, f.x));
    if (row2 && column3)
        alpha = max(alpha, lerp(k2_3, k2_4, f.x));
    if (row2 && column4)
        alpha = max(alpha, lerp(k2_4, k2_5, f.x));
    if (row2 && column5)
        alpha = max(alpha, lerp(k2_5, k2_6, f.x) * w5);
    if (row2 && column6)
        alpha = max(alpha, lerp(k2_6, k2_7, f.x) * w10);
    float k3_0 = lerp(g2_0.x, g4_0.w, f.y);
    float k3_1 = lerp(g2_0.y, g4_0.z, f.y);
    float k3_2 = lerp(g2_2.x, g4_2.w, f.y);
    float k3_3 = lerp(g2_2.y, g4_2.z, f.y);
    float k3_4 = lerp(g2_4.x, g4_4.w, f.y);
    float k3_5 = lerp(g2_4.y, g4_4.z, f.y);
    float k3_6 = lerp(g2_6.x, g4_6.w, f.y);
    float k3_7 = lerp(g2_6.y, g4_6.z, f.y);
    if (row3 && column0)
        alpha = max(alpha, lerp(k3_0, k3_1, f.x) * w9);
    if (row3 && column1)
        alpha = max(alpha, lerp(k3_1, k3_2, f.x) * w4);
    if (row3 && column2)
        alpha = max(alpha, lerp(k3_2, k3_3, f.x));
    if (row3 && column3)
        alpha = max(alpha, lerp(k3_3, k3_4, f.x));
    if (row3 && column4)
        alpha = max(alpha, lerp(k3_4, k3_5, f.x));
    if (row3 && column5)
        alpha = max(alpha, lerp(k3_5, k3_6, f.x) * w4);
    if (row3 && column6)
        alpha = max(alpha, lerp(k3_6, k3_7, f.x) * w9);
    float k4_0 = lerp(g4_0.w, g4_0.x, f.y);
    float k4_1 = lerp(g4_0.z, g4_0.y, f.y);
    float k4_2 = lerp(g4_2.w, g4_2.x, f.y);
    float k4_3 = lerp(g4_2.z, g4_2.y, f.y);
    float k4_4 = lerp(g4_4.w, g4_4.x, f.y);
    float k4_5 = lerp(g4_4.z, g4_4.y, f.y);
    float k4_6 = lerp(g4_6.w, g4_6.x, f.y);
    float k4_7 = lerp(g4_6.z, g4_6.y, f.y);
    if (row4 && column0)
        alpha = max(alpha, lerp(k4_0, k4_1, f.x) * w10);
    if (row4 && column1)
        alpha = max(alpha, lerp(k4_1, k4_2, f.x) * w5);
    if (row4 && column2)
        alpha = max(alpha, lerp(k4_2, k4_3, f.x));
    if (row4 && column3)
        alpha = max(alpha, lerp(k4_3, k4_4, f.x));
    if (row4 && column4)
        alpha = max(alpha, lerp(k4_4, k4_5, f.x));
    if (row4 && column5)
        alpha = max(alpha, lerp(k4_5, k4_6, f.x) * w5);
    if (row4 && column6)
        alpha = max(alpha, lerp(k4_6, k4_7, f.x) * w10);
    float k5_0 = lerp(g4_0.x, g6_0.w, f.y);
    float k5_1 = lerp(g4_0.y, g6_0.z, f.y);
    float k5_2 = lerp(g4_2.x, g6_2.w, f.y);
    float k5_3 = lerp(g4_2.y, g6_2.z, f.y);
    float k5_4 = lerp(g4_4.x, g6_4.w, f.y);
    float k5_5 = lerp(g4_4.y, g6_4.z, f.y);
    float k5_6 = lerp(g4_6.x, g6_6.w, f.y);
    float k5_7 = lerp(g4_6.y, g6_6.z, f.y);
    if (row5 && column0)
        alpha = max(alpha, lerp(k5_0, k5_1, f.x) * w13);
    if (row5 && column1)
        alpha = max(alpha, lerp(k5_1, k5_2, f.x) * w8);
    if (row5 && column2)
        alpha = max(alpha, lerp(k5_2, k5_3, f.x) * w5);
    if (row5 && column3)
        alpha = max(alpha, lerp(k5_3, k5_4, f.x) * w4);
    if (row5 && column4)
        alpha = max(alpha, lerp(k5_4, k5_5, f.x) * w5);
    if (row5 && column5)
        alpha = max(alpha, lerp(k5_5, k5_6, f.x) * w8);
    if (row5 && column6)
        alpha = max(alpha, lerp(k5_6, k5_7, f.x) * w13);
    float k6_0 = lerp(g6_0.w, g6_0.x, f.y);
    float k6_1 = lerp(g6_0.z, g6_0.y, f.y);
    float k6_2 = lerp(g6_2.w, g6_2.x, f.y);
    float k6_3 = lerp(g6_2.z, g6_2.y, f.y);
    float k6_4 = lerp(g6_4.w, g6_4.x, f.y);
    float k6_5 = lerp(g6_4.z, g6_4.y, f.y);
    float k6_6 = lerp(g6_6.w, g6_6.x, f.y);
    float k6_7 = lerp(g6_6.z, g6_6.y, f.y);
    if (row6 && column1)
        alpha = max(alpha, lerp(k6_1, k6_2, f.x) * w13);
    if (row6 && column2)
        alpha = max(alpha, lerp(k6_2, k6_3, f.x) * w10);
    if (row6 && column3)
        alpha = max(alpha, lerp(k6_3, k6_4, f.x) * w9);
    if (row6 && column4)
        alpha = max(alpha, lerp(k6_4, k6_5, f.x) * w10);
    if (row6 && column5)
        alpha = max(alpha, lerp(k6_5, k6_6, f.x) * w13);
    return alpha;
}

float DilateGathered4(PSInput input, int c, float2 texel, float2 size, float rim)
{
    float2 corner, f;
    Footprint(input, texel, size, corner, f);
    float u0 = input.Uv.x - 4 * texel.x, v0 = input.Uv.y - 4 * texel.y;
    bool column0 = u0 >= input.RectMin.x && u0 <= input.RectMax.x;
    bool row0 = v0 >= input.RectMin.y && v0 <= input.RectMax.y;
    float u1 = input.Uv.x - 3 * texel.x, v1 = input.Uv.y - 3 * texel.y;
    bool column1 = u1 >= input.RectMin.x && u1 <= input.RectMax.x;
    bool row1 = v1 >= input.RectMin.y && v1 <= input.RectMax.y;
    float u2 = input.Uv.x - 2 * texel.x, v2 = input.Uv.y - 2 * texel.y;
    bool column2 = u2 >= input.RectMin.x && u2 <= input.RectMax.x;
    bool row2 = v2 >= input.RectMin.y && v2 <= input.RectMax.y;
    float u3 = input.Uv.x - texel.x, v3 = input.Uv.y - texel.y;
    bool column3 = u3 >= input.RectMin.x && u3 <= input.RectMax.x;
    bool row3 = v3 >= input.RectMin.y && v3 <= input.RectMax.y;
    float u4 = input.Uv.x, v4 = input.Uv.y;
    bool column4 = u4 >= input.RectMin.x && u4 <= input.RectMax.x;
    bool row4 = v4 >= input.RectMin.y && v4 <= input.RectMax.y;
    float u5 = input.Uv.x + texel.x, v5 = input.Uv.y + texel.y;
    bool column5 = u5 >= input.RectMin.x && u5 <= input.RectMax.x;
    bool row5 = v5 >= input.RectMin.y && v5 <= input.RectMax.y;
    float u6 = input.Uv.x + 2 * texel.x, v6 = input.Uv.y + 2 * texel.y;
    bool column6 = u6 >= input.RectMin.x && u6 <= input.RectMax.x;
    bool row6 = v6 >= input.RectMin.y && v6 <= input.RectMax.y;
    float u7 = input.Uv.x + 3 * texel.x, v7 = input.Uv.y + 3 * texel.y;
    bool column7 = u7 >= input.RectMin.x && u7 <= input.RectMax.x;
    bool row7 = v7 >= input.RectMin.y && v7 <= input.RectMax.y;
    float u8 = input.Uv.x + 4 * texel.x, v8 = input.Uv.y + 4 * texel.y;
    bool column8 = u8 >= input.RectMin.x && u8 <= input.RectMax.x;
    bool row8 = v8 >= input.RectMin.y && v8 <= input.RectMax.y;
    float4 g0_0 = Gather(c, corner, int2(-4, -4));
    float4 g0_2 = Gather(c, corner, int2(-2, -4));
    float4 g0_4 = Gather(c, corner, int2(0, -4));
    float4 g0_6 = Gather(c, corner, int2(2, -4));
    float4 g0_8 = Gather(c, corner, int2(4, -4));
    float4 g2_0 = Gather(c, corner, int2(-4, -2));
    float4 g2_2 = Gather(c, corner, int2(-2, -2));
    float4 g2_4 = Gather(c, corner, int2(0, -2));
    float4 g2_6 = Gather(c, corner, int2(2, -2));
    float4 g2_8 = Gather(c, corner, int2(4, -2));
    float4 g4_0 = Gather(c, corner, int2(-4, 0));
    float4 g4_2 = Gather(c, corner, int2(-2, 0));
    float4 g4_4 = Gather(c, corner, int2(0, 0));
    float4 g4_6 = Gather(c, corner, int2(2, 0));
    float4 g4_8 = Gather(c, corner, int2(4, 0));
    float4 g6_0 = Gather(c, corner, int2(-4, 2));
    float4 g6_2 = Gather(c, corner, int2(-2, 2));
    float4 g6_4 = Gather(c, corner, int2(0, 2));
    float4 g6_6 = Gather(c, corner, int2(2, 2));
    float4 g6_8 = Gather(c, corner, int2(4, 2));
    float4 g8_0 = Gather(c, corner, int2(-4, 4));
    float4 g8_2 = Gather(c, corner, int2(-2, 4));
    float4 g8_4 = Gather(c, corner, int2(0, 4));
    float4 g8_6 = Gather(c, corner, int2(2, 4));
    float4 g8_8 = Gather(c, corner, int2(4, 4));
    float alpha = 0;
    float k0_0 = lerp(g0_0.w, g0_0.x, f.y);
    float k0_1 = lerp(g0_0.z, g0_0.y, f.y);
    float k0_2 = lerp(g0_2.w, g0_2.x, f.y);
    float k0_3 = lerp(g0_2.z, g0_2.y, f.y);
    float k0_4 = lerp(g0_4.w, g0_4.x, f.y);
    float k0_5 = lerp(g0_4.z, g0_4.y, f.y);
    float k0_6 = lerp(g0_6.w, g0_6.x, f.y);
    float k0_7 = lerp(g0_6.z, g0_6.y, f.y);
    float k0_8 = lerp(g0_8.w, g0_8.x, f.y);
    float k0_9 = lerp(g0_8.z, g0_8.y, f.y);
    float w20 = saturate(rim - 4.472135955);
    if (row0 && column2)
        alpha = max(alpha, lerp(k0_2, k0_3, f.x) * w20);
    float w17 = saturate(rim - 4.123105626);
    if (row0 && column3)
        alpha = max(alpha, lerp(k0_3, k0_4, f.x) * w17);
    float w16 = saturate(rim - 4.000000000);
    if (row0 && column4)
        alpha = max(alpha, lerp(k0_4, k0_5, f.x) * w16);
    if (row0 && column5)
        alpha = max(alpha, lerp(k0_5, k0_6, f.x) * w17);
    if (row0 && column6)
        alpha = max(alpha, lerp(k0_6, k0_7, f.x) * w20);
    float k1_0 = lerp(g0_0.x, g2_0.w, f.y);
    float k1_1 = lerp(g0_0.y, g2_0.z, f.y);
    float k1_2 = lerp(g0_2.x, g2_2.w, f.y);
    float k1_3 = lerp(g0_2.y, g2_2.z, f.y);
    float k1_4 = lerp(g0_4.x, g2_4.w, f.y);
    float k1_5 = lerp(g0_4.y, g2_4.z, f.y);
    float k1_6 = lerp(g0_6.x, g2_6.w, f.y);
    float k1_7 = lerp(g0_6.y, g2_6.z, f.y);
    float k1_8 = lerp(g0_8.x, g2_8.w, f.y);
    float k1_9 = lerp(g0_8.y, g2_8.z, f.y);
    float w18 = saturate(rim - 4.242640687);
    if (row1 && column1)
        alpha = max(alpha, lerp(k1_1, k1_2, f.x) * w18);
    float w13 = saturate(rim - 3.605551275);
    if (row1 && column2)
        alpha = max(alpha, lerp(k1_2, k1_3, f.x) * w13);
    float w10 = saturate(rim - 3.162277660);
    if (row1 && column3)
        alpha = max(alpha, lerp(k1_3, k1_4, f.x) * w10);
    float w9 = saturate(rim - 3.000000000);
    if (row1 && column4)
        alpha = max(alpha, lerp(k1_4, k1_5, f.x) * w9);
    if (row1 && column5)
        alpha = max(alpha, lerp(k1_5, k1_6, f.x) * w10);
    if (row1 && column6)
        alpha = max(alpha, lerp(k1_6, k1_7, f.x) * w13);
    if (row1 && column7)
        alpha = max(alpha, lerp(k1_7, k1_8, f.x) * w18);
    float k2_0 = lerp(g2_0.w, g2_0.x, f.y);
    float k2_1 = lerp(g2_0.z, g2_0.y, f.y);
    float k2_2 = lerp(g2_2.w, g2_2.x, f.y);
    float k2_3 = lerp(g2_2.z, g2_2.y, f.y);
    float k2_4 = lerp(g2_4.w, g2_4.x, f.y);
    float k2_5 = lerp(g2_4.z, g2_4.y, f.y);
    float k2_6 = lerp(g2_6.w, g2_6.x, f.y);
    float k2_7 = lerp(g2_6.z, g2_6.y, f.y);
    float k2_8 = lerp(g2_8.w, g2_8.x, f.y);
    float k2_9 = lerp(g2_8.z, g2_8.y, f.y);
    if (row2 && column0)
        alpha = max(alpha, lerp(k2_0, k2_1, f.x) * w20);
    if (row2 && column1)
        alpha = max(alpha, lerp(k2_1, k2_2, f.x) * w13);
    if (row2 && column2)
        alpha = max(alpha, lerp(k2_2, k2_3, f.x));
    if (row2 && column3)
        alpha = max(alpha, lerp(k2_3, k2_4, f.x));
    if (row2 && column4)
        alpha = max(alpha, lerp(k2_4, k2_5, f.x));
    if (row2 && column5)
        alpha = max(alpha, lerp(k2_5, k2_6, f.x));
    if (row2 && column6)
        alpha = max(alpha, lerp(k2_6, k2_7, f.x));
    if (row2 && column7)
        alpha = max(alpha, lerp(k2_7, k2_8, f.x) * w13);
    if (row2 && column8)
        alpha = max(alpha, lerp(k2_8, k2_9, f.x) * w20);
    float k3_0 = lerp(g2_0.x, g4_0.w, f.y);
    float k3_1 = lerp(g2_0.y, g4_0.z, f.y);
    float k3_2 = lerp(g2_2.x, g4_2.w, f.y);
    float k3_3 = lerp(g2_2.y, g4_2.z, f.y);
    float k3_4 = lerp(g2_4.x, g4_4.w, f.y);
    float k3_5 = lerp(g2_4.y, g4_4.z, f.y);
    float k3_6 = lerp(g2_6.x, g4_6.w, f.y);
    float k3_7 = lerp(g2_6.y, g4_6.z, f.y);
    float k3_8 = lerp(g2_8.x, g4_8.w, f.y);
    float k3_9 = lerp(g2_8.y, g4_8.z, f.y);
    if (row3 && column0)
        alpha = max(alpha, lerp(k3_0, k3_1, f.x) * w17);
    if (row3 && column1)
        alpha = max(alpha, lerp(k3_1, k3_2, f.x) * w10);
    if (row3 && column2)
        alpha = max(alpha, lerp(k3_2, k3_3, f.x));
    if (row3 && column3)
        alpha = max(alpha, lerp(k3_3, k3_4, f.x));
    if (row3 && column4)
        alpha = max(alpha, lerp(k3_4, k3_5, f.x));
    if (row3 && column5)
        alpha = max(alpha, lerp(k3_5, k3_6, f.x));
    if (row3 && column6)
        alpha = max(alpha, lerp(k3_6, k3_7, f.x));
    if (row3 && column7)
        alpha = max(alpha, lerp(k3_7, k3_8, f.x) * w10);
    if (row3 && column8)
        alpha = max(alpha, lerp(k3_8, k3_9, f.x) * w17);
    float k4_0 = lerp(g4_0.w, g4_0.x, f.y);
    float k4_1 = lerp(g4_0.z, g4_0.y, f.y);
    float k4_2 = lerp(g4_2.w, g4_2.x, f.y);
    float k4_3 = lerp(g4_2.z, g4_2.y, f.y);
    float k4_4 = lerp(g4_4.w, g4_4.x, f.y);
    float k4_5 = lerp(g4_4.z, g4_4.y, f.y);
    float k4_6 = lerp(g4_6.w, g4_6.x, f.y);
    float k4_7 = lerp(g4_6.z, g4_6.y, f.y);
    float k4_8 = lerp(g4_8.w, g4_8.x, f.y);
    float k4_9 = lerp(g4_8.z, g4_8.y, f.y);
    if (row4 && column0)
        alpha = max(alpha, lerp(k4_0, k4_1, f.x) * w16);
    if (row4 && column1)
        alpha = max(alpha, lerp(k4_1, k4_2, f.x) * w9);
    if (row4 && column2)
        alpha = max(alpha, lerp(k4_2, k4_3, f.x));
    if (row4 && column3)
        alpha = max(alpha, lerp(k4_3, k4_4, f.x));
    if (row4 && column4)
        alpha = max(alpha, lerp(k4_4, k4_5, f.x));
    if (row4 && column5)
        alpha = max(alpha, lerp(k4_5, k4_6, f.x));
    if (row4 && column6)
        alpha = max(alpha, lerp(k4_6, k4_7, f.x));
    if (row4 && column7)
        alpha = max(alpha, lerp(k4_7, k4_8, f.x) * w9);
    if (row4 && column8)
        alpha = max(alpha, lerp(k4_8, k4_9, f.x) * w16);
    float k5_0 = lerp(g4_0.x, g6_0.w, f.y);
    float k5_1 = lerp(g4_0.y, g6_0.z, f.y);
    float k5_2 = lerp(g4_2.x, g6_2.w, f.y);
    float k5_3 = lerp(g4_2.y, g6_2.z, f.y);
    float k5_4 = lerp(g4_4.x, g6_4.w, f.y);
    float k5_5 = lerp(g4_4.y, g6_4.z, f.y);
    float k5_6 = lerp(g4_6.x, g6_6.w, f.y);
    float k5_7 = lerp(g4_6.y, g6_6.z, f.y);
    float k5_8 = lerp(g4_8.x, g6_8.w, f.y);
    float k5_9 = lerp(g4_8.y, g6_8.z, f.y);
    if (row5 && column0)
        alpha = max(alpha, lerp(k5_0, k5_1, f.x) * w17);
    if (row5 && column1)
        alpha = max(alpha, lerp(k5_1, k5_2, f.x) * w10);
    if (row5 && column2)
        alpha = max(alpha, lerp(k5_2, k5_3, f.x));
    if (row5 && column3)
        alpha = max(alpha, lerp(k5_3, k5_4, f.x));
    if (row5 && column4)
        alpha = max(alpha, lerp(k5_4, k5_5, f.x));
    if (row5 && column5)
        alpha = max(alpha, lerp(k5_5, k5_6, f.x));
    if (row5 && column6)
        alpha = max(alpha, lerp(k5_6, k5_7, f.x));
    if (row5 && column7)
        alpha = max(alpha, lerp(k5_7, k5_8, f.x) * w10);
    if (row5 && column8)
        alpha = max(alpha, lerp(k5_8, k5_9, f.x) * w17);
    float k6_0 = lerp(g6_0.w, g6_0.x, f.y);
    float k6_1 = lerp(g6_0.z, g6_0.y, f.y);
    float k6_2 = lerp(g6_2.w, g6_2.x, f.y);
    float k6_3 = lerp(g6_2.z, g6_2.y, f.y);
    float k6_4 = lerp(g6_4.w, g6_4.x, f.y);
    float k6_5 = lerp(g6_4.z, g6_4.y, f.y);
    float k6_6 = lerp(g6_6.w, g6_6.x, f.y);
    float k6_7 = lerp(g6_6.z, g6_6.y, f.y);
    float k6_8 = lerp(g6_8.w, g6_8.x, f.y);
    float k6_9 = lerp(g6_8.z, g6_8.y, f.y);
    if (row6 && column0)
        alpha = max(alpha, lerp(k6_0, k6_1, f.x) * w20);
    if (row6 && column1)
        alpha = max(alpha, lerp(k6_1, k6_2, f.x) * w13);
    if (row6 && column2)
        alpha = max(alpha, lerp(k6_2, k6_3, f.x));
    if (row6 && column3)
        alpha = max(alpha, lerp(k6_3, k6_4, f.x));
    if (row6 && column4)
        alpha = max(alpha, lerp(k6_4, k6_5, f.x));
    if (row6 && column5)
        alpha = max(alpha, lerp(k6_5, k6_6, f.x));
    if (row6 && column6)
        alpha = max(alpha, lerp(k6_6, k6_7, f.x));
    if (row6 && column7)
        alpha = max(alpha, lerp(k6_7, k6_8, f.x) * w13);
    if (row6 && column8)
        alpha = max(alpha, lerp(k6_8, k6_9, f.x) * w20);
    float k7_0 = lerp(g6_0.x, g8_0.w, f.y);
    float k7_1 = lerp(g6_0.y, g8_0.z, f.y);
    float k7_2 = lerp(g6_2.x, g8_2.w, f.y);
    float k7_3 = lerp(g6_2.y, g8_2.z, f.y);
    float k7_4 = lerp(g6_4.x, g8_4.w, f.y);
    float k7_5 = lerp(g6_4.y, g8_4.z, f.y);
    float k7_6 = lerp(g6_6.x, g8_6.w, f.y);
    float k7_7 = lerp(g6_6.y, g8_6.z, f.y);
    float k7_8 = lerp(g6_8.x, g8_8.w, f.y);
    float k7_9 = lerp(g6_8.y, g8_8.z, f.y);
    if (row7 && column1)
        alpha = max(alpha, lerp(k7_1, k7_2, f.x) * w18);
    if (row7 && column2)
        alpha = max(alpha, lerp(k7_2, k7_3, f.x) * w13);
    if (row7 && column3)
        alpha = max(alpha, lerp(k7_3, k7_4, f.x) * w10);
    if (row7 && column4)
        alpha = max(alpha, lerp(k7_4, k7_5, f.x) * w9);
    if (row7 && column5)
        alpha = max(alpha, lerp(k7_5, k7_6, f.x) * w10);
    if (row7 && column6)
        alpha = max(alpha, lerp(k7_6, k7_7, f.x) * w13);
    if (row7 && column7)
        alpha = max(alpha, lerp(k7_7, k7_8, f.x) * w18);
    float k8_0 = lerp(g8_0.w, g8_0.x, f.y);
    float k8_1 = lerp(g8_0.z, g8_0.y, f.y);
    float k8_2 = lerp(g8_2.w, g8_2.x, f.y);
    float k8_3 = lerp(g8_2.z, g8_2.y, f.y);
    float k8_4 = lerp(g8_4.w, g8_4.x, f.y);
    float k8_5 = lerp(g8_4.z, g8_4.y, f.y);
    float k8_6 = lerp(g8_6.w, g8_6.x, f.y);
    float k8_7 = lerp(g8_6.z, g8_6.y, f.y);
    float k8_8 = lerp(g8_8.w, g8_8.x, f.y);
    float k8_9 = lerp(g8_8.z, g8_8.y, f.y);
    if (row8 && column2)
        alpha = max(alpha, lerp(k8_2, k8_3, f.x) * w20);
    if (row8 && column3)
        alpha = max(alpha, lerp(k8_3, k8_4, f.x) * w17);
    if (row8 && column4)
        alpha = max(alpha, lerp(k8_4, k8_5, f.x) * w16);
    if (row8 && column5)
        alpha = max(alpha, lerp(k8_5, k8_6, f.x) * w17);
    if (row8 && column6)
        alpha = max(alpha, lerp(k8_6, k8_7, f.x) * w20);
    return alpha;
}

// Channel c (0 to 3), reach r (1 to 4).
float DilateGatheredSquare(PSInput input, int c, int r, float2 texel, float2 size, float rim)
{
    [branch]
    switch (c * 8 + r)
    {
    case 1:
        return DilateGathered1(input, 0, texel, size, rim);
    case 2:
        return DilateGathered2(input, 0, texel, size, rim);
    case 3:
        return DilateGathered3(input, 0, texel, size, rim);
    case 4:
        return DilateGathered4(input, 0, texel, size, rim);
    case 9:
        return DilateGathered1(input, 1, texel, size, rim);
    case 10:
        return DilateGathered2(input, 1, texel, size, rim);
    case 11:
        return DilateGathered3(input, 1, texel, size, rim);
    case 12:
        return DilateGathered4(input, 1, texel, size, rim);
    case 17:
        return DilateGathered1(input, 2, texel, size, rim);
    case 18:
        return DilateGathered2(input, 2, texel, size, rim);
    case 19:
        return DilateGathered3(input, 2, texel, size, rim);
    case 20:
        return DilateGathered4(input, 2, texel, size, rim);
    case 25:
        return DilateGathered1(input, 3, texel, size, rim);
    case 26:
        return DilateGathered2(input, 3, texel, size, rim);
    case 27:
        return DilateGathered3(input, 3, texel, size, rim);
    case 28:
        return DilateGathered4(input, 3, texel, size, rim);
    default:
        return 0;
    }
}
