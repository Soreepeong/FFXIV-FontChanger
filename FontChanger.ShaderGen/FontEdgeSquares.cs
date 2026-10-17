using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FontChanger.ShaderGen;

/// <summary>Written out statement by statement, which FXC compiles several times faster than unrolled loops over arrays.</summary>
internal static class FontEdgeSquares
{
    public const int SquareReach = 4;

    private const int ChannelCount = 4;

    public static string Generate()
    {
        var sb = new StringBuilder();
        sb.Append($"#define SQUARE_REACH {SquareReach}\n\n");
        sb.Append($"// Reaches 1 to {SquareReach} for a one-hot Channel c (a literal), one function per reach: the (2R + 1)^2 samples\n");
        sb.Append("// around Uv from the (R + 1)^2 gathers at the texel corners around them, every texel gathered once.\n");
        for (var r = 1; r <= SquareReach; r++)
            sb.Append(Square(r)).Append('\n');

        sb.Append($"// Channel c (0 to 3), reach r (1 to {SquareReach}).\n");
        sb.Append("float DilateGatheredSquare(PSInput input, int c, int r, float2 texel, float2 size, float3 rim)\n{\n");
        sb.Append("    [branch]\n    switch (c * 8 + r)\n    {\n");
        for (var c = 0; c < ChannelCount; c++)
        {
            for (var r = 1; r <= SquareReach; r++)
                sb.Append($"    case {(c * 8) + r}:\n        return DilateGathered{r}(input, {c}, texel, size, rim);\n");
        }

        sb.Append("    default:\n        return 0;\n    }\n}");
        return sb.ToString();
    }

    /// <summary>
    /// Relative to the corner of Uv's footprint, gather (a, b) holds texel columns a, a + 1 of rows b, b + 1, and sample
    /// (x, y) blends texel columns x, x + 1 of rows y, y + 1.
    /// </summary>
    private static string Square(int r)
    {
        var sb = new StringBuilder();
        void Emit(string line) => sb.Append(line).Append('\n');

        Emit($"float DilateGathered{r}(PSInput input, int c, float2 texel, float2 size, float3 rim)");
        Emit("{");
        Emit("    float2 corner, f;");
        Emit("    Footprint(input, texel, size, corner, f);");

        for (var i = -r; i <= r; i++)
        {
            var n = i + r;
            Emit($"    float u{n} = {Offset("input.Uv.x", i, "texel.x")}, v{n} = {Offset("input.Uv.y", i, "texel.y")};");
            Emit($"    bool column{n} = u{n} >= input.RectMin.x && u{n} <= input.RectMax.x;");
            Emit($"    bool row{n} = v{n} >= input.RectMin.y && v{n} <= input.RectMax.y;");
        }

        for (var b = -r; b <= r; b += 2)
        {
            for (var a = -r; a <= r; a += 2)
                Emit($"    float4 g{b + r}_{a + r} = Gather(c, corner, int2({a}, {b}));");
        }

        string Texel(int row, int col)
        {
            var b = row - ((row + r) % 2);
            var a = col - ((col + r) % 2);
            var component = row == b ? (col == a ? 'w' : 'z') : (col == a ? 'x' : 'y');
            return $"g{b + r}_{a + r}.{component}";
        }

        // Offsets r + 1 or further weigh 0 (r is the furthest weighing more), and every other one is weighed, by exactly 1
        // where solid, which keeps the product exact.
        var weights = new HashSet<int>();
        Emit("    float alpha = 0;");
        for (var y = -r; y <= r; y++)
        {
            for (var col = -r; col <= r + 1; col++)
                Emit($"    float k{y + r}_{col + r} = lerp({Texel(y, col)}, {Texel(y + 1, col)}, f.y);");
            for (var x = -r; x <= r; x++)
            {
                var d2 = (x * x) + (y * y);
                if (d2 >= (r + 1) * (r + 1))
                    continue;
                if (weights.Add(d2))
                {
                    Emit(d2 == 0
                        ? "    float w0 = saturate(rim.x);"
                        : $"    float w{d2} = saturate(rim.x - {Math.Sqrt(d2).ToString("F9", CultureInfo.InvariantCulture)} * rim.y);");
                }

                Emit($"    if (row{y + r} && column{x + r})");
                Emit($"        alpha = max(alpha, lerp(k{y + r}_{x + r}, k{y + r}_{x + r + 1}, f.x) * w{d2});");
            }
        }

        Emit("    return alpha;");
        Emit("}");
        return sb.ToString();
    }

    /// <summary>As written by hand, without "+ 0 *" or "1 *".</summary>
    private static string Offset(string @base, int i, string step)
    {
        if (i == 0)
            return @base;
        var times = Math.Abs(i) == 1 ? step : $"{Math.Abs(i)} * {step}";
        return $"{@base} {(i > 0 ? '+' : '-')} {times}";
    }
}