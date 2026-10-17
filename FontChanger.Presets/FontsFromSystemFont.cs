using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;

namespace FontChanger.Presets;

public static partial class FontsFromSystemFont
{
    private const string LodestoneFontName = "XIV AXIS Std ATK";

    // Text is drawn a little heavier; the icons and merged glyphs more so.
    private const float TextGamma = 1.2f;
    private const float SymbolGamma = 1.4f;

    private const int RenderNatural = 4;
    private const int RenderNaturalSymmetric = 5;
    private const int MeasureGdiNatural = 2;
    private const int GridFitEnabled = 2;
    private const int StyleItalic = 2;

    private static readonly RuneRange[] AllCodepoints = RuneRange.From(0x20, 0x10FFFF);
    private static readonly RuneRange[] PrivateUseCodepoints = RuneRange.From(0xE000, 0xF8FF);

    // Fonts of every Windows installation, for the texts of merged glyphs in scripts that the chosen font lacks.
    private static readonly string[] MergedTextFallbackFontNames = ["Yu Gothic UI", "Malgun Gothic"];

    // The game's symbols stay the game's, which fallback fonts don't draw.
    private static readonly FontAdjustments FallbackAdjustments = new() { Scripts = FontScripts.All & ~FontScripts.GameSymbols };

    private static readonly List<RuneRange> MergeableCodepoints =
        GlyphMergeShapes.Presets.Select(e => new RuneRange(e.Codepoint, e.Codepoint)).Coalesce();

    public static Font? MakeFont(
        string gameFontName,
        float size,
        LookupStruct chosen,
        TryOpenFontDelegate tryOpenFont,
        bool monospacedDigits,
        IFontProbe? gameFont,
        IReadOnlyList<IFontProbe> fontsAbove,
        FontAdjustments adjustments)
    {
        if (!tryOpenFont(chosen, out var font))
            return null;

        var drawsNonCjk = adjustments.Scripts.HasFlag(FontScripts.NonCjk);
        var raise = StdRound(adjustments.Raise * size);
        var nonCjkSpacing = StdRound(adjustments.NonCjkSpacing * size);
        var cjkSpacing = StdRound(adjustments.CjkSpacing * size);
        var textGamma = GammaOf(adjustments.StrokeWeight);

        var traits = Traits.Of(Preset.FamilyOf(gameFontName));
        var placement = Placement.Of(traits, size, GameFonts.TryFind(gameFontName, out var def) ? def.Size : size);
        var features = new Dictionary<uint, uint>(chosen.Features);
        if (monospacedDigits)
            features[Preset.Tag("tnum"u8)] = 1;
        foreach (var feature in traits.Features)
            features[Preset.Tag(feature)] = 1;
        var lookup = chosen with { Features = features };

        LineMetrics? line = null;
        if (gameFont is { LineHeight: > 0 })
        {
            var ascent = StdRound(gameFont.Ascent * size);
            line = new(ascent, StdRound(gameFont.LineHeight * size) - ascent);
        }

        // CJK scripts are fitted next to the Latin letters as fitted, before the user's scaling.
        var latin = FitLatin(font, traits, size);
        var scaledLatin = ScaleLatin(font, latin, adjustments.NonCjkSize);
        var latinOffset = placement.LatinOffset;

        // JupiterN's "CriticalDirectHit!!": the digits move right, toward the italic exclamation mark.
        FontElement? markElement = null;
        if (traits.Mark is { } mark && drawsNonCjk)
        {
            var italic = chosen with { Style = StyleItalic };
            var italicFont = tryOpenFont(italic, out var openedItalic) ? openedItalic : font;
            var placed = PlaceExclamationMark(mark, font, italicFont, scaledLatin.Size, size, latinOffset);
            latinOffset = placed.DigitOffset;
            markElement = Make(
                italic,
                placed.Size,
                placed.RenderMode,
                textGamma,
                RuneRange.From('!', '!'),
                CodepointMergeMode.Replace,
                placed.Offset,
                placed.VerticalOffset - raise,
                placed.Spacing);
        }

        var latinPlaced = new GroupPlacement(scaledLatin.Size, scaledLatin.RenderMode, latinOffset, scaledLatin.VerticalOffset - raise);
        var parts = new List<(CjkGroup? Group, IReadOnlyList<RuneRange> Ranges, GroupPlacement Placement)>();
        if (traits.HasLetters)
        {
            // A font that doesn't draw its Latin letters isn't the base font.
            var baseFont = (drawsNonCjk ? fontsAbove.Append(font) : fontsAbove).FirstOrDefault(f => traits.ReferenceCharacters.EnumerateRunes().Any(r => f.TryGetGlyph(r, out _)));
            var targetCapHeight = size * traits.ReferenceHeight;

            // Symbols that the game and the font both draw an em wide go with the Han characters.
            var emWideSymbols = MeasureInk(font, CjkGroup.Han.Samples) is not null ? GetEmWideSymbols(font, gameFont) : [];

            // Groups that the font lacks stay with the rest; those it isn't to draw are left out, but for the symbols.
            var rest = AllCodepoints.Subtract(emWideSymbols);
            foreach (var group in CjkGroup.All)
            {
                var part = AllCodepoints.Intersect(group.Codepoints);
                if (!adjustments.Scripts.HasFlag(group.Scripts))
                {
                    rest = rest.Subtract(part);
                    if (group == CjkGroup.Han)
                        rest = rest.Concat(emWideSymbols).Coalesce();
                    continue;
                }

                if (MeasureInk(font, group.Samples) is not { } ink)
                    continue;

                if (group == CjkGroup.Han)
                    part = part.Concat(emWideSymbols).Coalesce();
                rest = rest.Subtract(part);

                var baseInk = baseFont is null ? null : ReferenceEquals(baseFont, font) ? ink : MeasureInk(baseFont, group.Samples);
                GroupPlacement placed;
                if (baseInk is not { } b || IsLeftToGame(font, baseFont, gameFont, group))
                {
                    placed = FitInk(font, ink, group.TopPerCapHeight * targetCapHeight, group.BottomPerCapHeight * targetCapHeight, latin.Size, placement.LatinOffset);
                }
                else if (ReferenceEquals(baseFont, font))
                {
                    placed = KeepWithinLine(font, ink, new(latin.Size, latin.RenderMode, placement.LatinOffset, latin.VerticalOffset + placement.CjkShift), line, latin.Size);
                }
                else
                {
                    placed = FitInk(font, ink, b.Top * size, b.Bottom * size, latin.Size, placement.LatinOffset);
                }

                placed = ScaleAroundInk(font, ink, placed, adjustments.CjkSize);
                parts.Add((group, part, placed with { VerticalOffset = placed.VerticalOffset - raise }));
            }

            if (rest.Count != 0 && drawsNonCjk)
                parts.Insert(0, (null, rest, latinPlaced));
        }
        else if (drawsNonCjk)
        {
            parts.Add((null, AllCodepoints, latinPlaced));
        }

        // The game's fonts other than KrnAXIS lack Hangul and the Han characters of Chinese, so those are added rather than
        // only replacing the game's.
        var isChinese = HasText(font, "们");
        var res = parts.Select(
                           p => Make(
                               lookup,
                               p.Placement.Size,
                               p.Placement.RenderMode,
                               textGamma,
                               p.Ranges,
                               p.Group == CjkGroup.Hangul || (p.Group == CjkGroup.Han && isChinese) ? CodepointMergeMode.AddAll : CodepointMergeMode.Replace,
                               p.Placement.HorizontalOffset,
                               p.Placement.VerticalOffset,
                               p.Group is null ? nonCjkSpacing : cjkSpacing))
                       .ToList();

        if (drawsNonCjk && monospacedDigits && !HasTabularDigits(font))
        {
            res.Add(
                Make(
                    lookup,
                    scaledLatin.Size,
                    scaledLatin.RenderMode,
                    textGamma,
                    RuneRange.From('0', '9'),
                    CodepointMergeMode.Replace,
                    latinOffset,
                    scaledLatin.VerticalOffset - raise,
                    nonCjkSpacing,
                    monospacing: new(1, 1, MonospacingUnit.ReferenceCharacter, new('0'), MonospacingAlignment.CenterAdvance)));
        }

        if (markElement is not null)
            res.Add(markElement);

        if (traits.IsAxis && adjustments.Scripts.HasFlag(FontScripts.GameSymbols))
        {
            // Text is shown in this font, the fonts drawn over it, and the game's glyphs that stay.
            var merged = MakeAxisElements(
                chosen,
                font,
                size,
                tryOpenFont,
                r => font.TryGetGlyph(r, out _) || fontsAbove.Any(f => f.TryGetGlyph(r, out _)) || gameFont?.TryGetGlyph(r, out _) == true,
                out var mergedCodepoints);

            // Vector PUA glyphs scale to any size; they follow the font's elements, which decide its metrics, and unmerged
            // mergeable ones keep the game's, as the Lodestone font places them apart.
            res.Add(
                Make(
                    LookupStruct.Of(LodestoneFontName),
                    size,
                    RenderNaturalSymmetric,
                    SymbolGamma,
                    PrivateUseCodepoints.Subtract(MergeableCodepoints.Subtract(mergedCodepoints)),
                    CodepointMergeMode.Replace,
                    placement.PuaOffset,
                    placement.CjkShift));
            res.AddRange(merged);
        }

        return new()
        {
            Name = gameFontName,
            Elements =
            [
                new() { Size = size, Source = ElementSource.Game, Ranges = RuneRange.From(0, 0x10FFFF) },
                .. res
            ]
        };
    }

    /// <summary>
    /// Fits a system fallback font as an item under the font's own with no adjustments, rather than at the size DirectWrite
    /// would draw it; <paramref name="size"/> is the one <see cref="MakeFont"/> is given for the game font, and
    /// <paramref name="drawn"/> is <see cref="ProbeFont"/> of the font. Of the elements, the last that has a glyph draws it.
    /// </summary>
    public static List<FontElement> MakeFallbackElements(
        Font font,
        float size,
        LookupStruct fallback,
        TryOpenFontDelegate tryOpenFont,
        IFontProbe drawn,
        IFontProbe? gameFont)
    {
        if (font.Elements is not [{ Size: > 0 } first, ..] ||
            MakeFont(font.Name, size, fallback, tryOpenFont, false, gameFont, [drawn], FallbackAdjustments) is not { } made)
        {
            return [];
        }

        // Private-use characters keep the system's icon font at its own size.
        var factor = first.Size / size;
        return made.Elements
                   .Where(e => e.Source != ElementSource.Game)
                   .Select(e => e.Scaled(e.MergeMode, factor) with { Ranges = e.Ranges.Subtract(PrivateUseCodepoints) })
                   .ToList();
    }

    /// <summary>In ems of the first element's size, from the system font elements only, as the C++ twin
    /// FontsFromSystemFont::ProbeFont measures.</summary>
    public static IFontProbe ProbeFont(Font font, TryOpenFontDelegate tryOpenFont) => new DrawnFontProbe(font, tryOpenFont);

    private static float GammaOf(StrokeWeight weight) => weight switch
    {
        StrokeWeight.Lighter => 1.0f,
        StrokeWeight.Heavier => 1.4f,
        StrokeWeight.Heaviest => 1.7f,
        _ => TextGamma,
    };

    private static FontElement Make(
        LookupStruct lookup,
        float size,
        int renderMode,
        float gamma,
        IReadOnlyList<RuneRange> ranges,
        CodepointMergeMode mergeMode,
        float horizontalOffset = 0,
        float verticalOffset = 0,
        float letterSpacing = 0,
        GlyphMergingStruct? glyphMerging = null,
        Monospacing? monospacing = null) => new()
    {
        Monospacing = monospacing,
        Size = size,
        Gamma = gamma,
        MergeMode = mergeMode,
        Source = ElementSource.DirectWrite,
        Lookup = lookup with { AllowSynthesis = true },
        Ranges = ranges,
        HorizontalOffset = horizontalOffset,
        VerticalOffset = verticalOffset,
        LetterSpacing = letterSpacing,
        DirectWrite = new(renderMode, MeasureGdiNatural, GridFitEnabled),
        GlyphMerging = glyphMerging,
    };

    /// <summary>Merged glyphs aren't moved as the Latin letters are, since their shapes sit where the game's glyphs are.</summary>
    private static List<FontElement> MakeAxisElements(
        LookupStruct lookup,
        IFontProbe font,
        float size,
        TryOpenFontDelegate tryOpenFont,
        Func<Rune, bool> shown,
        out List<RuneRange> merged)
    {
        var fonts = new List<(LookupStruct Lookup, IFontProbe Probe)> { (lookup with { Features = new Dictionary<uint, uint>() }, font) };
        foreach (var name in MergedTextFallbackFontNames)
        {
            if (tryOpenFont(LookupStruct.Of(name), out var fallback))
                fonts.Add((LookupStruct.Of(name), fallback));
        }

        // Each glyph goes to the first font that has its text; to a fallback font only if the surrounding text shows it.
        var glyphsByFont = fonts.Select(_ => new Dictionary<Rune, (GlyphMergeMapping, string)>()).ToList();
        foreach (var (codepoint, mapping, text) in GlyphMergeShapes.Presets)
        {
            var index = fonts.FindIndex(f => HasText(f.Probe, text.TrimStart('_')));
            if (index < 0 || (index > 0 && !text.EnumerateRunes().All(r => r.Value is '_' or '\n' || shown(r))))
                continue;
            glyphsByFont[index][codepoint] = (mapping, text);
        }

        merged = glyphsByFont.SelectMany(g => g.Keys).Select(c => new RuneRange(c, c)).Coalesce();
        var res = new List<FontElement>();
        for (var i = 0; i < fonts.Count; i++)
        {
            var glyphs = glyphsByFont[i];
            if (glyphs.Count == 0)
                continue;

            res.Add(
                Make(
                    fonts[i].Lookup,
                    size,
                    RenderNaturalSymmetric,
                    SymbolGamma,
                    glyphs.Keys.Select(c => new RuneRange(c, c)).Coalesce(),
                    CodepointMergeMode.Replace,
                    glyphMerging: new() { Glyphs = glyphs }));
        }

        return res;
    }

    private sealed class DrawnFontProbe : IFontProbe
    {
        private readonly List<(FontElement Element, IFontProbe? Probe)> _elements;
        private readonly float _size;

        public DrawnFontProbe(Font font, TryOpenFontDelegate tryOpenFont)
        {
            this._size = font.Elements.Count != 0 && font.Elements[0].Size > 0 ? font.Elements[0].Size : 1;
            this._elements = font.Elements
                                 .Select(e => (e, e is { Source: ElementSource.DirectWrite or ElementSource.FreeType, GlyphMerging: null } && tryOpenFont(e.Lookup, out var p) ? p : null))
                                 .ToList();
        }

        public IEnumerable<Rune> Codepoints =>
            this._elements.Where(e => e.Probe is not null)
                .SelectMany(e => e.Probe!.Codepoints.Where(e.Element.Contains))
                .Distinct()
                .Where(r => this.TryGetGlyph(r, out _));

        public float Ascent => 0;

        public float LineHeight => 0;

        public bool TryGetGlyph(Rune rune, out GlyphBox box)
        {
            // Elements take codepoints by their merge modes, the game's having all of theirs and empty ones none.
            var drawer = -1;
            for (var i = 0; i < this._elements.Count; i++)
            {
                var (element, probe) = this._elements[i];
                var has = element.Contains(rune) && (element.Source == ElementSource.Game || probe?.TryGetGlyph(rune, out _) == true);
                if (!has || (element.MergeMode == CodepointMergeMode.AddNew && drawer >= 0) || (element.MergeMode == CodepointMergeMode.Replace && drawer < 0))
                    continue;
                drawer = i;
            }

            if (drawer < 0 || this._elements[drawer].Probe is not { } drawerProbe || !drawerProbe.TryGetGlyph(rune, out var g))
            {
                box = default;
                return false;
            }

            var e = this._elements[drawer].Element;
            var scale = e.Size / this._size;
            var h = StdRound(e.HorizontalOffset) / this._size;
            var v = StdRound(e.VerticalOffset) / this._size;
            var spacing = StdRound(e.LetterSpacing) / this._size;
            box = new(
                (g.LeftSideBearing * scale) + h,
                ((g.RightSideBearing * scale) - h) + spacing,
                (g.AdvanceWidth * scale) + spacing,
                (g.BoundingBoxTop * scale) - v,
                (g.BoundingBoxBottom * scale) - v);
            return true;
        }
    }
}
