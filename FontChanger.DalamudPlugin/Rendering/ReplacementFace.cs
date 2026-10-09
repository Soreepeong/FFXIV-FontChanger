using System;
using System.Collections.Generic;
using System.Linq;

using TerraFX.Interop.DirectX;

namespace CustomFonts;

/// <summary>An element of a face: its definition, and the font it draws with (<see cref="IElementFont"/>).</summary>
internal sealed class FaceElement(ElementDef def, IElementFont font) : IDisposable
{
    public ElementDef Def { get; } = def;

    public IElementFont Font { get; } = font;

    /// <summary>Gets the element's font if its glyphs are shaped with DirectWrite: those of a font, not merged or images.</summary>
    public OutlineElementFont? Shaped => this.Font as OutlineElementFont;

    /// <summary>Gets whether the game draws the element's glyphs (its own).</summary>
    public bool DrawsGame => this.Font is GameElementFont;

    public void Dispose() => this.Font.Dispose();
}

/// <summary>
/// A face of a preset made usable (xivres merged_fixed_size_font): its elements' fonts, which element draws each
/// codepoint, how its glyphs are adjusted, and its line metrics at a drawn size.
/// </summary>
/// <remarks>
/// <para>Sizes in a face are relative to its first element: at a drawn size <c>px</c>, an element draws at
/// <c>px * element.Size / first.Size</c>, and pixel values in the face (letter spacing, offsets, Empty metrics) scale by
/// <c>px / first.Size</c>. The first element gives the line metrics.</para>
/// <para>A codepoint goes to the elements in order: an element has it if it is in its ranges and its font has it; AddNew
/// takes it if no element has yet, AddAll always, Replace only from an earlier element. Characters no element has are
/// the game's, or with <see cref="SystemFallback"/>, the system fonts': in shaped text DirectWrite's fallback, glyph by
/// glyph the built-in face's.</para>
/// <para>An element with glyph merging has only its merged codepoints, and an element of glyph images only those it has
/// files for; both are drawn glyph by glyph, not shaped.</para>
/// </remarks>
internal sealed unsafe class ReplacementFace : IDisposable
{
    // The built-in face: Segoe UI, the game's icon font, then fonts for scripts Segoe UI lacks.
    private static readonly string[] BuiltInFamilies =
    [
        "Segoe UI", TextShaper.IconFamily, "Yu Gothic UI", "Malgun Gothic", "Microsoft YaHei UI", "Microsoft JhengHei UI",
        "Nirmala UI", "Leelawadee UI", "Segoe UI Symbol", "Segoe UI Emoji",
    ];

    private static readonly uint RomanBaseline = Preset.Tag("romn");
    private static readonly uint IdeographicEmBoxBottom = Preset.Tag("ideo");
    private static readonly uint IdeographicEmBoxTop = Preset.Tag("idtp");
    private static readonly uint IdeographicFaceBottom = Preset.Tag("icfb");
    private static readonly uint IdeographicFaceTop = Preset.Tag("icft");

    private readonly GlyphRasterizer rasterizer;
    private readonly FaceDef def;
    private readonly FaceElement[] elements;
    private readonly FaceElement gameElement = new(new() { Renderer = ElementRenderer.Game }, new GameElementFont());
    private readonly ReplacementFace? fallback;
    private readonly Dictionary<(nint Game, int Codepoint), FaceElement?> elementCache = [];
    private readonly Dictionary<(FaceElement Element, float Px), float> referenceAdvances = [];

    /// <summary>
    /// Sets a face up. With <paramref name="systemFallback"/>, characters no element has are drawn with system fonts:
    /// <paramref name="fallback"/>'s glyph by glyph.
    /// </summary>
    public ReplacementFace(GlyphRasterizer rasterizer, FaceDef def, bool systemFallback, ReplacementFace? fallback)
    {
        this.rasterizer = rasterizer;
        this.def = def;
        this.SystemFallback = systemFallback;
        this.fallback = systemFallback ? fallback : null;
        var elements = new List<FaceElement>();
        try
        {
            foreach (var e in def.Elements)
                elements.Add(new(e, CreateFont(e, rasterizer, def.Name)));
        }
        catch
        {
            foreach (var e in elements)
                e.Dispose();
            throw;
        }

        this.elements = elements.ToArray();
        this.ReferenceSize = def.Elements.Count != 0 && def.Elements[0].Size > 0 ? def.Elements[0].Size : 1;
        this.Primary = Array.Find(this.elements, e => e.Shaped is not null);
        this.MeasureMode = this.Primary?.Def.DirectWrite.MeasureMode ?? 0;

        // The face's text is measured one way, its primary element's; every font of it draws glyphs measured so.
        foreach (var e in this.elements)
        {
            foreach (var outline in OutlineFonts(e.Font))
                outline.MeasureMode = this.MeasureMode;
        }
    }

    /// <summary>Gets whether characters no element has are drawn with system fonts instead of the game's glyphs.</summary>
    public bool SystemFallback { get; }

    /// <summary>Gets the size the face's sizes are relative to: its first element's.</summary>
    public float ReferenceSize { get; }

    /// <summary>Gets the first element whose glyphs are shaped (the font text is laid out in by default), or null.</summary>
    public FaceElement? Primary { get; }

    /// <summary>Gets how the face's text is measured (DWRITE_MEASURING_MODE): its primary element's, for all its glyphs.</summary>
    public int MeasureMode { get; }

    /// <summary>Makes the built-in face: <see cref="BuiltInFamilies"/>, then the system's fallback.</summary>
    public static ReplacementFace CreateBuiltIn(GlyphRasterizer rasterizer)
    {
        var elements = BuiltInFamilies
            .Select(family => new ElementDef { Size = 1, Renderer = ElementRenderer.DirectWrite, Lookup = LookupDef.Of(family), Ranges = [(0x20, 0x10FFFF)] })
            .ToList();
        return new(rasterizer, new() { Name = "(built-in)", Elements = elements }, true, null);
    }

    /// <summary>
    /// Makes the face of game fonts no preset gives one: the game's glyphs, and for characters the game font lacks, with
    /// <paramref name="systemFallback"/>, system fonts (<paramref name="fallback"/>'s glyph by glyph).
    /// </summary>
    public static ReplacementFace CreateGame(GlyphRasterizer rasterizer, bool systemFallback, ReplacementFace fallback)
    {
        var element = new ElementDef { Size = 1, Renderer = ElementRenderer.Game, Ranges = [(0, 0x10FFFF)] };
        return new(rasterizer, new() { Name = "(game)", Elements = [element] }, systemFallback, fallback);
    }

    public void Dispose()
    {
        foreach (var e in this.elements)
            e.Dispose();
    }

    /// <summary>Gets the size an element draws at for a drawn size.</summary>
    public float GetElementPx(FaceElement element, float px) => px * element.Def.Size / this.ReferenceSize;

    /// <summary>Gets a pixel value of the face (at its reference size) at a drawn size, rounded.</summary>
    public int ScalePixels(float value, float px) => (int)Rounding.Round(value * px / this.ReferenceSize);

    /// <summary>
    /// Gets the element that draws a codepoint (one whose <see cref="FaceElement.DrawsGame"/> if the game's glyph is
    /// used), or null if the system's fonts draw it.
    /// </summary>
    public FaceElement? GetElement(int codepoint, GameFont* game)
    {
        if (this.elementCache.TryGetValue(((nint)game, codepoint), out var cached))
            return cached;

        FaceElement? assigned = null;
        foreach (var e in this.elements)
        {
            // Asked only of elements that would take it.
            var mode = e.Def.MergeMode;
            if ((mode == MergeMode.AddNew && assigned is not null) || (mode == MergeMode.Replace && assigned is null))
                continue;
            if (e.Def.Contains(codepoint) && e.Font.Has(codepoint, game))
                assigned = e;
        }

        if (assigned is null && !this.SystemFallback)
            assigned = this.gameElement;
        this.elementCache[((nint)game, codepoint)] = assigned;
        return assigned;
    }

    /// <summary>Gets the codepoint whose glyph an element draws for a codepoint (its replacements).</summary>
    public static int GetDrawnCodepoint(FaceElement element, int codepoint) =>
        element.Def.Replacements.TryGetValue(codepoint, out var to) ? to : codepoint;

    /// <summary>Gets the line metrics at a drawn size: from the first element, rounded to whole pixels.</summary>
    public (int Ascent, int LineHeight) GetLineMetrics(float px, GameFont* game)
    {
        var (ascent, lineHeight) = this.elements.Length == 0 ? (0, 0) : this.GetElementMetrics(this.elements[0], px, game);
        if (lineHeight <= 0)
        {
            // Nothing to take them from: proportions of Segoe UI.
            ascent = (int)MathF.Ceiling(px * 1.08f);
            lineHeight = (int)MathF.Ceiling(px * 1.33f);
        }

        return (ascent, lineHeight);
    }

    /// <summary>
    /// Rasterizes a codepoint at a drawn size, in its cell (<see cref="Wrap"/>); false if the game's glyph is to be used.
    /// </summary>
    public bool TryRasterize(int codepoint, float px, GameFont* game, out RasterGlyph glyph)
    {
        glyph = default;
        var element = this.GetElement(codepoint, game);
        if (element is null)
        {
            // At the size the face's text is laid out in.
            var size = this.Primary is { } primary ? this.GetElementPx(primary, px) : px;
            return this.fallback is not null && this.fallback.TryRasterize(codepoint, size, null, out glyph);
        }

        var font = element.Font;
        var elementPx = this.GetElementPx(element, px);
        var drawn = GetDrawnCodepoint(element, codepoint);
        if (font.Rasterize(drawn, elementPx) is not { } raw)
            return false;

        glyph = this.Wrap(element, raw, px, game, scale => font.Squeeze(drawn, elementPx, raw, scale));
        return true;
    }

    /// <summary>
    /// Adjusts a glyph of an element at a drawn size as the face says (xivres wrapping_fixed_size_font): placed in its
    /// monospaced cell (<see cref="Monospace"/>; <paramref name="squeezed"/> draws it narrower), its letter spacing added
    /// to the advance, its horizontal offset to the ink (not the pen), and moved down by the element's vertical alignment
    /// in the face and baseline shift. <paramref name="glyph"/>'s advance is the whole pixels it takes, unadjusted.
    /// </summary>
    public RasterGlyph Wrap(FaceElement element, RasterGlyph glyph, float px, GameFont* game, Func<float, RasterGlyph> squeezed)
    {
        var g = this.Monospace(element, px, glyph, squeezed);
        return g with
        {
            Advance = g.Advance + this.GetLetterSpacing(element, px),
            Left = g.Left + this.ScalePixels(element.Def.HorizontalOffset, px),
            Top = g.Top + this.GetVerticalShift(element, px, game),
        };
    }

    /// <summary>Gets the letter spacing <see cref="Wrap"/> adds to an element's advances at a drawn size (0 for none).</summary>
    public int GetLetterSpacing(FaceElement? element, float px) => element is null ? 0 : this.ScalePixels(element.Def.LetterSpacing, px);

    /// <summary>
    /// Rasterizes a run of glyphs of a font face (one glyph, or a shaped cluster) for an element whose glyphs are shaped,
    /// or for the system's fonts (null): as <see cref="OutlineElementFont.RasterizeRun"/>, measured as the face measures.
    /// </summary>
    public RasterGlyph RasterizeRun(
        FaceElement? element,
        IDWriteFontFace* face,
        float size,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        float squeezeX = 1)
    {
        if (element?.Shaped is { } font)
            return font.RasterizeRun(face, size, glyphs, advances, offsets, count, originX, advance, squeezeX);
        var parameters = DirectWriteParams.Default with { MeasureMode = this.MeasureMode };
        return this.rasterizer.RasterizeRun(face, size, glyphs, advances, offsets, count, originX, advance, parameters, GlyphTransform.Identity.ScaledX(squeezeX));
    }

    /// <summary>
    /// Makes an element's font (xivres's fonts, as FontChanger's FaceElement::GetBaseFont makes them): by its renderer,
    /// with its gamma, and glyph merging around it. A font that isn't installed has no glyphs.
    /// </summary>
    private static IElementFont CreateFont(ElementDef def, GlyphRasterizer rasterizer, string faceName)
    {
        // FontChanger's gamma: coverage to the power of 1 / gamma.
        var gamma = def.Gamma != 1 && def.Gamma > 0 ? RasterGlyph.CoverageTable(1 / def.Gamma) : null;
        IElementFont font;
        switch (def.Renderer)
        {
            case ElementRenderer.Game:
                font = new GameElementFont();
                break;

            case ElementRenderer.DirectWrite or ElementRenderer.FreeType:
                font = (IElementFont?)OutlineElementFont.Create(rasterizer, def, def.Renderer, gamma, faceName) ?? new EmptyElementFont(0, 0, 0);
                break;

            case ElementRenderer.GlyphImages:
                if (rasterizer.Images is null)
                    Host.Log.Warning("{face}: glyph images can't be drawn without Direct2D", faceName);
                font = new ImageElementFont(GlyphImages.Load(def.GlyphImages ?? new(), rasterizer.Images), rasterizer.Images, def.Transform, gamma);
                break;

            default:
                font = new EmptyElementFont(def.Size, def.EmptyAscent, def.EmptyLineHeight);
                break;
        }

        if (def.GlyphMerging is not { } merging)
            return font;

        // Texts are drawn with the element's font; glyph images draw them with the font the lookup names, if any, else
        // with their own files.
        var images = font as ImageElementFont;
        var textOutline = images is not null && def.Lookup.Name.Length != 0
            ? OutlineElementFont.Create(rasterizer, def, ElementRenderer.DirectWrite, gamma, faceName)
            : font as OutlineElementFont;
        return new MergingElementFont(font, def, merging, TextFont, rasterizer, textOutline);

        // The element's transformation, then the texts', then the condensing.
        IMergeTextFont TextFont(float size, float condense)
        {
            var transform = new GlyphTransform(condense, 0, 0, 1).After(merging.TextTransform.After(def.Transform));
            return textOutline is not null
                ? new OutlineTextFont(textOutline, size, transform.After(textOutline.Font.Transform), rasterizer)
                : new ImageTextFont(images, size, transform);
        }
    }

    /// <summary>Gets the fonts of DirectWrite and FreeType in an element's font (glyph merging's texts' too).</summary>
    private static IEnumerable<OutlineElementFont> OutlineFonts(IElementFont font) => font switch
    {
        OutlineElementFont outline => [outline],
        MergingElementFont merging => merging.OutlineFonts,
        _ => [],
    };

    /// <summary>
    /// Gets how far an element's glyphs are moved down from the face's baseline at a drawn size: by the face's vertical
    /// alignment of the element's line box in the face's (xivres merged_fixed_size_font get_vertical_adjustment), and the
    /// element's baseline shift.
    /// </summary>
    private int GetVerticalShift(FaceElement element, float px, GameFont* game)
    {
        // The element's line box is placed in the face's: its top moved down by the adjustment.
        var (faceAscent, faceLineHeight) = this.GetLineMetrics(px, game);
        var (ascent, lineHeight) = this.GetElementMetrics(element, px, game);
        var adjustment = this.def.Alignment switch
        {
            FaceAlignment.Top => 0,
            FaceAlignment.Middle => (faceLineHeight - lineHeight) / 2,
            FaceAlignment.Bottom => faceLineHeight - lineHeight,
            FaceAlignment.RomanBaseline => (int)Rounding.Round(this.GetRomanBaselineY(px, game) - this.GetRomanBaselineY(element, px, game)),
            FaceAlignment.IdeographicCenter => (int)Rounding.Round(this.GetIdeographicCenterY(px, game) - this.GetIdeographicCenterY(element, px, game)),
            _ => faceAscent - ascent,
        };
        return ascent + adjustment - faceAscent + this.ScalePixels(element.Def.BaselineShift, px);
    }

    /// <summary>Gets how far below the line's top the roman baseline of the face is: its first element's.</summary>
    private float GetRomanBaselineY(float px, GameFont* game) =>
        this.GetLineMetrics(px, game).Ascent - (this.elements.Length == 0 ? null : this.GetBaseline(this.elements[0], RomanBaseline, px)).GetValueOrDefault();

    private float GetRomanBaselineY(FaceElement element, float px, GameFont* game) =>
        this.GetElementMetrics(element, px, game).Ascent - this.GetBaseline(element, RomanBaseline, px).GetValueOrDefault();

    /// <summary>
    /// Gets how far below the line's top the middle of the ideographic face (or em box) of the face is: its first
    /// element's, or the middle of the line.
    /// </summary>
    private float GetIdeographicCenterY(float px, GameFont* game)
    {
        var (ascent, lineHeight) = this.GetLineMetrics(px, game);
        return this.elements.Length == 0 ? lineHeight / 2f : this.GetIdeographicCenterY(this.elements[0], px, ascent, lineHeight);
    }

    private float GetIdeographicCenterY(FaceElement element, float px, GameFont* game)
    {
        var (ascent, lineHeight) = this.GetElementMetrics(element, px, game);
        return this.GetIdeographicCenterY(element, px, ascent, lineHeight);
    }

    private float GetIdeographicCenterY(FaceElement element, float px, int ascent, int lineHeight)
    {
        if (this.GetBaseline(element, IdeographicFaceBottom, px) is { } bottom && this.GetBaseline(element, IdeographicFaceTop, px) is { } top)
            return ascent - ((bottom + top) / 2);
        if (this.GetBaseline(element, IdeographicEmBoxBottom, px) is { } emBottom && this.GetBaseline(element, IdeographicEmBoxTop, px) is { } emTop)
            return ascent - ((emBottom + emTop) / 2);
        return lineHeight / 2f;
    }

    private float? GetBaseline(FaceElement element, uint tag, float px) => element.Font.GetBaseline(tag, this.GetElementPx(element, px));

    private (int Ascent, int LineHeight) GetElementMetrics(FaceElement element, float px, GameFont* game) =>
        element.Font.GetLineMetrics(this.GetElementPx(element, px), game);

    /// <summary>
    /// Places a glyph of an element (its advance on screen, unadjusted) in a cell as the element's monospacing says: the
    /// advance clamped into its limits, the ink aligned in the cell, and ink wider than a limited cell drawn narrower with
    /// <paramref name="squeezed"/> (given the horizontal scale). Glyphs that don't advance stay as they are.
    /// </summary>
    private RasterGlyph Monospace(FaceElement element, float px, RasterGlyph glyph, Func<float, RasterGlyph> squeezed)
    {
        var def = element.Def;
        if (def.Monospacing is not { } mono || glyph.Advance <= 0)
            return glyph;

        var unit = mono.Unit switch
        {
            MonospacingUnit.Pixels => px / this.ReferenceSize,
            MonospacingUnit.Em => this.GetElementPx(element, px),
            _ => this.GetReferenceAdvance(element, px, mono.ReferenceCharacter),
        };
        if (unit is not > 0)
            return glyph;

        var min = mono.Min is { } a ? Math.Max(0, (int)Rounding.Round(a * unit)) : 0;
        var max = mono.Max is { } b ? Math.Max(0, (int)Rounding.Round(b * unit)) : int.MaxValue;
        min = Math.Min(min, max);
        var cell = Math.Clamp(glyph.Advance, min, max);
        if (glyph.Width == 0)
            return glyph with { Advance = cell };

        int left;
        if (mono.Max is not null && cell > 0 && glyph.Width > cell)
        {
            // Hinting and rounding make the ink only roughly as narrow as the scale says: narrower scales are tried until
            // it fits.
            var scale = (float)cell / glyph.Width;
            var fitted = glyph;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                fitted = squeezed(scale);
                if (fitted.Width <= cell)
                    break;
                scale *= (float)cell / fitted.Width * 0.98f;
            }

            left = (cell - fitted.Width) >> 1;
            glyph = fitted;
        }
        else
        {
            left = mono.Alignment switch
            {
                MonospacingAlignment.Left => glyph.Left,
                MonospacingAlignment.CenterInk => (cell - glyph.Width) >> 1,
                MonospacingAlignment.Right => glyph.Left + cell - glyph.Advance,
                _ => glyph.Left + ((cell - glyph.Advance) >> 1),
            };
        }

        // The horizontal offset (added after) nudges the ink in its cell, but not left of the pen.
        var offset = this.ScalePixels(def.HorizontalOffset, px);
        return glyph with { Advance = cell, Left = Math.Max(0, left + offset) - offset };
    }

    /// <summary>Gets the advance of an element's monospacing reference character at a drawn size (kept by size).</summary>
    private float GetReferenceAdvance(FaceElement element, float px, int codepoint)
    {
        if (!this.referenceAdvances.TryGetValue((element, px), out var advance))
        {
            advance = element.Font.GetAdvance(codepoint, this.GetElementPx(element, px));
            this.referenceAdvances.Add((element, px), advance);
        }

        return advance;
    }
}
