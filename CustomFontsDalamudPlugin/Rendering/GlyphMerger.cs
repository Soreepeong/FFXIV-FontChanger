using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CustomFonts;

/// <summary>
/// A font the texts of glyph merging are drawn with: the element's, at a size and transformation. Its glyphs are placed
/// relative to the pen on the baseline.
/// </summary>
internal interface IMergeTextFont
{
    /// <summary>Gets the ascent in pixels.</summary>
    int Ascent { get; }

    /// <summary>Gets whether a codepoint can be drawn.</summary>
    bool Has(int codepoint);

    /// <summary>
    /// Lays out a line, its glyphs <paramref name="letterSpacing"/> pixels further apart. A shaped line is drawn at any
    /// fraction of a pixel; others are drawn at whole pixels.
    /// </summary>
    MergeTextLine LayOut(string text, int letterSpacing);
}

/// <summary>A laid out line: its advance, and how to draw it with the pen at an x on the baseline.</summary>
internal sealed record MergeTextLine(int Width, bool Shaped, Func<float, RasterGlyph> Draw);

/// <summary>
/// Draws merged glyphs (xivres glyph_merging_fixed_size_font): a text put in a shape, the text cut out of the shape (or
/// drawn inside a hollow one), or alone, smaller, on the baseline. Shapes are in units of 1/1000 em with y growing
/// downwards and the baseline at y = 880; the built-in ones follow the Lodestone web font (FFXIV_Lodestone_SSF).
/// </summary>
internal static class GlyphMerger
{
    private const float ShapeUnitsPerEm = 1000;
    private const float ShapeBaselineY = 880;

    // The capital height of texts without a shape (and a text size): the Lodestone web font's level digits'.
    private const float ShapelessCapHeight = 630;

    // Lines are stacked this many capital heights apart, as in the game's AM/PM glyphs.
    private const float StackedLineAdvance = 1.11f;

    private const byte TagOn = 1;
    private const byte TagConic = 0;
    private const byte TagCubic = 2;

    /// <summary>
    /// Draws a merged glyph of an element whose font is <paramref name="size"/> pixels with an ascent of
    /// <paramref name="ascent"/>, relative to the pen on the baseline. <paramref name="textFont"/> makes the text's font at
    /// a size and horizontal scale; <paramref name="baseGlyph"/> is the element's own glyph of the codepoint (for
    /// <see cref="MergeShape.Glyph"/>), and <paramref name="gamma"/> the element's gamma, which the text and that glyph
    /// get. Pixel values of <paramref name="def"/> are scaled by <paramref name="pixelScale"/>.
    /// </summary>
    public static RasterGlyph Draw(
        MergeMapping mapping,
        string text,
        GlyphMergingDef def,
        float pixelScale,
        float size,
        int ascent,
        Func<float, float, IMergeTextFont> textFont,
        RasterGlyph? baseGlyph,
        byte[]? gamma,
        ImageRenderer? images,
        FreeTypeFonts? freeType)
    {
        var scale = size / ShapeUnitsPerEm;
        float baselineY = ascent;
        var characters = text.Count(c => c is not (' ' or '\n' or '\r'));
        var shape = MakeShape(mapping.Shape, characters);

        // Shapes drawn into pixels, in coordinates from the top of the line: the element's glyph, or an SVG document.
        RasterGlyph? raster = null;
        void SetArea(float x1, float y1, float x2, float y2)
        {
            if (mapping.CustomTextArea is { } a)
            {
                shape.Area = a;
            }
            else if (x1 < x2 && y1 < y2)
            {
                // The middle of the shape's bounds.
                var (insetX, insetY) = ((x2 - x1) * 0.15f, (y2 - y1) * 0.2f);
                shape.Area = (x1 + insetX, y1 + insetY, x2 - insetX, y2 - insetY);
            }
        }

        void SetAreaFromPixels(RasterGlyph box) => SetArea(
            box.Left / scale,
            ShapeBaselineY - ((baselineY - box.Top) / scale),
            (box.Left + box.Width) / scale,
            ShapeBaselineY - ((baselineY - box.Top - box.Height) / scale));

        if (mapping.Shape == MergeShape.Glyph)
        {
            if (baseGlyph is { } g)
            {
                shape.Advance = g.Advance / scale;
                if (g.Width != 0)
                {
                    raster = (gamma is null ? g : g.WithCoverage(gamma)) with { Top = g.Top + ascent };
                    SetAreaFromPixels(raster.Value);
                }
            }
        }
        else if (mapping.Shape == MergeShape.Custom && mapping.CustomSvg.Length != 0)
        {
            shape.Advance = mapping.CustomAdvance;
            if (images is not null)
            {
                // Drawn with room around the line box, and trimmed to the ink.
                var bx1 = (int)MathF.Floor(-ShapeUnitsPerEm * scale);
                var bx2 = (int)MathF.Ceiling((Math.Max(0, mapping.CustomAdvance) + ShapeUnitsPerEm) * scale);
                var by1 = (int)MathF.Floor(baselineY - (2 * ShapeUnitsPerEm * scale));
                var by2 = (int)MathF.Ceiling(baselineY + (ShapeUnitsPerEm * scale));
                var coverage = images.DrawSvg(mapping.CustomSvg, [scale, 0, -bx1, 0, scale, baselineY - (ShapeBaselineY * scale) - by1], bx2 - bx1, by2 - by1);
                var trimmed = GlyphImages.Trim(new(0, bx1, by1, bx2 - bx1, by2 - by1, coverage));
                if (trimmed.Width != 0)
                {
                    raster = trimmed;
                    SetAreaFromPixels(trimmed);
                }
            }
        }
        else if (mapping.Shape == MergeShape.Custom)
        {
            shape.Path = ParseSvgPath(mapping.CustomPath);
            shape.Advance = mapping.CustomAdvance;
            var (x1, y1, x2, y2) = shape.Path.GetBounds(1, ShapeBaselineY);
            SetArea(x1, y1, x2, y2);
        }

        var hasShape = shape.Path.Points.Count != 0 || raster is not null;
        var hasArea = shape.Area.X1 < shape.Area.X2 && shape.Area.Y1 < shape.Area.Y2;

        // The text's size and horizontal scale, from its layout at the element's size.
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var textScale = def.TextSize is { } textSize ? textSize * pixelScale / size : 1;
        var condense = 1f;
        var cache = new Dictionary<(int, int), IMergeTextFont>();
        IMergeTextFont GetTextFont(float s, float c)
        {
            // Sizes in steps of 1/20 pixel, scales of 1/1000.
            var key = ((int)MathF.Round(s * 20), (int)MathF.Round(c * 1000));
            if (!cache.TryGetValue(key, out var font))
                cache.Add(key, font = textFont(key.Item1 / 20f, key.Item2 / 1000f));
            return font;
        }

        if (!hasShape && def.TextSize is null)
        {
            // Without a shape, the text is smaller than the element's, as the game's level digits and units are.
            var reference = LayOut(GetTextFont(size, 1), lines, def, pixelScale, false);
            if (reference.CapHeight > 0)
                textScale = ShapelessCapHeight * scale / reference.CapHeight;
        }

        if (hasArea)
        {
            var areaWidth = (shape.Area.X2 - shape.Area.X1) * scale;
            var areaHeight = (shape.Area.Y2 - shape.Area.Y1) * scale;
            var reference = LayOut(GetTextFont(size, 1), lines, def, pixelScale, true);
            var referenceHeight = (float)(reference.LastBaseline - reference.CapTop);
            if (def.TextSize is null && referenceHeight > 0)
                textScale = areaHeight / referenceHeight;

            var width = (reference.InkX2 - reference.InkX1) * textScale;
            if (width > areaWidth && width > 0)
            {
                switch (def.FitMode)
                {
                    case MergeFitMode.CondenseThenShrink:
                        condense = Math.Max(0.7f, areaWidth / width);
                        if (width * condense > areaWidth)
                            textScale *= areaWidth / (width * condense);
                        break;
                    case MergeFitMode.Shrink:
                        textScale *= areaWidth / width;
                        break;
                }
            }
        }

        var font = GetTextFont(size * textScale, condense);
        var layout = LayOut(font, lines, def, pixelScale, hasArea);
        float textX;
        int textY;
        var advance = (int)MathF.Round(shape.Advance * scale);
        if (mapping.Shape == MergeShape.Glyph && baseGlyph is { } bg)
            advance = bg.Advance;
        if (hasArea)
        {
            var centerX = ((shape.Area.X1 + shape.Area.X2) / 2 * scale) + (def.TextOffsetX * pixelScale);
            var centerY = baselineY - ((ShapeBaselineY - ((shape.Area.Y1 + shape.Area.Y2) / 2)) * scale) + (def.TextOffsetY * pixelScale);
            textX = centerX - ((layout.InkX1 + layout.InkX2) / 2);

            // Letters and digits are centered by the capitals' height, so they line up across glyphs; symbols such as +
            // sit around the middle of lowercase letters instead, and are centered by themselves.
            textY = IsSymbolsOnly(text) && layout.InkY1 < layout.InkY2
                ? (int)MathF.Round(centerY - ((layout.InkY1 + layout.InkY2) / 2f))
                : (int)MathF.Round(centerY - ((layout.CapTop + layout.LastBaseline) / 2f));
        }
        else
        {
            // Without a shape, the text sits on the element's baseline.
            textX = def.TextOffsetX * pixelScale;
            textY = (int)MathF.Round(baselineY - font.Ascent + (def.TextOffsetY * pixelScale));
            if (!hasShape)
                advance = layout.AdvanceWidth;
        }

        return Compose(mapping, shape, raster, hasShape, scale, baselineY, layout, font, textX, textY, advance, gamma, freeType) with
        {
            GammaApplied = true,
        };
    }

    /// <summary>
    /// Draws the shape and the text, and combines them: the text over a shape that draws it, cut out of others (and drawn
    /// where it goes past the shape, by its text mode), or alone. Returned relative to the pen on the baseline.
    /// </summary>
    private static RasterGlyph Compose(
        MergeMapping mapping,
        Shape shape,
        RasterGlyph? raster,
        bool hasShape,
        float scale,
        float baselineY,
        Layout layout,
        IMergeTextFont font,
        float textX,
        int textY,
        int advance,
        byte[]? gamma,
        FreeTypeFonts? freeType)
    {
        // The glyph's bounds: the shape's, and the text's where it can be seen.
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
        if (raster is { } r)
        {
            (x1, y1, x2, y2) = (r.Left, r.Top, r.Left + r.Width, r.Top + r.Height);
        }
        else if (hasShape)
        {
            var (fx1, fy1, fx2, fy2) = shape.Path.GetBounds(scale, baselineY);
            (x1, y1, x2, y2) = ((int)MathF.Floor(fx1), (int)MathF.Floor(fy1), (int)MathF.Ceiling(fx2), (int)MathF.Ceiling(fy2));
        }

        if ((!hasShape || shape.DrawsText || mapping.TextMode == MergeTextMode.Difference) && layout.InkX1 < layout.InkX2)
        {
            // Partly covered pixels may go past the ink measured; empty columns are trimmed afterwards.
            x1 = Math.Min(x1, (int)MathF.Floor(textX + layout.InkX1) - 1);
            y1 = Math.Min(y1, textY + layout.InkY1);
            x2 = Math.Max(x2, (int)MathF.Ceiling(textX + layout.InkX2) + 1);
            y2 = Math.Max(y2, textY + layout.InkY2);
        }

        if (x1 >= x2 || y1 >= y2)
            return new(advance, 0, 0, 0, 0, []);

        var width = x2 - x1;
        var height = y2 - y1;
        var shapeCoverage = new byte[width * height];
        var textCoverage = new byte[width * height];
        if (raster is { } shapeRaster)
        {
            Blit(shapeCoverage, width, height, shapeRaster, shapeRaster.Left - x1, shapeRaster.Top - y1);
        }
        else if (hasShape && freeType is not null)
        {
            var points = shape.Path.Points.Select(p => ((p.X * scale) - x1, baselineY - ((ShapeBaselineY - p.Y) * scale) - y1)).ToArray();
            shapeCoverage = freeType.FillOutline(points, shape.Path.Tags.ToArray(), shape.Path.Ends.ToArray(), width, height);
        }

        foreach (var (line, shift, y) in layout.Lines)
        {
            var g = line.Draw(textX + shift);
            if (gamma is not null)
                g = g.WithCoverage(gamma);
            Blit(textCoverage, width, height, g, g.Left - x1, textY + y + font.Ascent + g.Top - y1);
        }

        var alpha = new byte[width * height];
        for (var i = 0; i < alpha.Length; i++)
        {
            int a = shapeCoverage[i], t = textCoverage[i];
            var v = !hasShape || shape.DrawsText ? a + t - (a * t / 255)
                : mapping.TextMode == MergeTextMode.Difference ? a + t - (2 * a * t / 255)
                : a * (255 - t) / 255;
            alpha[i] = (byte)Math.Clamp(v, 0, 255);
        }

        var trimmed = GlyphImages.Trim(new(advance, x1, y1, width, height, alpha));
        return trimmed with { Top = trimmed.Top - (int)baselineY };
    }

    /// <summary>Draws coverage into a buffer at a position, keeping the larger value where it overlaps.</summary>
    private static void Blit(byte[] buffer, int width, int height, RasterGlyph g, int x, int y)
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

    /// <summary>Gets whether a text has nothing but symbols (+, arrows) besides spaces and line breaks.</summary>
    private static bool IsSymbolsOnly(string text)
    {
        var any = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var c = rune.Value;
            if (c is ' ' or '\n' or '\r')
                continue;
            if (!((c < 0x80 && char.IsPunctuation((char)c)) || (c < 0x80 && char.IsSymbol((char)c)) || c is 0xD7 or 0xF7 or (>= 0x2190 and <= 0x2BFF)))
                return false;
            any = true;
        }

        return any;
    }

    /// <summary>
    /// Lays out the lines of a text, stacked closely. Lines are aligned by their ink when the text is put in a shape, else
    /// by their advances, keeping the side bearings as they are in text of the font. Coordinates are from the top of the
    /// first line.
    /// </summary>
    private static Layout LayOut(IMergeTextFont font, string[] lines, GlyphMergingDef def, float pixelScale, bool alignByInk)
    {
        var letterSpacing = (int)MathF.Round(def.LetterSpacing * pixelScale);
        var laidOut = new List<(MergeTextLine Line, float InkX1, float InkX2, int InkY1, int InkY2)>();
        var maxInkWidth = 0f;
        var res = new Layout();
        foreach (var text in lines)
        {
            var available = string.Concat(text.EnumerateRunes().Where(r => font.Has(r.Value)).Select(r => r.ToString()));
            var line = font.LayOut(available, letterSpacing);
            var ink = line.Draw(0);
            var (ix1, ix2) = ink.Width == 0 ? (0f, 0f) : (ink.Left, ink.Left + ink.Width);
            laidOut.Add((line, ix1, ix2, font.Ascent + ink.Top, font.Ascent + ink.Top + ink.Height));
            maxInkWidth = Math.Max(maxInkWidth, ix2 - ix1);
            res.AdvanceWidth = Math.Max(res.AdvanceWidth, line.Width);
        }

        // The capitals' height, from H's ink.
        var h = font.Has('H') ? font.LayOut("H", 0).Draw(0) : default;
        var capHeight = h.Width != 0 ? -h.Top : (int)MathF.Round(font.Ascent * 0.7f);
        var lineAdvance = (int)MathF.Round((capHeight * StackedLineAdvance) + (def.LineSpacing * pixelScale));

        (res.InkX1, res.InkX2, res.InkY1, res.InkY2) = (float.MaxValue, float.MinValue, int.MaxValue, int.MinValue);
        for (var i = 0; i < laidOut.Count; i++)
        {
            var (line, ix1, ix2, iy1, iy2) = laidOut[i];
            var lineWidth = alignByInk ? ix2 - ix1 : line.Width;
            var maxWidth = alignByInk ? maxInkWidth : res.AdvanceWidth;
            var shift = alignByInk ? -ix1 : 0;
            shift += def.LineAlignment switch
            {
                MergeLineAlignment.Center => (maxWidth - lineWidth) / 2,
                MergeLineAlignment.Right => maxWidth - lineWidth,
                _ => 0,
            };
            if (!line.Shaped)
                shift = MathF.Round(shift);

            var y = i * lineAdvance;
            res.Lines.Add((line, shift, y));
            if (iy1 < iy2)
            {
                (res.InkX1, res.InkX2) = (Math.Min(res.InkX1, shift + ix1), Math.Max(res.InkX2, shift + ix2));
                (res.InkY1, res.InkY2) = (Math.Min(res.InkY1, y + iy1), Math.Max(res.InkY2, y + iy2));
            }
        }

        if (res.InkX1 > res.InkX2 || res.InkY1 > res.InkY2)
            (res.InkX1, res.InkX2, res.InkY1, res.InkY2) = (0, 0, 0, 0);
        res.CapHeight = capHeight;
        res.CapTop = font.Ascent - capHeight;
        res.LastBaseline = font.Ascent + ((laidOut.Count - 1) * lineAdvance);
        return res;
    }

    /// <summary>Makes a built-in shape; <paramref name="characters"/> is the length of the text, which some are sized by.</summary>
    private static Shape MakeShape(MergeShape kind, int characters)
    {
        var s = new Shape();
        switch (kind)
        {
            case MergeShape.Box:
                s.Advance = 1000;
                s.Path.AddRoundedPolygon(Rect(71, 71, 929, 926), 110);
                s.Area = (165, 159, 835, 850);
                break;

            case MergeShape.NumberBox:
                s.Advance = 1000;
                if (characters <= 1)
                {
                    s.Path.AddRoundedPolygon(Rect(71, 71, 929, 926), 110);
                    s.Area = (132, 192, 879, 816);
                }
                else
                {
                    s.Path.AddRoundedPolygon(Rect(75, 75, 926, 922), 110);
                    s.Area = (154, 199, 827, 793);
                }

                break;

            case MergeShape.Ime:
                s.Advance = 1000;
                s.Path.AddRoundedPolygon(Rect(28, 28, 972, 969), 121);
                s.Area = (105, 111, 895, 880);
                break;

            case MergeShape.HollowBox:
                s.Advance = 1000;
                s.Path.AddRoundedPolygon(Rect(45, 45, 955, 955), 145);
                s.Path.AddRoundedPolygon(Rect(95, 95, 905, 905, true), 95);
                s.Area = (180, 210, 820, 790);
                s.DrawsText = true;
                break;

            case MergeShape.AmPm:
                s.Advance = 750;
                s.Path.AddRoundedPolygon(Rect(41, 61, 699, 916), 110);
                s.Area = (107, 136, 626, 841);
                break;

            case MergeShape.Hexagon:
                s.Advance = 1250;
                s.Path.AddRoundedPolygon([(287, 0), (972, 0), (1257, 499), (972, 998), (287, 998), (1, 499)], 130);
                s.Area = (263, 196, 983, 812);
                break;

            case MergeShape.Rhombus:
                s.Advance = 1000;
                s.Path.AddRoundedPolygon([(500, -94), (1094, 499), (500, 1092), (-94, 499)], 236);
                s.Area = (185, 178, 815, 822);
                break;

            case MergeShape.Bozja:
                s.Advance = 1750;
                s.Path.AddRoundedPolygon(Rect(168, 50, 1582, 948), 200);
                s.Area = (330, 163, 1420, 837);
                break;

            case MergeShape.Time:
                s.Advance = 2000;
                s.Path.AddRoundedPolygon(Rect(63, 31, 1938, 967), 234);
                s.Area = (220, 156, 1780, 852);
                break;
        }

        return s;

        static (float X, float Y)[] Rect(float x1, float y1, float x2, float y2, bool reverse = false) =>
            reverse ? [(x1, y1), (x1, y2), (x2, y2), (x2, y1)] : [(x1, y1), (x2, y1), (x2, y2), (x1, y2)];
    }

    /// <summary>Parses SVG path data: M, L, H, V, C, S, Q, T, A and Z, absolute and relative.</summary>
    private static ShapePath ParseSvgPath(string d)
    {
        var p = new ShapePath();
        var i = 0;
        (float X, float Y) cur = (0, 0), start = (0, 0), lastControl = (0, 0);
        char lastCommand = '\0', command = '\0';
        while (true)
        {
            SkipSeparators();
            if (i >= d.Length)
                break;
            if (char.IsAsciiLetter(d[i]))
                command = d[i++];
            else if (command == '\0')
                throw new FormatException("Path data must begin with a command.");

            var relative = char.IsLower(command);
            var b = relative ? cur : (0f, 0f);
            var upper = char.ToUpperInvariant(command);
            switch (upper)
            {
                case 'M':
                    cur = start = Add(b, ReadPoint());
                    p.MoveTo(cur);

                    // Pairs after the first are lines.
                    command = relative ? 'l' : 'L';
                    break;
                case 'L':
                    cur = Add(b, ReadPoint());
                    p.LineTo(cur);
                    break;
                case 'H':
                    cur = ((relative ? cur.X : 0) + ReadNumber(), cur.Y);
                    p.LineTo(cur);
                    break;
                case 'V':
                    cur = (cur.X, (relative ? cur.Y : 0) + ReadNumber());
                    p.LineTo(cur);
                    break;
                case 'C':
                {
                    var c1 = Add(b, ReadPoint());
                    var c2 = Add(b, ReadPoint());
                    cur = Add(b, ReadPoint());
                    p.CubicTo(c1, c2, cur);
                    lastControl = c2;
                    break;
                }

                case 'S':
                {
                    var c1 = char.ToUpperInvariant(lastCommand) is 'C' or 'S' ? Reflect(cur, lastControl) : cur;
                    var c2 = Add(b, ReadPoint());
                    cur = Add(b, ReadPoint());
                    p.CubicTo(c1, c2, cur);
                    lastControl = c2;
                    break;
                }

                case 'Q':
                {
                    var c = Add(b, ReadPoint());
                    cur = Add(b, ReadPoint());
                    p.QuadTo(c, cur);
                    lastControl = c;
                    break;
                }

                case 'T':
                {
                    var c = char.ToUpperInvariant(lastCommand) is 'Q' or 'T' ? Reflect(cur, lastControl) : cur;
                    cur = Add(b, ReadPoint());
                    p.QuadTo(c, cur);
                    lastControl = c;
                    break;
                }

                case 'A':
                {
                    var rx = MathF.Abs(ReadNumber());
                    var ry = MathF.Abs(ReadNumber());
                    var angle = ReadNumber() * MathF.PI / 180;
                    var largeArc = ReadFlag();
                    var sweep = ReadFlag();
                    var end = Add(b, ReadPoint());
                    Arc(p, cur, end, rx, ry, angle, largeArc, sweep);
                    cur = end;
                    break;
                }

                case 'Z':
                    p.Close();
                    cur = start;
                    break;
                default:
                    throw new FormatException("Unsupported command in path data.");
            }

            lastCommand = command;
            if (upper == 'Z' && !NextIsNumber())
                command = '\0';
        }

        p.Close();
        return p;

        void SkipSeparators()
        {
            while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ','))
                i++;
        }

        bool NextIsNumber()
        {
            SkipSeparators();
            return i < d.Length && (char.IsAsciiDigit(d[i]) || d[i] is '-' or '+' or '.');
        }

        float ReadNumber()
        {
            SkipSeparators();
            var begin = i;
            if (i < d.Length && d[i] is '-' or '+')
                i++;
            bool dot = false, exponent = false;
            while (i < d.Length)
            {
                var c = d[i];
                if (char.IsAsciiDigit(c))
                {
                    i++;
                }
                else if (c == '.' && !dot && !exponent)
                {
                    dot = true;
                    i++;
                }
                else if (c is 'e' or 'E' && !exponent)
                {
                    exponent = true;
                    i++;
                    if (i < d.Length && d[i] is '-' or '+')
                        i++;
                }
                else
                {
                    break;
                }
            }

            if (begin == i)
                throw new FormatException("Invalid number in path data.");
            return float.Parse(d.AsSpan(begin, i - begin), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        (float, float) ReadPoint() => (ReadNumber(), ReadNumber());

        bool ReadFlag()
        {
            SkipSeparators();
            if (i >= d.Length || d[i] is not ('0' or '1'))
                throw new FormatException("Invalid flag in path data.");
            return d[i++] == '1';
        }

        static (float, float) Add((float X, float Y) a, (float X, float Y) b) => (a.X + b.X, a.Y + b.Y);

        static (float, float) Reflect((float X, float Y) p, (float X, float Y) c) => ((2 * p.X) - c.X, (2 * p.Y) - c.Y);
    }

    /// <summary>Adds an elliptical arc as cubic curves of at most 90 degrees (SVG 1.1 implementation notes F.6).</summary>
    private static void Arc(ShapePath p, (float X, float Y) from, (float X, float Y) to, float rx, float ry, float angle, bool largeArc, bool sweep)
    {
        if (rx == 0 || ry == 0)
        {
            p.LineTo(to);
            return;
        }

        var (cosA, sinA) = (MathF.Cos(angle), MathF.Sin(angle));
        var (dx, dy) = ((from.X - to.X) / 2, (from.Y - to.Y) / 2);
        var (x1p, y1p) = ((cosA * dx) + (sinA * dy), (-sinA * dx) + (cosA * dy));
        var lambda = (x1p * x1p / (rx * rx)) + (y1p * y1p / (ry * ry));
        if (lambda > 1)
            (rx, ry) = (rx * MathF.Sqrt(lambda), ry * MathF.Sqrt(lambda));

        var num = (rx * rx * ry * ry) - (rx * rx * y1p * y1p) - (ry * ry * x1p * x1p);
        var den = (rx * rx * y1p * y1p) + (ry * ry * x1p * x1p);
        var coef = MathF.Sqrt(Math.Max(0, num / den));
        if (largeArc == sweep)
            coef = -coef;
        var (cxp, cyp) = (coef * rx * y1p / ry, -coef * ry * x1p / rx);
        var cx = (cosA * cxp) - (sinA * cyp) + ((from.X + to.X) / 2);
        var cy = (sinA * cxp) + (cosA * cyp) + ((from.Y + to.Y) / 2);
        var theta1 = VectorAngle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        var delta = VectorAngle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && delta > 0)
            delta -= 2 * MathF.PI;
        else if (sweep && delta < 0)
            delta += 2 * MathF.PI;

        var pieces = Math.Max(1, (int)MathF.Ceiling((MathF.Abs(delta) / (MathF.PI / 2)) - 0.001f));
        var step = delta / pieces;
        var k = 4f / 3 * MathF.Tan(step / 4);
        for (var j = 0; j < pieces; j++)
        {
            var t1 = theta1 + (step * j);
            var t2 = t1 + step;
            var p1 = At(t1);
            var p2 = j + 1 == pieces ? to : At(t2);
            var d1 = Derivative(t1);
            var d2 = Derivative(t2);
            p.CubicTo((p1.X + (d1.X * k), p1.Y + (d1.Y * k)), (p2.X - (d2.X * k), p2.Y - (d2.Y * k)), p2);
        }

        (float X, float Y) At(float t) =>
            (cx + (rx * MathF.Cos(t) * cosA) - (ry * MathF.Sin(t) * sinA), cy + (rx * MathF.Cos(t) * sinA) + (ry * MathF.Sin(t) * cosA));

        (float X, float Y) Derivative(float t) =>
            ((-rx * MathF.Sin(t) * cosA) - (ry * MathF.Cos(t) * sinA), (-rx * MathF.Sin(t) * sinA) + (ry * MathF.Cos(t) * cosA));

        static float VectorAngle(float ux, float uy, float vx, float vy) => MathF.Atan2((ux * vy) - (uy * vx), (ux * vx) + (uy * vy));
    }

    /// <summary>A shape: its advance, outline, the area its text is fitted and centered in, and whether it draws the text.</summary>
    private sealed class Shape
    {
        public float Advance { get; set; }

        public ShapePath Path { get; set; } = new();

        public (float X1, float Y1, float X2, float Y2) Area { get; set; }

        public bool DrawsText { get; set; }
    }

    /// <summary>A laid out text: its lines and where they go (x shift, top), and its ink and capital lines from the first line's top.</summary>
    private sealed class Layout
    {
        public List<(MergeTextLine Line, float Shift, int Y)> Lines { get; } = [];

        public float InkX1 { get; set; }

        public float InkX2 { get; set; }

        public int InkY1 { get; set; }

        public int InkY2 { get; set; }

        public int CapHeight { get; set; }

        public int CapTop { get; set; }

        public int LastBaseline { get; set; }

        public int AdvanceWidth { get; set; }
    }

    /// <summary>Points with FreeType's outline tags; each contour starts on the curve and closes by itself.</summary>
    private sealed class ShapePath
    {
        public List<(float X, float Y)> Points { get; } = [];

        public List<byte> Tags { get; } = [];

        public List<short> Ends { get; } = [];

        public void MoveTo((float X, float Y) p)
        {
            this.Close();
            this.Points.Add(p);
            this.Tags.Add(TagOn);
        }

        public void LineTo((float X, float Y) p)
        {
            this.Points.Add(p);
            this.Tags.Add(TagOn);
        }

        public void QuadTo((float X, float Y) c, (float X, float Y) p)
        {
            this.Points.Add(c);
            this.Tags.Add(TagConic);
            this.LineTo(p);
        }

        public void CubicTo((float X, float Y) c1, (float X, float Y) c2, (float X, float Y) p)
        {
            this.Points.Add(c1);
            this.Tags.Add(TagCubic);
            this.Points.Add(c2);
            this.Tags.Add(TagCubic);
            this.LineTo(p);
        }

        public void Close()
        {
            var begin = this.Ends.Count == 0 ? 0 : this.Ends[^1] + 1;
            if (this.Points.Count <= begin)
                return;

            // The contour closes by itself: an explicit closing point is dropped.
            var first = this.Points[begin];
            var last = this.Points[^1];
            if (this.Points.Count - begin > 1 && this.Tags[^1] == TagOn && MathF.Abs(last.X - first.X) < 0.001f && MathF.Abs(last.Y - first.Y) < 0.001f)
            {
                this.Points.RemoveAt(this.Points.Count - 1);
                this.Tags.RemoveAt(this.Tags.Count - 1);
            }

            this.Ends.Add((short)(this.Points.Count - 1));
        }

        /// <summary>Adds a polygon with its corners rounded by up to a radius, approximated with cubic curves.</summary>
        public void AddRoundedPolygon((float X, float Y)[] vertices, float radius)
        {
            const float Kappa = 0.5522847f;
            var n = vertices.Length;
            var starts = new (float X, float Y)[n];
            var ends = new (float X, float Y)[n];
            for (var i = 0; i < n; i++)
            {
                var (prev, cur, next) = (vertices[(i + n - 1) % n], vertices[i], vertices[(i + 1) % n]);
                var (px, py) = (prev.X - cur.X, prev.Y - cur.Y);
                var (nx, ny) = (next.X - cur.X, next.Y - cur.Y);
                var (lp, ln) = (MathF.Sqrt((px * px) + (py * py)), MathF.Sqrt((nx * nx) + (ny * ny)));
                var r = Math.Min(radius, Math.Min(lp / 2, ln / 2));
                starts[i] = (cur.X + (px * r / lp), cur.Y + (py * r / lp));
                ends[i] = (cur.X + (nx * r / ln), cur.Y + (ny * r / ln));
            }

            this.MoveTo(ends[0]);
            for (var j = 1; j <= n; j++)
            {
                var i = j % n;
                var v = vertices[i];
                this.LineTo(starts[i]);
                this.CubicTo(
                    (starts[i].X + ((v.X - starts[i].X) * Kappa), starts[i].Y + ((v.Y - starts[i].Y) * Kappa)),
                    (ends[i].X + ((v.X - ends[i].X) * Kappa), ends[i].Y + ((v.Y - ends[i].Y) * Kappa)),
                    ends[i]);
            }

            this.Close();
        }

        /// <summary>Gets the bounds of the points in pixels from the top of the line, at a scale, with the baseline at <paramref name="baselineY"/>.</summary>
        public (float X1, float Y1, float X2, float Y2) GetBounds(float scale, float baselineY)
        {
            float x1 = float.MaxValue, y1 = float.MaxValue, x2 = float.MinValue, y2 = float.MinValue;
            foreach (var (px, py) in this.Points)
            {
                var x = px * scale;
                var y = baselineY - ((ShapeBaselineY - py) * scale);
                (x1, x2, y1, y2) = (Math.Min(x1, x), Math.Max(x2, x), Math.Min(y1, y), Math.Max(y2, y));
            }

            return (x1, y1, x2, y2);
        }
    }
}
