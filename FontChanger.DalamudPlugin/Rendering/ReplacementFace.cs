using System;
using System.Collections.Generic;
using System.Linq;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace CustomFonts;

/// <summary>
/// A face of a preset made usable: its elements' fonts found, which element draws each codepoint, and the metrics and
/// adjustments of each at a drawn size.
/// </summary>
/// <remarks>
/// <para>Sizes in a face are relative to its first element: at a drawn size <c>px</c>, an element draws at
/// <c>px * element.Size / first.Size</c>, and pixel values in the face (letter spacing, offsets, Empty metrics) scale by
/// <c>px / first.Size</c>. The first element gives the line metrics: the game font's (Game), its own (Empty), or its
/// font's (DirectWrite).</para>
/// <para>A codepoint goes to the elements in order (xivres merged_fixed_size_font): an element has it if it is in its
/// ranges and its font has it; AddNew takes it if no element has yet, AddAll always, Replace only from an earlier
/// element. Characters no element has are the game's, or with <see cref="SystemFallback"/>, the system fonts': in
/// shaped text DirectWrite's fallback, glyph by glyph the built-in face's.</para>
/// <para>An element with glyph merging has only its merged codepoints, and an element of glyph images only those it has
/// files for; both are drawn glyph by glyph, not shaped.</para>
/// </remarks>
internal sealed unsafe class ReplacementFace : IDisposable
{
    /// <summary>The element index for a codepoint the game's glyph is used for.</summary>
    public const int GameElement = -2;

    /// <summary>The element index for a codepoint no element has, left to the system's fonts.</summary>
    public const int NoElement = -1;

    // The built-in face: Segoe UI, the game's icon font, then fonts for scripts Segoe UI lacks.
    private static readonly string[] BuiltInFamilies =
    [
        "Segoe UI", TextShaper.IconFamily, "Yu Gothic UI", "Malgun Gothic", "Microsoft YaHei UI", "Microsoft JhengHei UI",
        "Nirmala UI", "Leelawadee UI", "Segoe UI Symbol", "Segoe UI Emoji",
    ];

    private static int nextId;

    private readonly GlyphRasterizer rasterizer;
    private readonly FaceDef def;
    private readonly Element[] elements;
    private readonly ReplacementFace? fallback;
    private readonly Dictionary<(nint Game, int Codepoint), int> elementCache = [];

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
        this.elements = new Element[def.Elements.Count];
        try
        {
            for (var i = 0; i < this.elements.Length; i++)
                this.elements[i] = new(def.Elements[i], rasterizer, def.Name);
        }
        catch
        {
            this.Dispose();
            throw;
        }

        this.ReferenceSize = def.Elements.Count != 0 && def.Elements[0].Size > 0 ? def.Elements[0].Size : 1;
        this.PrimaryElement = Array.FindIndex(this.elements, e => e.IsShaped);
        this.MeasureMode = this.PrimaryElement >= 0 ? def.Elements[this.PrimaryElement].DirectWrite.MeasureMode : 0;
    }

    /// <summary>Gets a number telling this face apart from others, for keys of cached glyphs.</summary>
    public int Id { get; } = System.Threading.Interlocked.Increment(ref nextId);

    public string Name => this.def.Name;

    /// <summary>Gets whether characters no element has are drawn with system fonts instead of the game's glyphs.</summary>
    public bool SystemFallback { get; }

    /// <summary>Gets the size the face's sizes are relative to: its first element's.</summary>
    public float ReferenceSize { get; }

    /// <summary>Gets the first element with a font it is drawn with (the font text is laid out in by default), or -1.</summary>
    public int PrimaryElement { get; }

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

    public void Dispose()
    {
        foreach (var e in this.elements)
            e?.Dispose();
    }

    public ElementDef GetDef(int element) => this.elements[element].Def;

    /// <summary>Gets the size an element draws at for a drawn size.</summary>
    public float GetElementPx(int element, float px) => px * this.elements[element].Def.Size / this.ReferenceSize;

    /// <summary>Gets a pixel value of the face (at its reference size) at a drawn size, rounded.</summary>
    public int ScalePixels(float value, float px) => (int)MathF.Round(value * px / this.ReferenceSize);

    /// <summary>Gets the element that draws a codepoint: an index, <see cref="GameElement"/>, or <see cref="NoElement"/>.</summary>
    public int GetElement(int codepoint, GameFont* game)
    {
        if (this.elementCache.TryGetValue(((nint)game, codepoint), out var cached))
            return cached;

        var assigned = NoElement;
        for (var i = 0; i < this.elements.Length; i++)
        {
            // Asked only of elements that would take it.
            var mode = this.elements[i].Def.MergeMode;
            if ((mode == MergeMode.AddNew && assigned != NoElement) || (mode == MergeMode.Replace && assigned == NoElement))
                continue;
            if (this.Has(i, codepoint, game))
                assigned = i;
        }

        if (assigned >= 0 ? this.elements[assigned].Def.Renderer == ElementRenderer.Game : !this.SystemFallback)
            assigned = GameElement;
        this.elementCache[((nint)game, codepoint)] = assigned;
        return assigned;
    }

    /// <summary>
    /// Gets whether an element's glyphs are shaped with DirectWrite: those of its font, but not merged glyphs or glyph
    /// images, which are drawn glyph by glyph.
    /// </summary>
    public bool IsShaped(int element) => element < 0 || this.elements[element].IsShaped;

    /// <summary>Gets the codepoint whose glyph an element draws for a codepoint (its replacements).</summary>
    public int GetDrawnCodepoint(int element, int codepoint) =>
        element >= 0 && this.elements[element].Def.Replacements.TryGetValue(codepoint, out var to) ? to : codepoint;

    /// <summary>Gets the line metrics at a drawn size: from the first element, rounded to whole pixels.</summary>
    public (int Ascent, int LineHeight) GetLineMetrics(float px, GameFont* game)
    {
        var (ascent, lineHeight) = this.elements.Length == 0 ? (0, 0) : this.GetElementMetrics(0, px, game);
        if (lineHeight <= 0)
        {
            // Nothing to take them from: proportions of Segoe UI.
            ascent = (int)MathF.Ceiling(px * 1.08f);
            lineHeight = (int)MathF.Ceiling(px * 1.33f);
        }

        return (ascent, lineHeight);
    }

    /// <summary>
    /// Rasterizes a codepoint at a drawn size, unadjusted (see <see cref="Adjust"/>); false if the game's glyph is to be
    /// used. <paramref name="element"/> is the element that drew it, or <see cref="NoElement"/> for a system font.
    /// </summary>
    public bool TryRasterize(int codepoint, float px, GameFont* game, out RasterGlyph glyph, out int element)
    {
        glyph = default;
        element = this.GetElement(codepoint, game);
        if (element >= 0)
            return this.TryRasterizeGlyph(element, this.GetDrawnCodepoint(element, codepoint), px, game, out glyph);
        if (element == GameElement || this.fallback is null)
            return false;

        // At the size the face's text is laid out in.
        var size = this.PrimaryElement >= 0 ? this.GetElementPx(this.PrimaryElement, px) : px;
        return this.fallback.TryRasterize(codepoint, size, null, out glyph, out _);
    }

    /// <summary>
    /// Adjusts a glyph of an element at a drawn size: its gamma, letter spacing (to the advance), horizontal offset (the
    /// ink moves, not the pen), and vertical alignment in the face and baseline shift.
    /// </summary>
    public RasterGlyph Adjust(int element, RasterGlyph glyph, float px, GameFont* game)
    {
        var e = this.elements[element];
        if (e.GammaTable is not null && !glyph.GammaApplied)
            glyph = glyph.WithCoverage(e.GammaTable);
        return glyph with
        {
            Advance = glyph.Advance + this.ScalePixels(e.Def.LetterSpacing, px),
            Left = glyph.Left + this.ScalePixels(e.Def.HorizontalOffset, px),
            Top = glyph.Top + this.GetVerticalShift(element, px, game),
        };
    }

    /// <summary>
    /// Sets an element's font, size, language, features and axis values on a range of a text layout, asking for what
    /// matches the element's font with its simulations.
    /// </summary>
    public void ApplyElement(IDWriteTextLayout* layout, int element, DWRITE_TEXT_RANGE range, float emSize)
    {
        var e = this.elements[element];
        var lookup = e.Def.Lookup;
        var font = e.Font;
        fixed (char* family = lookup.Name)
            layout->SetFontFamilyName(family, range).ThrowOnError();
        layout->SetFontWeight((DWRITE_FONT_WEIGHT)(font?.LayoutWeight ?? lookup.Weight), range).ThrowOnError();
        layout->SetFontStretch((DWRITE_FONT_STRETCH)(font?.LayoutStretch ?? lookup.Stretch), range).ThrowOnError();
        layout->SetFontStyle((DWRITE_FONT_STYLE)(font?.LayoutStyle ?? lookup.Style), range).ThrowOnError();
        layout->SetFontSize(emSize, range).ThrowOnError();
        if (lookup.Language.Length != 0)
        {
            fixed (char* language = lookup.Language)
                layout->SetLocaleName(language, range).ThrowOnError();
        }

        if (e.GetTypography(this.rasterizer.Factory) is var typography && typography is not null)
            layout->SetTypography(typography, range).ThrowOnError();

        // A variable font's axes; the optical size follows the size unless set.
        if (font is not null && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            IDWriteTextLayout4* layout4;
            var iid = IID.IID_IDWriteTextLayout4;
            if (((IUnknown*)layout)->QueryInterface(&iid, (void**)&layout4).SUCCEEDED)
            {
                var axes = font.LayoutAxes;
                if (!axes.IsEmpty)
                {
                    fixed (DWRITE_FONT_AXIS_VALUE* a = axes)
                        layout4->SetFontAxisValues(a, (uint)axes.Length, range).ThrowOnError();
                }

                if (font.AutoOpticalSize)
                    layout4->SetAutomaticFontAxes(DWRITE_AUTOMATIC_FONT_AXES.DWRITE_AUTOMATIC_FONT_AXES_OPTICAL_SIZE).ThrowOnError();
                layout4->Release();
            }
        }
    }

    /// <summary>
    /// Gets the face an element's glyph run is drawn from: the element's own (with its simulations and axis values at the
    /// size) if the layout picked its font, else the layout's.
    /// </summary>
    public IDWriteFontFace* GetRunFace(int element, IDWriteFontFace* runFace, float emSize)
    {
        if (element < 0 || this.elements[element].Font is not { } font)
            return runFace;
        var own = font.GetFace(emSize);
        return own->GetIndex() == runFace->GetIndex() && own->GetGlyphCount() == runFace->GetGlyphCount() ? own : runFace;
    }

    /// <summary>Gets how much further an element's glyphs advance than DirectWrite lays them out: FreeType's emboldening.</summary>
    public float GetExtraAdvance(int element, float emSize) =>
        element >= 0 && this.elements[element] is { FreeTypeFace: not 0, Font: { } font } ? font.Embolden * emSize : 0;

    /// <summary>
    /// Gets how far an element's glyphs are moved down from the face's baseline at a drawn size: by the face's vertical
    /// alignment of the element's line box in the face's, and the element's baseline shift.
    /// </summary>
    private int GetVerticalShift(int element, float px, GameFont* game)
    {
        var (faceAscent, faceLineHeight) = this.GetLineMetrics(px, game);
        var (ascent, lineHeight) = this.GetElementMetrics(element, px, game);
        var shift = this.def.Alignment switch
        {
            FaceAlignment.Top => ascent - faceAscent,
            FaceAlignment.Middle or FaceAlignment.IdeographicCenter => ((faceLineHeight - lineHeight) / 2) + ascent - faceAscent,
            FaceAlignment.Bottom => faceLineHeight - lineHeight + ascent - faceAscent,
            _ => 0,
        };
        return shift + this.ScalePixels(this.elements[element].Def.BaselineShift, px);
    }

    private (int Ascent, int LineHeight) GetElementMetrics(int element, float px, GameFont* game)
    {
        var e = this.elements[element];
        switch (e.Def.Renderer)
        {
            case ElementRenderer.Game when game is not null && game->Size > 0:
            {
                var scale = this.GetElementPx(element, px) / game->Size;
                return ((int)MathF.Round(game->Ascent * scale), (int)MathF.Round(game->LineHeight * scale));
            }

            case ElementRenderer.Empty:
                return (this.ScalePixels(e.Def.EmptyAscent, px), this.ScalePixels(e.Def.EmptyLineHeight, px));

            case ElementRenderer.GlyphImages:
                return e.Images?.GetLineMetrics(this.GetElementPx(element, px), e.Transform) ?? (0, 0);

            case ElementRenderer.DirectWrite or ElementRenderer.FreeType when e.Font is not null:
                return GetFontMetrics(e.Font, this.GetElementPx(element, px), e.Transform);

            default:
                return (0, 0);
        }
    }

    /// <summary>Gets a font's line metrics at a size, scaled vertically as its glyphs are.</summary>
    private static (int Ascent, int LineHeight) GetFontMetrics(ElementFont font, float px, GlyphTransform transform)
    {
        var m = font.Metrics;
        var size = px / m.designUnitsPerEm * MathF.Abs(transform.M22);
        var ascent = (int)MathF.Ceiling(m.ascent * size);
        var descent = (int)MathF.Ceiling(m.descent * size);
        return (ascent, ascent + descent + (int)MathF.Round(m.lineGap * size));
    }

    private bool Has(int element, int codepoint, GameFont* game)
    {
        var e = this.elements[element];
        if (!e.Def.Contains(codepoint))
            return false;
        if (e.Def.GlyphMerging is { } merging)
            return merging.Glyphs.ContainsKey(codepoint);
        return e.Def.Renderer switch
        {
            ElementRenderer.Game => game is not null && game->HasGlyph(codepoint),
            ElementRenderer.DirectWrite or ElementRenderer.FreeType => e.HasCharacter(codepoint),
            ElementRenderer.GlyphImages => e.Images?.Has(codepoint) ?? false,
            _ => false,
        };
    }

    /// <summary>
    /// Rasterizes a run of glyphs of a font face (one glyph, or a shaped cluster) for an element (or
    /// <see cref="NoElement"/>, a system font): with FreeType if the element is drawn so or its font has only bitmaps,
    /// else with DirectWrite, measured as the face measures.
    /// </summary>
    /// <remarks>
    /// The glyphs are transformed as the element says (or by <paramref name="transform"/>), then squeezed horizontally
    /// by <paramref name="squeezeX"/>; <paramref name="originX"/> is on screen, after the transformation, and
    /// <paramref name="advance"/> is the advance the glyph is given.
    /// </remarks>
    public RasterGlyph RasterizeRun(
        int element,
        IDWriteFontFace* face,
        float size,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        float squeezeX = 1,
        GlyphTransform? transform = null)
    {
        var e = element >= 0 ? this.elements[element] : null;
        var t = (transform ?? e?.Transform ?? GlyphTransform.Identity).ScaledX(squeezeX);
        if (e is { FreeTypeFace: not 0, Font: { } font })
        {
            return this.rasterizer.FreeType!.RasterizeRun(
                e.FreeTypeFace, size, glyphs, advances, offsets, count, originX, advance, e.Def.FreeType, t, font.GetAxisValues(size), font.Embolden);
        }

        var parameters = (e?.Def.DirectWrite ?? DirectWriteParams.Default) with { MeasureMode = this.MeasureMode };
        return this.rasterizer.RasterizeRun(face, size, glyphs, advances, offsets, count, originX, advance, parameters, t);
    }

    /// <summary>Gets how far an element's transformation scales advances: 1 for anything but an element.</summary>
    public float GetAdvanceScale(int element) => element >= 0 ? this.elements[element].Transform.M11 : 1;

    /// <summary>Gets the OpenType features an element turns on or off; null if it sets none. Owned by the face.</summary>
    public IDWriteTypography* GetTypography(int element) => this.elements[element].GetTypography(this.rasterizer.Factory);

    /// <summary>
    /// Places a glyph of an element (its advance on screen, unadjusted) in a cell as the element's monospacing says: the
    /// advance clamped into its limits, the ink aligned in the cell, and ink wider than a limited cell drawn narrower with
    /// <paramref name="squeezed"/> (given the horizontal scale). Glyphs that don't advance stay as they are.
    /// </summary>
    public RasterGlyph Monospace(int element, float px, RasterGlyph glyph, Func<float, RasterGlyph> squeezed)
    {
        var def = element >= 0 ? this.elements[element].Def : null;
        if (def?.Monospacing is not { } mono || glyph.Advance <= 0)
            return glyph;

        var unit = mono.Unit switch
        {
            MonospacingUnit.Pixels => px / this.ReferenceSize,
            MonospacingUnit.Em => this.GetElementPx(element, px),
            _ => this.GetReferenceAdvance(element, px, mono.ReferenceCharacter),
        };
        if (unit is not > 0)
            return glyph;

        var min = mono.Min is { } a ? Math.Max(0, (int)MathF.Round(a * unit)) : 0;
        var max = mono.Max is { } b ? Math.Max(0, (int)MathF.Round(b * unit)) : int.MaxValue;
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

        // The horizontal offset (added when the glyph is adjusted) nudges the ink in its cell, but not left of the pen.
        var offset = this.ScalePixels(def.HorizontalOffset, px);
        return glyph with { Advance = cell, Left = Math.Max(0, left + offset) - offset };
    }

    private float GetReferenceAdvance(int element, float px, int codepoint)
    {
        var e = this.elements[element];
        var size = this.GetElementPx(element, px);
        if (e.Def.Renderer == ElementRenderer.GlyphImages)
        {
            return e.Images is not null && this.rasterizer.Images is not null && e.Images.Rasterize(this.rasterizer.Images, codepoint, size, e.Transform) is { } g
                ? g.Advance
                : 0;
        }

        if (e.Font is null)
            return 0;
        var face = e.Font.GetFace(size);
        var cp = (uint)codepoint;
        ushort index;
        if (face->GetGlyphIndices(&cp, 1, &index).FAILED || index == 0)
            return 0;
        DWRITE_GLYPH_METRICS gm;
        face->GetDesignGlyphMetrics(&index, 1, &gm, BOOL.FALSE).ThrowOnError();
        return ((gm.advanceWidth * size / e.Font.Metrics.designUnitsPerEm) + this.GetExtraAdvance(element, size)) * e.Transform.M11;
    }

    private bool TryRasterizeGlyph(int element, int codepoint, float px, GameFont* game, out RasterGlyph glyph)
    {
        glyph = default;
        var e = this.elements[element];
        RasterGlyph? drawn;
        if (e.Def.GlyphMerging is { } merging)
            drawn = this.RasterizeMerged(element, merging, codepoint, px, game);
        else if (e.Def.Renderer == ElementRenderer.GlyphImages)
            drawn = e.Images is not null && this.rasterizer.Images is not null ? e.Images.Rasterize(this.rasterizer.Images, codepoint, this.GetElementPx(element, px), e.Transform) : null;
        else
            return this.TryRasterizeFontGlyph(element, codepoint, px, true, out glyph);

        if (drawn is not { } g)
            return false;

        // Glyphs that aren't drawn from a font are resampled narrower.
        glyph = this.Monospace(element, px, g, scale => Squeeze(g, scale));
        return true;

        // Scales a glyph horizontally about the pen, each pixel averaging the source columns it covers.
        static RasterGlyph Squeeze(RasterGlyph g, float scale)
        {
            var x0 = (int)MathF.Floor(g.Left * scale);
            var w = Math.Max(1, (int)MathF.Ceiling((g.Left + g.Width) * scale) - x0);
            var alpha = new byte[w * g.Height];
            for (var y = 0; y < g.Height; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var u0 = ((x0 + x) / scale) - g.Left;
                    var u1 = ((x0 + x + 1) / scale) - g.Left;
                    var sum = 0f;
                    for (var u = (int)MathF.Floor(u0); u < MathF.Ceiling(u1); u++)
                    {
                        if (u < 0 || u >= g.Width)
                            continue;
                        var cover = Math.Min(u + 1, u1) - Math.Max(u, u0);
                        sum += g.Alpha[(y * g.Width) + u] * cover;
                    }

                    alpha[(y * w) + x] = (byte)Math.Clamp(MathF.Round(sum * scale), 0, 255);
                }
            }

            return g with { Left = x0, Width = w, Alpha = alpha };
        }
    }

    /// <summary>Rasterizes a glyph of an element's font at a drawn size, placed in its monospaced cell if asked.</summary>
    private bool TryRasterizeFontGlyph(int element, int codepoint, float px, bool monospace, out RasterGlyph glyph)
    {
        glyph = default;
        var e = this.elements[element];
        if (e.Font is null)
            return false;

        var size = this.GetElementPx(element, px);
        var face = e.Font.GetFace(size);
        var cp = (uint)codepoint;
        ushort index;
        if (face->GetGlyphIndices(&cp, 1, &index).FAILED || index == 0)
            return false;

        DWRITE_GLYPH_METRICS gm;
        face->GetDesignGlyphMetrics(&index, 1, &gm, BOOL.FALSE).ThrowOnError();
        var advance = (gm.advanceWidth * size / e.Font.Metrics.designUnitsPerEm) + this.GetExtraAdvance(element, size);
        var onScreen = (int)MathF.Round(advance * e.Transform.M11);
        glyph = this.RasterizeRun(element, face, size, &index, &advance, null, 1, 0, onScreen);
        if (!monospace)
            return true;

        var (glyphIndex, glyphAdvance) = (index, advance);
        glyph = this.Monospace(element, px, glyph, scale =>
        {
            var (i, a) = (glyphIndex, glyphAdvance);
            return this.RasterizeRun(element, face, size, &i, &a, null, 1, 0, onScreen, scale);
        });
        return true;
    }

    /// <summary>Draws a merged glyph of an element at a drawn size (null if it can't be), unadjusted.</summary>
    private RasterGlyph? RasterizeMerged(int element, GlyphMergingDef merging, int codepoint, float px, GameFont* game)
    {
        if (!merging.Glyphs.TryGetValue(codepoint, out var glyph))
            return null;

        var e = this.elements[element];
        var size = this.GetElementPx(element, px);
        RasterGlyph? baseGlyph = null;
        if (glyph.Mapping.Shape == MergeShape.Glyph)
        {
            if (e.Def.Renderer == ElementRenderer.GlyphImages)
                baseGlyph = e.Images is not null && this.rasterizer.Images is not null ? e.Images.Rasterize(this.rasterizer.Images, codepoint, size, e.Transform) : null;
            else if (this.TryRasterizeFontGlyph(element, codepoint, px, false, out var g))
                baseGlyph = g;
        }

        return GlyphMerger.Draw(
            glyph.Mapping,
            glyph.Text,
            merging,
            px / this.ReferenceSize,
            size,
            e.Def.Size,
            this.GetElementMetrics(element, px, game).Ascent,
            (textSize, condense) => this.CreateMergeTextFont(element, merging, textSize, condense),
            baseGlyph,
            e.GammaTable,
            this.rasterizer.Images,
            this.rasterizer.FreeType);
    }

    /// <summary>
    /// Makes the font a merged glyph's text is drawn with: the element's at a size, transformed by the element, the
    /// text's transformation and the condensing. Glyph images draw texts with the font the lookup names, if any.
    /// </summary>
    private IMergeTextFont CreateMergeTextFont(int element, GlyphMergingDef merging, float size, float condense)
    {
        var e = this.elements[element];
        var transform = new GlyphTransform(condense, 0, 0, 1).After(merging.TextTransform.After(e.Def.Transform));
        if (e.Font is { } font)
            return new FontTextFont(this, element, font, size, transform.After(font.Transform));
        return new ImageTextFont(this.rasterizer.Images, e.Images, size, transform);
    }

    /// <summary>The glyph run of a laid out line: its face, size, and glyphs.</summary>
    private sealed record TextRun(nint Face, float EmSize, ushort[] Glyphs, float[] Advances, DWRITE_GLYPH_OFFSET[]? Offsets);

    /// <summary>Texts of glyph merging in an element's font, shaped with its features.</summary>
    private sealed class FontTextFont(ReplacementFace face, int element, ElementFont font, float size, GlyphTransform transform) : IMergeTextFont, IGlyphRunSink
    {
        private readonly List<TextRun> runs = [];

        public int Ascent { get; } = GetFontMetrics(font, size, transform).Ascent;

        public bool Has(int codepoint)
        {
            BOOL exists;
            return font.Font->HasCharacter((uint)codepoint, &exists).SUCCEEDED && exists;
        }

        public MergeTextLine LayOut(string text, int letterSpacing)
        {
            this.runs.Clear();
            if (text.Length != 0)
                this.Shape(text, transform.M11 != 0 ? letterSpacing / transform.M11 : 0);

            // Letter spacing goes between glyphs.
            var runs = this.runs.ToArray();
            var extra = face.GetExtraAdvance(element, size);
            var width = runs.Sum(r => r.Advances.Sum() + (extra * r.Glyphs.Length)) - (text.Length != 0 && transform.M11 != 0 ? letterSpacing / transform.M11 : 0);
            var advance = (int)MathF.Round(width * transform.M11);
            return new(advance, true, x =>
            {
                var pieces = new List<RasterGlyph>();
                var pen = x;
                foreach (var r in runs)
                {
                    var advances = r.Advances.Select(a => a + extra).ToArray();
                    fixed (ushort* g = r.Glyphs)
                    fixed (float* a = advances)
                    fixed (DWRITE_GLYPH_OFFSET* o = r.Offsets)
                    {
                        var runFace = face.GetRunFace(element, (IDWriteFontFace*)r.Face, r.EmSize);
                        pieces.Add(face.RasterizeRun(element, runFace, r.EmSize, g, a, o, (uint)r.Glyphs.Length, pen, 0, 1, transform));
                    }

                    pen += advances.Sum() * transform.M11;
                }

                return Merge(pieces, advance);
            });
        }

        void IGlyphRunSink.OnGlyphRun(float baselineX, DWRITE_GLYPH_RUN* run, DWRITE_GLYPH_RUN_DESCRIPTION* description)
        {
            // Glyphs the font lacks are left out.
            var glyphs = new List<ushort>();
            var advances = new List<float>();
            var offsets = run->glyphOffsets is null ? null : new List<DWRITE_GLYPH_OFFSET>();
            for (var i = 0; i < run->glyphCount; i++)
            {
                if (run->glyphIndices[i] == 0)
                {
                    if (advances.Count != 0)
                        advances[^1] += run->glyphAdvances[i];
                    continue;
                }

                glyphs.Add(run->glyphIndices[i]);
                advances.Add(run->glyphAdvances[i]);
                offsets?.Add(run->glyphOffsets[i]);
            }

            if (glyphs.Count != 0)
                this.runs.Add(new((nint)run->fontFace, run->fontEmSize, glyphs.ToArray(), advances.ToArray(), offsets?.ToArray()));
        }

        private void Shape(string text, float letterSpacing)
        {
            var factory = face.rasterizer.Factory;
            IDWriteTextFormat* format = null;
            IDWriteTextFormat1* format1 = null;
            IDWriteTextLayout* layout = null;
            try
            {
                fixed (char* name = face.elements[element].Def.Lookup.Name)
                fixed (char* locale = "en-us")
                {
                    factory->CreateTextFormat(
                        name,
                        null,
                        (DWRITE_FONT_WEIGHT)font.LayoutWeight,
                        (DWRITE_FONT_STYLE)font.LayoutStyle,
                        (DWRITE_FONT_STRETCH)font.LayoutStretch,
                        size,
                        locale,
                        &format).ThrowOnError();
                }

                format->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP).ThrowOnError();
                var iid = IID.IID_IDWriteTextFormat1;
                ((IUnknown*)format)->QueryInterface(&iid, (void**)&format1).ThrowOnError();
                format1->SetFontFallback(face.rasterizer.NoFallback).ThrowOnError();
                fixed (char* t = text)
                    factory->CreateTextLayout(t, (uint)text.Length, format, 1e6f, 1e6f, &layout).ThrowOnError();

                var range = new DWRITE_TEXT_RANGE { startPosition = 0, length = (uint)text.Length };
                face.ApplyElement(layout, element, range, size);
                if (letterSpacing != 0)
                {
                    IDWriteTextLayout1* layout1;
                    iid = IID.IID_IDWriteTextLayout1;
                    if (((IUnknown*)layout)->QueryInterface(&iid, (void**)&layout1).SUCCEEDED)
                    {
                        layout1->SetCharacterSpacing(0, letterSpacing, 0, range);
                        layout1->Release();
                    }
                }

                face.rasterizer.RunCollector.Collect(layout, this);
            }
            finally
            {
                if (layout is not null)
                    layout->Release();
                if (format1 is not null)
                    format1->Release();
                if (format is not null)
                    format->Release();
            }
        }
    }

    /// <summary>Texts of glyph merging in an element's glyph images, glyph by glyph with font.json's kerning.</summary>
    private sealed class ImageTextFont(ImageRenderer? renderer, GlyphImages? images, float size, GlyphTransform transform) : IMergeTextFont
    {
        public int Ascent { get; } = images?.GetLineMetrics(size, transform).Ascent ?? 0;

        public bool Has(int codepoint) => renderer is not null && images is not null && images.Has(codepoint);

        public MergeTextLine LayOut(string text, int letterSpacing)
        {
            var pieces = new List<RasterGlyph>();
            var codepoints = text.EnumerateRunes().Select(r => r.Value).ToArray();
            var pen = 0;
            for (var i = 0; i < codepoints.Length; i++)
            {
                if (images!.Rasterize(renderer!, codepoints[i], size, transform) is not { } g)
                    continue;
                pieces.Add(g with { Left = g.Left + pen });
                pen += i + 1 < codepoints.Length ? g.Advance + images.GetKerning(codepoints[i], codepoints[i + 1], size, transform) + letterSpacing : g.Advance;
            }

            var merged = Merge(pieces, pen);
            return new(pen, false, x => merged with { Left = merged.Left + (int)MathF.Round(x) });
        }
    }

    /// <summary>Merges glyphs placed relative to one pen into one box, keeping the larger coverage where they overlap.</summary>
    private static RasterGlyph Merge(List<RasterGlyph> pieces, int advance)
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
        var alpha = new byte[w * (bottom - top)];
        foreach (var p in pieces)
        {
            for (var y = 0; y < p.Height; y++)
            {
                for (var x = 0; x < p.Width; x++)
                {
                    ref var d = ref alpha[((p.Top - top + y) * w) + (p.Left - left + x)];
                    d = Math.Max(d, p.Alpha[(y * p.Width) + x]);
                }
            }
        }

        return new(advance, left, top, w, bottom - top, alpha);
    }

    /// <summary>
    /// An element: its font (and FreeType's, if it draws with it), glyph images, gamma as a coverage table (null for 1),
    /// and OpenType features.
    /// </summary>
    private sealed class Element : IDisposable
    {
        private nint typography;
        private bool typographyMade;

        public Element(ElementDef def, GlyphRasterizer rasterizer, string faceName)
        {
            this.Def = def;

            // FontChanger's gamma: coverage to the power of 1 / gamma.
            if (def.Gamma != 1 && def.Gamma > 0)
                this.GammaTable = RasterGlyph.CoverageTable(1 / def.Gamma);

            if (def.Renderer == ElementRenderer.GlyphImages)
            {
                if (rasterizer.Images is null)
                    Plugin.Log.Warning("{face}: glyph images can't be drawn without Direct2D", faceName);
                this.Images = GlyphImages.Load(def.GlyphImages ?? new(), rasterizer.Images);
            }

            // Glyph images draw the texts of glyph merging with the font the lookup names, if any.
            var fontRenderer = def.Renderer == ElementRenderer.GlyphImages ? ElementRenderer.DirectWrite : def.Renderer;
            var wantsFont = def.Renderer is ElementRenderer.DirectWrite or ElementRenderer.FreeType ||
                            (def.Renderer == ElementRenderer.GlyphImages && def.GlyphMerging is not null && def.Lookup.Name.Length != 0);
            if (!wantsFont)
                return;

            this.Font = ElementFont.Create(rasterizer, def.Lookup, fontRenderer);
            if (this.Font is null)
            {
                Plugin.Log.Warning("{face}: font {family} not found", faceName, def.Lookup.Name);
                return;
            }

            // FreeType draws what the preset says it does, and fonts with only bitmaps, which DirectWrite doesn't draw.
            var ftFace = rasterizer.FreeType?.Open(this.Font.Face) ?? 0;
            if (ftFace != 0 && (def.Renderer == ElementRenderer.FreeType || !FreeTypeFonts.IsScalable(ftFace)))
                this.FreeTypeFace = ftFace;
            else if (def.Renderer == ElementRenderer.FreeType)
                Plugin.Log.Warning("{family} can't be opened with FreeType; it is drawn with DirectWrite", def.Lookup.Name);
        }

        public ElementDef Def { get; }

        /// <summary>Gets the element's font, if it is drawn with one (or draws merged texts with one).</summary>
        public ElementFont? Font { get; private set; }

        public GlyphImages? Images { get; }

        /// <summary>Gets whether the element's glyphs are drawn from its font (and shaped), not merged or from images.</summary>
        public bool IsShaped => this is { Font: not null, Def: { Renderer: ElementRenderer.DirectWrite or ElementRenderer.FreeType, GlyphMerging: null } };

        /// <summary>Gets how the element's glyphs are transformed on screen: by synthesis, then by the element.</summary>
        public GlyphTransform Transform => this.Font is null || this.Def.Renderer == ElementRenderer.GlyphImages
            ? this.Def.Transform
            : this.Def.Transform.After(this.Font.Transform);

        /// <summary>The FreeType face the element is drawn with (owned by <see cref="FreeTypeFonts"/>), or 0 for DirectWrite.</summary>
        public nint FreeTypeFace { get; }

        public byte[]? GammaTable { get; }

        public bool HasCharacter(int codepoint)
        {
            BOOL exists;
            return this.Font is not null && this.Font.Font->HasCharacter((uint)codepoint, &exists).SUCCEEDED && exists;
        }

        /// <summary>Gets the element's OpenType features as a typography; null if it sets none.</summary>
        public IDWriteTypography* GetTypography(IDWriteFactory2* factory)
        {
            if (this.typographyMade)
                return (IDWriteTypography*)this.typography;

            this.typographyMade = true;
            var features = this.Def.Lookup.Features;
            if (features.Count == 0)
                return null;
            IDWriteTypography* t;
            factory->CreateTypography(&t).ThrowOnError();
            this.typography = (nint)t;
            foreach (var (tag, value) in features)
                t->AddFontFeature(new DWRITE_FONT_FEATURE { nameTag = (DWRITE_FONT_FEATURE_TAG)tag, parameter = value }).ThrowOnError();
            return t;
        }

        public void Dispose()
        {
            if (this.typography != 0)
            {
                ((IUnknown*)this.typography)->Release();
                this.typography = 0;
            }

            this.Font?.Dispose();
            this.Font = null;
        }
    }
}
