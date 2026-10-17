using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Faces.Sources.Nominal;

/// <summary>Port of xivres glyph_merging_fixed_size_font; glyph IDs are codepoints.</summary>
internal sealed partial class MergingFaceSource(
    IFaceSource inner,
    FontElement def,
    GlyphMergingStruct merging,
    FontServices services,
    OutlineFaceSource? textOutline) : NominalFaceSource
{
    // Shapes are in 1/1000 em with y growing downwards and the baseline at y = 880, as in the Lodestone web font
    // (FFXIV_Lodestone_SSF) the built-in ones follow.
    private const float ShapeUnitsPerEm = 1000;
    private const float ShapeBaselineY = 880;

    // The Lodestone web font's level digits' capital height.
    private const float ShapelessCapHeight = 630;

    // In capital heights, from the game's AM/PM glyphs.
    private const float StackedLineAdvance = 1.11f;

    /// <summary>Has the element's gamma applied; glyphs are placed relative to the pen on the baseline.</summary>
    internal interface IMergeTextFont
    {
        int Ascent { get; }

        bool Has(Rune rune);

        MergeTextLine LayOut(string text, int letterSpacing);
    }

    public int MeasureMode
    {
        set
        {
            inner.MeasureMode = value;
            textOutline?.MeasureMode = value;
        }
    }

    public override IEnumerable<Rune> Codepoints => merging.Glyphs.Keys;

    public static MergingFaceSource Create(IFaceSource inner, FontElement def, GlyphMergingStruct merging, FontServices services, string fontName)
    {
        // On a glyph images element, a lookup family names the font for the texts instead of the images.
        var textOutline = inner is ImageFaceSource && def.Lookup.FamilyName.Length != 0
                              ? OutlineFaceSource.Create(services, def with { Source = ElementSource.DirectWrite }, fontName)
                              : inner as OutlineFaceSource;
        return new(inner, def, merging, services, textOutline);
    }

    public override uint GetGlyphId(Rune rune) => merging.Glyphs.ContainsKey(rune) ? (uint)rune.Value : 0;

    public override (int Ascent, int LineHeight) GetLineMetrics(float px) => inner.GetLineMetrics(px);

    public override bool TryGetBaseline(float px, Baseline baseline, out float y) => inner.TryGetBaseline(px, baseline, out y);

    public override void Dispose()
    {
        inner.Dispose();
        if (!ReferenceEquals(textOutline, inner))
            textOutline?.Dispose();
    }

    protected override RasterGlyph? RasterizeGlyph(float px, uint glyphId)
    {
        if (!Rune.IsValid(glyphId) || !merging.Glyphs.TryGetValue(new((int)glyphId), out var glyph))
            return null;
        var baseGlyph = glyph.Mapping.Shape == GlyphMergeShape.Glyph ? this.DrawInner(new((int)glyphId), px) : null;
        return this.Draw(glyph.Mapping, glyph.Text, px, baseGlyph);
    }

    private static bool IsCjk(int c) =>
        c is (>= 0x1100 and <= 0x11FF) or (>= 0x2E80 and <= 0x9FFF) or (>= 0xA960 and <= 0xA97F) or (>= 0xAC00 and <= 0xD7FF)
            or (>= 0xF900 and <= 0xFAFF) or (>= 0xFF00 and <= 0xFFEF) or >= 0x20000;

    private static bool IsSymbol(int c) =>
        (c < 0x80 && (char.IsPunctuation((char)c) || char.IsSymbol((char)c))) || c is 0xD7 or 0xF7 or (>= 0x2190 and <= 0x2BFF);

    // Spaces and line breaks aside; false for a text of nothing else.
    private static bool AllAre(string text, Func<int, bool> predicate)
    {
        var any = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var c = rune.Value;
            if (c is ' ' or '\n' or '\r')
                continue;
            if (!predicate(c))
                return false;
            any = true;
        }

        return any;
    }

    private static int GetIdeographHeight(IMergeTextFont font)
    {
        foreach (var c in "国가")
        {
            if (font.Has(new(c)) && font.LayOut(c.ToString(), 0) is { } line && line.InkY2 > line.InkY1)
                return line.InkY2 - line.InkY1;
        }

        return 0;
    }

    // At the game's font sizes, glyph_merge_shapes.json's "game" placements put shapes where the game's own glyphs are.
    private static GlyphShape MakeShape(GlyphMergeShape kind, int characters, float size, bool underline)
    {
        var s = new GlyphShape();
        var specs = GlyphMergeShapes.Shapes;
        if (!specs.TryGetProperty(GlyphMergeShapes.NameOf(kind), out var spec))
            return s;
        if (characters > 1 && spec.TryGetProperty("more", out var more))
            spec = more;
        s.Advance = spec.GetProperty("advance").GetSingle();

        // The game's placements are of the vertices' box; path data is placed as another shape is, with its text in that box.
        var placed = spec;
        (float X1, float Y1, float X2, float Y2) nominal;
        if (spec.TryGetProperty("path", out var path))
        {
            s.Path = ShapePath.Parse(path.GetString()!);
            placed = specs.GetProperty(spec.GetProperty("placedAs").GetString()!);
            s.Area = nominal = Bounds(Vertices(placed));
        }
        else
        {
            var vertices = Vertices(spec);
            var radius = spec.GetProperty("radius").GetSingle();
            s.Path.AddRoundedPolygon(vertices, radius);
            nominal = Bounds(vertices);
            s.Area = ReadBox(spec.GetProperty("area"));

            // A hollow shape's inside is cut out (reversed to make a hole) at least a pixel in, as the game's small glyphs draw it.
            if (spec.TryGetProperty("ring", out var ringSpec))
            {
                var ring = size > 0 ? Math.Max(ringSpec.GetSingle(), ShapeUnitsPerEm / size) : ringSpec.GetSingle();
                s.Path.AddRoundedPolygon(Rect((nominal.X1 + ring, nominal.Y1 + ring, nominal.X2 - ring, nominal.Y2 - ring), true), Math.Max(0, radius - ring));
                s.DrawsText = true;
            }

            if (underline && spec.TryGetProperty("underline", out var underlineSpec))
            {
                var bar = Rect(ReadBox(underlineSpec.GetProperty("bar")), true);
                s.Path.MoveTo(bar[0]);
                foreach (var v in bar[1..])
                    s.Path.LineTo(v);
                s.Path.Close();
                s.Area = ReadBox(underlineSpec.GetProperty("area"));
            }
        }

        var placement = placed.GetProperty("game").EnumerateArray().FirstOrDefault(p => MathF.Abs(p[0].GetSingle() - size) < 0.01f);
        if (placement.ValueKind != JsonValueKind.Array)
            return s;

        var (advance, gx1, gy1, gx2, gy2) = (placement[1].GetSingle(), placement[2].GetSingle(), placement[3].GetSingle(), placement[4].GetSingle(),
                                                placement[5].GetSingle());
        var sx = (gx2 - gx1) / (nominal.X2 - nominal.X1);
        var sy = (gy2 - gy1) / (nominal.Y2 - nominal.Y1);
        float MapX(float x) => gx1 + ((x - nominal.X1) * sx);
        float MapY(float y) => gy1 + ((y - nominal.Y1) * sy);
        for (var i = 0; i < s.Path.Points.Count; i++)
            s.Path.Points[i] = (MapX(s.Path.Points[i].X), MapY(s.Path.Points[i].Y));
        s.Area = (MapX(s.Area.X1), MapY(s.Area.Y1), MapX(s.Area.X2), MapY(s.Area.Y2));
        s.Advance = advance;
        return s;

        static (float, float, float, float) ReadBox(JsonElement v) => (v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle(), v[3].GetSingle());

        static (float X, float Y)[] Rect((float X1, float Y1, float X2, float Y2) r, bool reverse = false) =>
            reverse ? [(r.X1, r.Y1), (r.X1, r.Y2), (r.X2, r.Y2), (r.X2, r.Y1)] : [(r.X1, r.Y1), (r.X2, r.Y1), (r.X2, r.Y2), (r.X1, r.Y2)];

        static (float X, float Y)[] Vertices(JsonElement spec) => spec.TryGetProperty("rect", out var rect)
                                                                      ? Rect(ReadBox(rect))
                                                                      : spec.GetProperty("polygon").EnumerateArray()
                                                                            .Select(v => (X: v[0].GetSingle(), Y: v[1].GetSingle())).ToArray();

        static (float X1, float Y1, float X2, float Y2) Bounds((float X, float Y)[] vertices) =>
            (vertices.Min(v => v.X), vertices.Min(v => v.Y), vertices.Max(v => v.X), vertices.Max(v => v.Y));
    }

    private IMergeTextFont CreateTextFont(float size, float condense)
    {
        var transform = new Matrix2x2(condense, 0, 0, 1) * merging.TextTransform * def.Transform;
        return textOutline?.CreateMergeTextFont(size, transform)
               ?? (inner as ImageFaceSource)?.CreateMergeTextFont(size, transform)
               ?? NoTextFont.Instance;
    }

    private RasterGlyph? DrawInner(Rune rune, float px)
    {
        var id = inner.GetGlyphId(rune);
        if (id == 0)
            return null;
        ReadOnlySpan<uint> ids = [id];
        ReadOnlySpan<float> advances = [inner.GetAdvance(px, id)];
        return inner.Rasterize(px, new(ids, advances));
    }

    // The merging's pixel values are at the element's preset size, which built-in shapes are also placed at; shapes get no gamma.
    private RasterGlyph Draw(GlyphMergeMapping mapping, string mappedText, float size, RasterGlyph? baseGlyph)
    {
        var designSize = def.Size;
        var pixelScale = designSize > 0 ? size / designSize : 1;
        var ascent = inner.GetLineMetrics(size).Ascent;
        var scale = size / ShapeUnitsPerEm;
        float baselineY = ascent;

        // The bar of an underlined IME box stands for the text's leading "_".
        var underline = mapping.Shape == GlyphMergeShape.Ime && mappedText.StartsWith('_');
        var text = underline ? mappedText[1..] : mappedText;
        var characters = text.Count(c => c is not (' ' or '\n' or '\r'));
        var shape = MakeShape(mapping.Shape, characters, designSize, underline);

        void SetArea(float x1, float y1, float x2, float y2)
        {
            if (mapping.CustomTextArea is { } a)
            {
                shape.Area = a;
            }
            else if (x1 < x2 && y1 < y2)
            {
                var (insetX, insetY) = ((x2 - x1) * 0.15f, (y2 - y1) * 0.2f);
                shape.Area = (x1 + insetX, y1 + insetY, x2 - insetX, y2 - insetY);
            }
        }

        RasterGlyph? drawn = null;
        if (baseGlyph is { } g)
        {
            shape.Advance = g.Advance / scale;
            drawn = g with { Top = g.Top + ascent };
        }
        else if (mapping.Shape == GlyphMergeShape.Custom && mapping.CustomSvg.Length != 0)
        {
            shape.Advance = mapping.CustomAdvance;

            // Drawings may go past the line box, so an em of room is left around it and the ink is trimmed afterwards.
            var bx1 = (int)MathF.Floor(-ShapeUnitsPerEm * scale);
            var bx2 = (int)MathF.Ceiling((Math.Max(0, mapping.CustomAdvance) + ShapeUnitsPerEm) * scale);
            var by1 = (int)MathF.Floor(baselineY - (2 * ShapeUnitsPerEm * scale));
            var by2 = (int)MathF.Ceiling(baselineY + (ShapeUnitsPerEm * scale));
            var coverage = services.Images.DrawSvg(mapping.CustomSvg, [scale, 0, -bx1, 0, scale, baselineY - (ShapeBaselineY * scale) - by1], bx2 - bx1,
                by2 - by1);
            drawn = new RasterGlyph(0, bx1, by1, bx2 - bx1, by2 - by1, coverage).Trimmed();
        }
        else if (mapping.Shape == GlyphMergeShape.Custom)
        {
            shape.Path = ShapePath.Parse(mapping.CustomPath);
            shape.Advance = mapping.CustomAdvance;
            var (x1, y1, x2, y2) = shape.Path.GetBounds(1, ShapeBaselineY);
            SetArea(x1, y1, x2, y2);
        }

        if (drawn is { Width: not 0 } box)
        {
            shape.Raster = box;
            SetArea(
                box.Left / scale,
                ShapeBaselineY - ((baselineY - box.Top) / scale),
                (box.Left + box.Width) / scale,
                ShapeBaselineY - ((baselineY - box.Top - box.Height) / scale));
        }

        var hasArea = shape.Area.X1 < shape.Area.X2 && shape.Area.Y1 < shape.Area.Y2;
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var textScale = merging.TextSize is { } textSize ? textSize * pixelScale / size : 1;
        var condense = 1f;
        var cache = new Dictionary<(int, int), IMergeTextFont>();

        IMergeTextFont GetTextFont(float s, float c)
        {
            // Quantized as xivres get_text_font's cache keys are, so the sizes drawn match it.
            var key = ((int)Misc.StdRound(s * 20), (int)Misc.StdRound(c * 1000));
            if (!cache.TryGetValue(key, out var font))
                cache.Add(key, font = this.CreateTextFont(key.Item1 / 20f, key.Item2 / 1000f));
            return font;
        }

        if (shape.IsEmpty && merging.TextSize is null)
        {
            // The game's shapeless level digits and units are smaller than its text.
            var reference = this.LayOut(GetTextFont(size, 1), lines, pixelScale, false);
            if (reference.CapHeight > 0)
                textScale = ShapelessCapHeight * scale / reference.CapHeight;
        }

        var cjk = AllAre(text, IsCjk);
        if (hasArea)
        {
            var areaWidth = (shape.Area.X2 - shape.Area.X1) * scale;
            var areaHeight = (shape.Area.Y2 - shape.Area.Y1) * scale;
            var referenceFont = GetTextFont(size, 1);
            var reference = this.LayOut(referenceFont, lines, pixelScale, true);
            var referenceHeight = (float)(reference.LastBaseline - reference.CapTop);

            // Ideographs are drawn taller than the font's capitals, so CJK text is sized by an ideograph's ink instead.
            if ((cjk ? GetIdeographHeight(referenceFont) : 0) is var ideograph and > 0)
                referenceHeight = ideograph + reference.LastBaseline - referenceFont.Ascent;

            // Whole pixels tall, as the game's letters in shapes are, so a fractional area doesn't reach into another row.
            if (merging.TextSize is null && referenceHeight > 0)
                textScale = MathF.Max(1, MathF.Floor(areaHeight + 0.01f)) / referenceHeight;

            var width = (reference.InkX2 - reference.InkX1) * textScale;
            if (width > areaWidth && width > 0)
            {
                switch (merging.FitMode)
                {
                    case GlyphMergeFitMode.CondenseThenShrink:
                        condense = Math.Max(0.7f, areaWidth / width);
                        if (width * condense > areaWidth)
                            textScale *= areaWidth / (width * condense);
                        break;
                    case GlyphMergeFitMode.Shrink:
                        textScale *= areaWidth / width;
                        break;
                }
            }
        }

        var layout = this.LayOut(GetTextFont(size * textScale, condense), lines, pixelScale, hasArea);
        float textX;
        int textY;
        var advance = baseGlyph?.Advance ?? (int)Misc.StdRound(shape.Advance * scale);
        if (hasArea)
        {
            var centerX = ((shape.Area.X1 + shape.Area.X2) / 2 * scale) + (merging.TextOffsetX * pixelScale);
            var centerY = baselineY - ((ShapeBaselineY - ((shape.Area.Y1 + shape.Area.Y2) / 2)) * scale) + (merging.TextOffsetY * pixelScale);
            textX = centerX - ((layout.InkX1 + layout.InkX2) / 2);

            // Capital-height centering lines letters up across glyphs, but symbols and CJK don't sit on the capitals, so they center by their ink.
            textY = (AllAre(text, IsSymbol) || cjk) && layout.InkY1 < layout.InkY2
                        ? (int)Misc.StdRound(centerY - ((layout.InkY1 + layout.InkY2) / 2f))
                        : (int)Misc.StdRound(centerY - ((layout.CapTop + layout.LastBaseline) / 2f));
        }
        else
        {
            textX = merging.TextOffsetX * pixelScale;
            textY = (int)Misc.StdRound(baselineY - layout.Ascent + (merging.TextOffsetY * pixelScale));
            if (shape.IsEmpty)
                advance = layout.AdvanceWidth;
        }

        return this.Compose(mapping.TextMode, shape, scale, baselineY, layout, textX, textY, advance);
    }

    private RasterGlyph Compose(
        GlyphMergeTextMode textMode,
        GlyphShape shape,
        float scale,
        float baselineY,
        TextLayout layout,
        float textX,
        int textY,
        int advance)
    {
        var textOver = shape.IsEmpty || shape.DrawsText;
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
        if (shape.Raster is { } r)
        {
            (x1, y1, x2, y2) = (r.Left, r.Top, r.Left + r.Width, r.Top + r.Height);
        }
        else if (!shape.IsEmpty)
        {
            var (fx1, fy1, fx2, fy2) = shape.Path.GetBounds(scale, baselineY);
            (x1, y1, x2, y2) = ((int)MathF.Floor(fx1), (int)MathF.Floor(fy1), (int)MathF.Ceiling(fx2), (int)MathF.Ceiling(fy2));
        }

        if ((textOver || textMode == GlyphMergeTextMode.Difference) && layout.InkX1 < layout.InkX2)
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
        if (shape.Raster is { } shapeRaster)
        {
            RasterGlyph.BlitMax(shapeCoverage, width, height, shapeRaster, shapeRaster.Left - x1, shapeRaster.Top - y1);
        }
        else if (!shape.IsEmpty)
        {
            var points = shape.Path.ToPixels(scale, baselineY).Select(p => (p.X - x1, p.Y - y1)).ToArray();
            shapeCoverage = services.FreeType.FillOutline(points, shape.Path.Tags.ToArray(), shape.Path.Ends.ToArray(), width, height);
        }

        foreach (var (line, shift, y) in layout.Lines)
        {
            var g = line.Draw(textX + shift);
            RasterGlyph.BlitMax(textCoverage, width, height, g, g.Left - x1, textY + y + layout.Ascent + g.Top - y1);
        }

        var alpha = new byte[width * height];
        for (var i = 0; i < alpha.Length; i++)
        {
            int a = shapeCoverage[i], t = textCoverage[i];
            var v = textOver ? a + t - (a * t / 255)
                    : textMode == GlyphMergeTextMode.Difference ? a + t - (2 * a * t / 255)
                    : a * (255 - t) / 255;
            alpha[i] = (byte)Math.Clamp(v, 0, 255);
        }

        var trimmed = new RasterGlyph(advance, x1, y1, width, height, alpha).Trimmed();
        return trimmed with { Top = trimmed.Top - (int)baselineY };
    }

    // Port of xivres layout_text; aligning by advances keeps the font's side bearings, and y is from the first line's top.
    private TextLayout LayOut(IMergeTextFont font, string[] lines, float pixelScale, bool alignByInk)
    {
        var letterSpacing = (int)Misc.StdRound(merging.LetterSpacing * pixelScale);
        var laidOut = new List<MergeTextLine>();
        var maxInkWidth = 0f;
        var advanceWidth = 0;
        foreach (var text in lines)
        {
            var line = font.LayOut(string.Concat(text.EnumerateRunes().Where(font.Has).Select(r => r.ToString())), letterSpacing);
            laidOut.Add(line);
            maxInkWidth = Math.Max(maxInkWidth, line.InkX2 - line.InkX1);
            advanceWidth = Math.Max(advanceWidth, line.Width);
        }

        var h = font.Has(new('H')) ? font.LayOut("H", 0) : null;
        var capHeight = h is not null && h.InkY1 < h.InkY2 ? font.Ascent - h.InkY1 : (int)Misc.StdRound(font.Ascent * 0.7f);
        var lineAdvance = (int)Misc.StdRound((capHeight * StackedLineAdvance) + (merging.LineSpacing * pixelScale));

        var placed = new List<(MergeTextLine Line, float Shift, int Y)>();
        float inkX1 = float.MaxValue, inkX2 = float.MinValue;
        int inkY1 = int.MaxValue, inkY2 = int.MinValue;
        for (var i = 0; i < laidOut.Count; i++)
        {
            var line = laidOut[i];
            var lineWidth = alignByInk ? line.InkX2 - line.InkX1 : line.Width;
            var maxWidth = alignByInk ? maxInkWidth : advanceWidth;
            var shift = alignByInk ? -line.InkX1 : 0;
            shift += merging.LineAlignment switch
            {
                GlyphMergeLineAlignment.Center => (maxWidth - lineWidth) / 2,
                GlyphMergeLineAlignment.Right => maxWidth - lineWidth,
                _ => 0,
            };
            if (!line.Shaped)
                shift = Misc.StdRound(shift);

            var y = i * lineAdvance;
            placed.Add((line, shift, y));
            if (line.InkY1 < line.InkY2)
            {
                (inkX1, inkX2) = (Math.Min(inkX1, shift + line.InkX1), Math.Max(inkX2, shift + line.InkX2));
                (inkY1, inkY2) = (Math.Min(inkY1, y + line.InkY1), Math.Max(inkY2, y + line.InkY2));
            }
        }

        if (inkX1 > inkX2 || inkY1 > inkY2)
            (inkX1, inkX2, inkY1, inkY2) = (0, 0, 0, 0);
        return new(placed, font.Ascent, capHeight, font.Ascent + ((laidOut.Count - 1) * lineAdvance), inkX1, inkX2, inkY1, inkY2, advanceWidth);
    }

    /// <summary>
    /// Ink x is from the pen (from the outlines if shaped, so pixels may go past it) and ink y from the line's top, all 0
    /// without ink; <c>Draw</c> takes the pen x on the baseline, rounded to whole pixels unless shaped.
    /// </summary>
    internal sealed record MergeTextLine(int Width, bool Shaped, float InkX1, float InkX2, int InkY1, int InkY2, Func<float, RasterGlyph> Draw);

    private sealed class NoTextFont : IMergeTextFont
    {
        public static readonly NoTextFont Instance = new();

        public int Ascent => 0;

        public bool Has(Rune rune) => false;

        public MergeTextLine LayOut(string text, int letterSpacing) => new(0, false, 0, 0, 0, 0, _ => new(0, 0, 0, 0, 0, []));
    }

    private sealed class GlyphShape
    {
        public float Advance { get; set; }

        public ShapePath Path { get; set; } = new();

        // In pixels from the line's top, unlike Path's shape units.
        public RasterGlyph? Raster { get; set; }

        public (float X1, float Y1, float X2, float Y2) Area { get; set; }

        public bool DrawsText { get; set; }

        public bool IsEmpty => this.Path.Points.Count == 0 && this.Raster is null;
    }

    private sealed record TextLayout(
        List<(MergeTextLine Line, float Shift, int Y)> Lines,
        int Ascent,
        int CapHeight,
        int LastBaseline,
        float InkX1,
        float InkX2,
        int InkY1,
        int InkY2,
        int AdvanceWidth)
    {
        public int CapTop => this.Ascent - this.CapHeight;
    }
}
