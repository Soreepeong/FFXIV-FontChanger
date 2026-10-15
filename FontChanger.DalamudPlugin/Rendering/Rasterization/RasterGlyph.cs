using System;
using System.Collections.Generic;

namespace FontChanger.DalamudPlugin.Rendering.Rasterization;

/// <summary>
/// A rasterized glyph: 8-bit coverage, placed relative to the pen on the baseline. <paramref name="Lcd"/> is null, or R, G,
/// B subpixel coverage per pixel of Alpha (which is their mean); resampling drops it.
/// </summary>
internal readonly record struct RasterGlyph(int Advance, int Left, int Top, int Width, int Height, byte[] Alpha, byte[]? Lcd = null)
{
    /// <summary>
    /// Gets the box a glyph's coverage takes when scaled by <paramref name="scale"/> about the pen, moved right and down by
    /// a fraction of a pixel.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) ScaledBounds(int left, int top, int width, int height, float scale, float shiftX, float shiftY)
    {
        var x0 = (int)MathF.Floor((left * scale) + shiftX);
        var y0 = (int)MathF.Floor((top * scale) + shiftY);
        return (x0, y0,
                   (int)MathF.Ceiling((left + width) * scale + shiftX) - x0,
                   (int)MathF.Ceiling((top + height) * scale + shiftY) - y0);
    }

    /// <summary>
    /// Gets the glyph scaled by <paramref name="scale"/> about the pen, moved right and down by a fraction of a pixel: shrunk,
    /// each output pixel averages the source area it covers; enlarged, 4 x 4 bilinear samples.
    /// </summary>
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
                    alpha[(y * w) + x] = (byte)Math.Clamp(Rounding.Round(sum * norm), 0, 255);
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

                alpha[(y * w) + x] = (byte)Rounding.Round(sum / (Samples * Samples));
            }
        }

        return this with { Left = x0, Top = y0, Width = w, Height = h, Alpha = alpha, Lcd = null };
    }

    /// <summary>Gets, per output pixel from <paramref name="first"/>, the source pixels it covers and by how much (summing to 1 / scale).</summary>
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

    /// <summary>
    /// Gets the glyph scaled horizontally by <paramref name="scale"/> about the pen, each pixel averaging the source
    /// columns it covers.
    /// </summary>
    public RasterGlyph SqueezedX(float scale)
    {
        var x0 = (int)MathF.Floor(this.Left * scale);
        var w = Math.Max(1, (int)MathF.Ceiling((this.Left + this.Width) * scale) - x0);
        var alpha = new byte[w * this.Height];
        for (var y = 0; y < this.Height; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var u0 = ((x0 + x) / scale) - this.Left;
                var u1 = ((x0 + x + 1) / scale) - this.Left;
                var sum = 0f;
                for (var u = (int)MathF.Floor(u0); u < MathF.Ceiling(u1); u++)
                {
                    if (u < 0 || u >= this.Width)
                        continue;
                    var cover = Math.Min(u + 1, u1) - Math.Max(u, u0);
                    sum += this.Alpha[(y * this.Width) + u] * cover;
                }

                alpha[(y * w) + x] = (byte)Math.Clamp(Rounding.Round(sum * scale), 0, 255);
            }
        }

        return this with { Left = x0, Width = w, Alpha = alpha, Lcd = null };
    }

    /// <summary>Gets the glyph with its empty rows and columns cut off.</summary>
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
        var w = x2 - x1;
        var alpha = new byte[w * (y2 - y1)];
        for (var y = y1; y < y2; y++)
            this.Alpha.AsSpan((y * this.Width) + x1, w).CopyTo(alpha.AsSpan((y - y1) * w));
        byte[]? lcd = null;
        if (this.Lcd is { } source)
        {
            lcd = new byte[alpha.Length * 3];
            for (var y = y1; y < y2; y++)
                source.AsSpan(((y * this.Width) + x1) * 3, w * 3).CopyTo(lcd.AsSpan((y - y1) * w * 3));
        }

        return this with { Left = this.Left + x1, Top = this.Top + y1, Width = w, Height = y2 - y1, Alpha = alpha, Lcd = lcd };
    }

    /// <summary>
    /// Merges glyphs placed relative to one pen into one box with an advance, keeping the larger coverage where they
    /// overlap.
    /// </summary>
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

    /// <summary>Draws a glyph's coverage into a buffer at a position, keeping the larger value where it overlaps.</summary>
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

    /// <summary>As <see cref="BlitMax"/>, into 3 bytes a pixel; a glyph without Lcd gives its Alpha to all three.</summary>
    public static void BlitMaxLcd(byte[] buffer, int width, int height, RasterGlyph g, int x, int y)
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

    /// <summary>Makes a table mapping coverage to coverage to the power of <paramref name="exponent"/>.</summary>
    public static byte[] CoverageTable(float exponent)
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
            table[i] = (byte)Rounding.Round(255 * MathF.Pow(i / 255f, exponent));
        return table;
    }

    /// <summary>Gets the glyph with its coverage mapped through a table, in a new array (coverage may be shared).</summary>
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