using System;
using System.Collections.Generic;

using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Records;

/// <summary>
/// Coverage placed relative to the pen on the baseline; <paramref name="Lcd"/> is null or R, G, B coverage per pixel of Alpha
/// (their mean), and resampling drops it.
/// </summary>
internal readonly record struct RasterGlyph(int Advance, int Left, int Top, int Width, int Height, byte[] Alpha, byte[]? Lcd = null)
{
    public static (int Left, int Top, int Width, int Height) ScaledBounds(int left, int top, int width, int height, float scale, float shiftX, float shiftY)
    {
        var x0 = (int)MathF.Floor((left * scale) + shiftX);
        var y0 = (int)MathF.Floor((top * scale) + shiftY);
        return (x0, y0,
                   (int)MathF.Ceiling((left + width) * scale + shiftX) - x0,
                   (int)MathF.Ceiling((top + height) * scale + shiftY) - y0);
    }

    public RasterGlyph Scaled(float scale, float shiftX, float shiftY)
    {
        var (x0, y0, w, h) = ScaledBounds(this.Left, this.Top, this.Width, this.Height, scale, shiftX, shiftY);
        var left = (this.Left * scale) + shiftX;
        var top = (this.Top * scale) + shiftY;
        var alpha = new byte[w * h];
        if (scale < 1)
        {
            var columns = AreaWeights(x0, w, left, scale, this.Width);
            var rows = AreaWeights(y0, h, top, scale, this.Height);
            var horizontal = new float[w * this.Height];
            for (var y = 0; y < this.Height; y++)
            {
                var src = this.Alpha.AsSpan(y * this.Width, this.Width);
                for (var x = 0; x < w; x++)
                {
                    var sum = 0f;
                    foreach (var (i, weight) in columns[x])
                        sum += weight * src[i];
                    horizontal[(y * w) + x] = sum;
                }
            }

            var norm = scale * scale;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var sum = 0f;
                    foreach (var (j, weight) in rows[y])
                        sum += weight * horizontal[(j * w) + x];
                    alpha[(y * w) + x] = (byte)Math.Clamp(Misc.StdRound(sum * norm), 0, 255);
                }
            }

            return this with { Left = x0, Top = y0, Width = w, Height = h, Alpha = alpha, Lcd = null };
        }

        const int Samples = 4;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = 0f;
                for (var sy = 0; sy < Samples; sy++)
                {
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        // The sample's position in source pixels, from their centers.
                        var u = ((x0 + x + ((sx + 0.5f) / Samples) - left) / scale) - 0.5f;
                        var v = ((y0 + y + ((sy + 0.5f) / Samples) - top) / scale) - 0.5f;
                        sum += this.Bilinear(u, v);
                    }
                }

                alpha[(y * w) + x] = (byte)Misc.StdRound(sum / (Samples * Samples));
            }
        }

        return this with { Left = x0, Top = y0, Width = w, Height = h, Alpha = alpha, Lcd = null };
    }

    /// <summary>Each output pixel's weights sum to 1 / scale.</summary>
    private static List<(int Index, float Weight)>[] AreaWeights(int first, int count, float origin, float scale, int sourceSize)
    {
        var res = new List<(int, float)>[count];
        for (var d = 0; d < count; d++)
        {
            res[d] = [];
            var u0 = (first + d - origin) / scale;
            var u1 = (first + d + 1 - origin) / scale;
            var i0 = Math.Max(0, (int)MathF.Floor(u0));
            var i1 = Math.Min(sourceSize, (int)MathF.Ceiling(u1));
            for (var i = i0; i < i1; i++)
            {
                var weight = MathF.Min(u1, i + 1) - MathF.Max(u0, i);
                if (weight > 0)
                    res[d].Add((i, weight));
            }
        }

        return res;
    }

    private float Bilinear(float u, float v)
    {
        var x = (int)MathF.Floor(u);
        var y = (int)MathF.Floor(v);
        float fx = u - x, fy = v - y;
        return Lerp(Lerp(this.At(x, y), this.At(x + 1, y), fx), Lerp(this.At(x, y + 1), this.At(x + 1, y + 1), fx), fy);

        static float Lerp(float a, float b, float t) => a + ((b - a) * t);
    }

    private float At(int x, int y) => x < 0 || y < 0 || x >= this.Width || y >= this.Height ? 0 : this.Alpha[(y * this.Width) + x];

    public RasterGlyph SqueezedX(float scale)
    {
        var x0 = (int)MathF.Floor(this.Left * scale);
        var w = Math.Max(1, (int)MathF.Ceiling((this.Left + this.Width) * scale) - x0);
        var columns = AreaWeights(x0, w, this.Left * scale, scale, this.Width);
        var alpha = new byte[w * this.Height];
        for (var y = 0; y < this.Height; y++)
        {
            var src = this.Alpha.AsSpan(y * this.Width, this.Width);
            for (var x = 0; x < w; x++)
            {
                var sum = 0f;
                foreach (var (i, weight) in columns[x])
                    sum += weight * src[i];
                alpha[(y * w) + x] = (byte)Math.Clamp(Misc.StdRound(sum * scale), 0, 255);
            }
        }

        return this with { Left = x0, Width = w, Alpha = alpha, Lcd = null };
    }

    public RasterGlyph Trimmed()
    {
        int x1 = this.Width, y1 = this.Height, x2 = 0, y2 = 0;
        for (var y = 0; y < this.Height; y++)
        {
            for (var x = 0; x < this.Width; x++)
            {
                if (this.Alpha[(y * this.Width) + x] != 0)
                    (x1, y1, x2, y2) = (Math.Min(x1, x), Math.Min(y1, y), Math.Max(x2, x + 1), Math.Max(y2, y + 1));
            }
        }

        if (x1 >= x2 || y1 >= y2)
            return this with { Left = 0, Top = 0, Width = 0, Height = 0, Alpha = [], Lcd = null };
        return this.Reboxed(this.Left + x1, this.Top + y1, x2 - x1, y2 - y1);
    }

    public RasterGlyph Reboxed(int left, int top, int width, int height)
    {
        var alpha = new byte[width * height];
        var lcd = this.Lcd is null ? null : new byte[width * height * 3];
        int x1 = Math.Max(left, this.Left), x2 = Math.Min(left + width, this.Left + this.Width);
        for (var y = Math.Max(top, this.Top); y < Math.Min(top + height, this.Top + this.Height) && x1 < x2; y++)
        {
            var from = ((y - this.Top) * this.Width) + x1 - this.Left;
            var to = ((y - top) * width) + x1 - left;
            this.Alpha.AsSpan(from, x2 - x1).CopyTo(alpha.AsSpan(to));
            if (lcd is not null)
                this.Lcd!.AsSpan(from * 3, (x2 - x1) * 3).CopyTo(lcd.AsSpan(to * 3));
        }

        return this with { Left = left, Top = top, Width = width, Height = height, Alpha = alpha, Lcd = lcd };
    }

    public static RasterGlyph Merge(List<RasterGlyph> pieces, int advance)
    {
        pieces.RemoveAll(p => p.Width == 0);
        if (pieces.Count == 0)
            return new(advance, 0, 0, 0, 0, []);
        if (pieces.Count == 1)
            return pieces[0] with { Advance = advance };

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var p in pieces)
            (left, top, right, bottom) = (Math.Min(left, p.Left), Math.Min(top, p.Top), Math.Max(right, p.Left + p.Width), Math.Max(bottom, p.Top + p.Height));
        var w = right - left;
        var h = bottom - top;
        var alpha = new byte[w * h];
        foreach (var p in pieces)
            BlitMax(alpha, w, h, p, p.Left - left, p.Top - top);
        byte[]? lcd = null;
        if (pieces.Exists(p => p.Lcd is not null))
        {
            lcd = new byte[w * h * 3];
            foreach (var p in pieces)
                BlitMaxLcd(lcd, w, h, p, p.Left - left, p.Top - top);
        }

        return new(advance, left, top, w, h, alpha, lcd);
    }

    public static void BlitMax(byte[] buffer, int width, int height, RasterGlyph g, int x, int y)
    {
        for (var row = Math.Max(0, -y); row < g.Height && row + y < height; row++)
        {
            for (var col = Math.Max(0, -x); col < g.Width && col + x < width; col++)
            {
                ref var d = ref buffer[((row + y) * width) + col + x];
                d = Math.Max(d, g.Alpha[(row * g.Width) + col]);
            }
        }
    }

    private static void BlitMaxLcd(byte[] buffer, int width, int height, RasterGlyph g, int x, int y)
    {
        for (var row = Math.Max(0, -y); row < g.Height && row + y < height; row++)
        {
            for (var col = Math.Max(0, -x); col < g.Width && col + x < width; col++)
            {
                var from = (row * g.Width) + col;
                var to = (((row + y) * width) + col + x) * 3;
                for (var c = 0; c < 3; c++)
                    buffer[to + c] = Math.Max(buffer[to + c], g.Lcd is { } lcd ? lcd[(from * 3) + c] : g.Alpha[from]);
            }
        }
    }

    public static byte[]? GammaTable(float gamma) => gamma != 1 && gamma > 0 ? CoverageTable(1 / gamma) : null;

    public static byte[] CoverageTable(float exponent)
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
            table[i] = (byte)Misc.StdRound(255 * MathF.Pow(i / 255f, exponent));
        return table;
    }

    /// <summary>Into new arrays, as coverage may be shared.</summary>
    public RasterGlyph WithCoverage(byte[] table)
    {
        var alpha = new byte[this.Alpha.Length];
        for (var i = 0; i < alpha.Length; i++)
            alpha[i] = table[this.Alpha[i]];
        byte[]? lcd = null;
        if (this.Lcd is { } source)
        {
            lcd = new byte[source.Length];
            for (var i = 0; i < lcd.Length; i++)
                lcd[i] = table[source[i]];
        }

        return this with { Alpha = alpha, Lcd = lcd };
    }
}
