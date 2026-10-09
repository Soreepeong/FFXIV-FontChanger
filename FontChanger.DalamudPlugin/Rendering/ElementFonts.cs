using System;
using System.Collections.Generic;
using System.Linq;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace CustomFonts;

/// <summary>
/// The glyphs a face element draws (xivres fixed_size_font), at any size: what it has, its line metrics and baselines,
/// and its glyphs, rasterized as the element draws them (its gamma applied) but not yet adjusted by the face
/// (<see cref="ReplacementFace.Wrap"/>). Made by <see cref="ReplacementFace"/> from an element's renderer: the game's
/// glyphs, none, a font (DirectWrite or FreeType), glyph images, or glyph merging around one of those.
/// </summary>
/// <remarks>Sizes are the element's (the face's drawn size scaled by the element's size); glyphs are placed relative to
/// the pen on the baseline.</remarks>
internal unsafe interface IElementFont : IDisposable
{
    /// <summary>Gets whether a codepoint has a glyph; the game's glyphs are those of <paramref name="game"/>.</summary>
    bool Has(int codepoint, GameFont* game);

    /// <summary>Gets the line metrics at a size, in whole pixels.</summary>
    (int Ascent, int LineHeight) GetLineMetrics(float px, GameFont* game);

    /// <summary>Gets a baseline of the BASE table (by tag, as DWRITE_FONT_FEATURE_TAG) in pixels above the glyphs' origin; null if none.</summary>
    float? GetBaseline(uint tag, float px);

    /// <summary>Rasterizes a codepoint's glyph at a size; null if there is none, or the game draws it.</summary>
    RasterGlyph? Rasterize(int codepoint, float px);

    /// <summary>
    /// Gets a glyph (<paramref name="glyph"/>, rasterized by <see cref="Rasterize"/>) drawn narrower by a horizontal scale,
    /// for monospacing: resampled, unless the font can draw it so.
    /// </summary>
    RasterGlyph Squeeze(int codepoint, float px, RasterGlyph glyph, float scale) => glyph.SqueezedX(scale);

    /// <summary>Gets a codepoint's advance on screen at a size, unrounded (monospacing's reference glyph); 0 if none.</summary>
    float GetAdvance(int codepoint, float px);
}

/// <summary>The game's own glyphs (xivres fontdata_fixed_size_font), which the game draws.</summary>
internal sealed unsafe class GameElementFont : IElementFont
{
    public bool Has(int codepoint, GameFont* game) => game is not null && game->HasGlyph(codepoint);

    public (int Ascent, int LineHeight) GetLineMetrics(float px, GameFont* game)
    {
        if (game is null || game->Size <= 0)
            return (0, 0);
        var scale = px / game->Size;
        return ((int)Rounding.Round(game->Ascent * scale), (int)Rounding.Round(game->LineHeight * scale));
    }

    public float? GetBaseline(uint tag, float px) => null;

    public RasterGlyph? Rasterize(int codepoint, float px) => null;

    public float GetAdvance(int codepoint, float px) => 0;

    public void Dispose()
    {
    }
}

/// <summary>No glyphs, only line metrics (xivres empty_fixed_size_font), given in pixels at the element's size.</summary>
internal sealed unsafe class EmptyElementFont(float size, float ascent, float lineHeight) : IElementFont
{
    public bool Has(int codepoint, GameFont* game) => false;

    public (int Ascent, int LineHeight) GetLineMetrics(float px, GameFont* game) =>
        size > 0 ? ((int)Rounding.Round(ascent * px / size), (int)Rounding.Round(lineHeight * px / size)) : (0, 0);

    public float? GetBaseline(uint tag, float px) => null;

    public RasterGlyph? Rasterize(int codepoint, float px) => null;

    public float GetAdvance(int codepoint, float px) => 0;

    public void Dispose()
    {
    }
}

/// <summary>
/// A font's glyphs (xivres directwrite_fixed_size_font and freetype_fixed_size_font): with FreeType if the element is drawn
/// so or its font has only bitmaps, else with DirectWrite. Its glyphs are also shaped with DirectWrite, in text laid out
/// with <see cref="ApplyTo"/>.
/// </summary>
internal sealed unsafe class OutlineElementFont : IElementFont
{
    private static readonly uint RomanBaseline = Preset.Tag("romn");
    private static readonly uint[] IdeographicBaselines = [Preset.Tag("ideo"), Preset.Tag("idtp"), Preset.Tag("icfb"), Preset.Tag("icft")];

    private readonly GlyphRasterizer rasterizer;
    private readonly ElementDef def;
    private readonly byte[]? gamma;
    private readonly Dictionary<(nint A, nint B), bool> sameFonts = [];
    private Dictionary<uint, int>? baselines;
    private nint typography;
    private bool typographyMade;

    private OutlineElementFont(GlyphRasterizer rasterizer, ElementDef def, LookupFont font, nint freeTypeFace, byte[]? gamma)
    {
        this.rasterizer = rasterizer;
        this.def = def;
        this.Font = font;
        this.FreeTypeFace = freeTypeFace;
        this.gamma = gamma;
        this.Transform = def.Transform.After(font.Transform);
    }

    /// <summary>Gets the font.</summary>
    public LookupFont Font { get; }

    /// <summary>Gets the lookup the font was found by, which layouts ask for (with its language and features).</summary>
    public LookupDef Lookup => this.def.Lookup;

    /// <summary>Gets how the glyphs are transformed on screen: by synthesis, then by the element.</summary>
    public GlyphTransform Transform { get; }

    /// <summary>Gets or sets how glyphs are measured (DWRITE_MEASURING_MODE): the face's, which its text is laid out with.</summary>
    public int MeasureMode { get; set; }

    /// <summary>The FreeType face the glyphs are drawn with (owned by <see cref="FreeTypeFonts"/>), or 0 for DirectWrite.</summary>
    private nint FreeTypeFace { get; }

    /// <summary>
    /// Finds the font an element's lookup names and makes it as it draws (<paramref name="renderer"/>, for synthesis);
    /// null if the family isn't installed.
    /// </summary>
    public static OutlineElementFont? Create(GlyphRasterizer rasterizer, ElementDef def, ElementRenderer renderer, byte[]? gamma, string faceName)
    {
        var font = LookupFont.Create(rasterizer, def.Lookup, renderer);
        if (font is null)
        {
            Host.Log.Warning("{face}: font {family} not found", faceName, def.Lookup.Name);
            return null;
        }

        // FreeType draws what the preset says it does, and fonts with only bitmaps, which DirectWrite doesn't draw.
        var ftFace = rasterizer.FreeType?.Open(font.Face) ?? 0;
        if (ftFace == 0 || (renderer != ElementRenderer.FreeType && FreeTypeFonts.IsScalable(ftFace)))
        {
            if (renderer == ElementRenderer.FreeType)
                Host.Log.Warning("{family} can't be opened with FreeType; it is drawn with DirectWrite", def.Lookup.Name);
            ftFace = 0;
        }

        return new(rasterizer, def, font, ftFace, gamma);
    }

    public bool Has(int codepoint, GameFont* game)
    {
        BOOL exists;
        return this.Font.Font->HasCharacter((uint)codepoint, &exists).SUCCEEDED && exists;
    }

    public (int Ascent, int LineHeight) GetLineMetrics(float px, GameFont* game) => this.GetLineMetrics(px, this.Transform);

    /// <summary>Gets the line metrics at a size, scaled vertically as glyphs transformed by <paramref name="transform"/> are.</summary>
    public (int Ascent, int LineHeight) GetLineMetrics(float px, GlyphTransform transform)
    {
        var m = this.Font.Metrics;
        var size = px / m.designUnitsPerEm * MathF.Abs(transform.M22);
        var ascent = (int)MathF.Ceiling(m.ascent * size);
        var descent = (int)MathF.Ceiling(m.descent * size);
        return (ascent, ascent + descent + (int)Rounding.Round(m.lineGap * size));
    }

    public float? GetBaseline(uint tag, float px)
    {
        this.baselines ??= ReadBaselines(this.Font.Face);
        return this.baselines.TryGetValue(tag, out var value) ? value * px * this.Transform.M22 / this.Font.Metrics.designUnitsPerEm : null;
    }

    public RasterGlyph? Rasterize(int codepoint, float px) =>
        this.TryGetGlyph(codepoint, px, out var face, out var index, out var advance)
            ? this.RasterizeRun(face, px, &index, &advance, null, 1, 0, (int)Rounding.Round(advance * this.Transform.M11))
            : null;

    /// <summary>Draws the glyph narrower: rasterized at the scale, which keeps its hinting and stroke weight.</summary>
    public RasterGlyph Squeeze(int codepoint, float px, RasterGlyph glyph, float scale) =>
        this.TryGetGlyph(codepoint, px, out var face, out var index, out var advance)
            ? this.RasterizeRun(face, px, &index, &advance, null, 1, 0, glyph.Advance, scale)
            : glyph;

    public float GetAdvance(int codepoint, float px) =>
        this.TryGetGlyph(codepoint, px, out _, out _, out var advance) ? advance * this.Transform.M11 : 0;

    /// <summary>Gets how much further glyphs advance than DirectWrite lays them out: FreeType's emboldening.</summary>
    public float GetExtraAdvance(float emSize) => this.FreeTypeFace != 0 ? this.Font.Embolden * emSize : 0;

    /// <summary>
    /// Rasterizes a run of glyphs of a font face (one glyph, or a shaped cluster): with FreeType if the font is drawn so
    /// and the face is its own, else with DirectWrite. The glyphs are transformed as the element says (or by
    /// <paramref name="transform"/>), then squeezed horizontally by <paramref name="squeezeX"/>; <paramref name="originX"/>
    /// is on screen, after the transformation, and <paramref name="advance"/> is the advance the glyph is given.
    /// </summary>
    public RasterGlyph RasterizeRun(
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
        var t = (transform ?? this.Transform).ScaledX(squeezeX);

        // FreeType draws the font's own face; another face's glyphs (its family's italic) are DirectWrite's.
        var glyph = this.FreeTypeFace != 0 && this.SameFont(this.Font.GetFace(size), face)
            ? this.rasterizer.FreeType!.RasterizeRun(
                this.FreeTypeFace, size, glyphs, advances, offsets, count, originX, advance, this.def.FreeType, t, this.Font.GetAxisValues(size), this.Font.Embolden)
            : this.rasterizer.RasterizeRun(face, size, glyphs, advances, offsets, count, originX, advance, this.def.DirectWrite with { MeasureMode = this.MeasureMode }, t);
        return this.gamma is null ? glyph : glyph.WithCoverage(this.gamma);
    }

    /// <summary>
    /// Gets the vertical extent in pixels of a glyph's ink, moved by its offset in a shaped run, as
    /// <see cref="RasterizeRun"/> would draw it; null if it has none.
    /// </summary>
    public (int Top, int Bottom)? MeasureGlyph(IDWriteFontFace* face, float size, ushort glyph, DWRITE_GLYPH_OFFSET offset, GlyphTransform transform)
    {
        var advance = 0f;
        RECT bounds;
        if (this.FreeTypeFace != 0 && this.SameFont(this.Font.GetFace(size), face))
        {
            var g = this.rasterizer.FreeType!.RasterizeRun(this.FreeTypeFace, size, &glyph, &advance, &offset, 1, 0, 0, this.def.FreeType, transform, this.Font.GetAxisValues(size), this.Font.Embolden);
            bounds = new() { left = g.Left, top = g.Top, right = g.Left + g.Width, bottom = g.Top + g.Height };
        }
        else
        {
            bounds = this.rasterizer.GetRunBounds(face, size, &glyph, &advance, &offset, 1, this.def.DirectWrite with { MeasureMode = this.MeasureMode }, transform);
        }

        return bounds.right <= bounds.left || bounds.bottom <= bounds.top ? null : (bounds.top, bounds.bottom);
    }

    /// <summary>Gets a glyph's ink extent horizontally in pixels from its origin, from its outline's side bearings.</summary>
    public (float Left, float Right) GetInkExtent(IDWriteFontFace* face, float size, ushort glyph, GlyphTransform transform)
    {
        DWRITE_GLYPH_METRICS gm;
        if (face->GetDesignGlyphMetrics(&glyph, 1, &gm, BOOL.FALSE).FAILED)
            return (0, 0);
        DWRITE_FONT_METRICS m;
        face->GetMetrics(&m);
        var scale = size / m.designUnitsPerEm * transform.M11;
        var (x1, x2) = (gm.leftSideBearing * scale, ((int)gm.advanceWidth - gm.rightSideBearing) * scale);
        return x1 <= x2 ? (x1, x2) : (x2, x1);
    }

    /// <summary>
    /// Sets the font, size, language, features and axis values on a range of a text layout, asking for what matches the
    /// font with its simulations.
    /// </summary>
    public void ApplyTo(IDWriteTextLayout* layout, DWRITE_TEXT_RANGE range, float emSize)
    {
        var lookup = this.def.Lookup;
        var font = this.Font;
        fixed (char* family = lookup.Name)
            layout->SetFontFamilyName(family, range).ThrowOnError();
        layout->SetFontWeight((DWRITE_FONT_WEIGHT)font.LayoutWeight, range).ThrowOnError();
        layout->SetFontStretch((DWRITE_FONT_STRETCH)font.LayoutStretch, range).ThrowOnError();
        layout->SetFontStyle((DWRITE_FONT_STYLE)font.LayoutStyle, range).ThrowOnError();
        layout->SetFontSize(emSize, range).ThrowOnError();
        if (lookup.Language.Length != 0)
        {
            fixed (char* language = lookup.Language)
                layout->SetLocaleName(language, range).ThrowOnError();
        }

        if (this.GetTypography() is var typography && typography is not null)
            layout->SetTypography(typography, range).ThrowOnError();

        // A variable font's axes; the optical size follows the size unless set.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
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
    /// Gets the face a glyph run of a layout is drawn from: the font's own (with its simulations and axis values at the
    /// size) if the layout picked the font, else the layout's.
    /// </summary>
    public IDWriteFontFace* GetRunFace(IDWriteFontFace* runFace, float emSize)
    {
        var own = this.Font.GetFace(emSize);
        return this.SameFont(own, runFace) ? own : runFace;
    }

    public void Dispose()
    {
        if (this.typography != 0)
        {
            ((IUnknown*)this.typography)->Release();
            this.typography = 0;
        }

        foreach (var (a, b) in this.sameFonts.Keys)
        {
            ((IUnknown*)a)->Release();
            ((IUnknown*)b)->Release();
        }

        this.sameFonts.Clear();
        this.Font.Dispose();
    }

    /// <summary>
    /// Reads the baselines of a font's BASE table (xivres read_baselines), in font units above the glyphs' origin: the
    /// roman baseline of Latin (or the default script), and the ideographic ones of Han, kana or Hangul (or the default).
    /// </summary>
    private static Dictionary<uint, int> ReadBaselines(IDWriteFontFace* face)
    {
        var result = new Dictionary<uint, int>();
        void* data;
        uint size;
        void* context;
        BOOL exists;
        if (face->TryGetFontTable(Preset.Tag("BASE"), &data, &size, &context, &exists).FAILED || !exists)
            return result;
        try
        {
            var table = new ReadOnlySpan<byte>(data, (int)size);
            foreach (var (tag, value) in ReadHorizontalBaselines(table, [Preset.Tag("latn"), Preset.Tag("DFLT")]))
            {
                if (tag == RomanBaseline)
                    result.TryAdd(tag, value);
            }

            foreach (var (tag, value) in ReadHorizontalBaselines(table, [Preset.Tag("hani"), Preset.Tag("kana"), Preset.Tag("hang"), Preset.Tag("DFLT")]))
            {
                if (IdeographicBaselines.Contains(tag))
                    result.TryAdd(tag, value);
            }
        }
        finally
        {
            face->ReleaseFontTable(context);
        }

        return result;
    }

    /// <summary>
    /// Reads the horizontal baselines of the first of <paramref name="scripts"/> the BASE table lists, or of its first
    /// script if none (xivres util::truetype::Base::ReadHorizontalBaselines).
    /// </summary>
    private static List<(uint Tag, int Value)> ReadHorizontalBaselines(ReadOnlySpan<byte> table, uint[] scripts)
    {
        var result = new List<(uint, int)>();
        if (U16(table, 0) != 1)
            return result;

        // Axis { Offset16 baseTagListOffset; Offset16 baseScriptListOffset; }
        var axis = U16(table, 4);
        if (axis == 0)
            return result;
        var tagListOffset = U16(table, axis);
        var scriptListOffset = U16(table, axis + 2);
        if (tagListOffset == 0 || scriptListOffset == 0)
            return result;

        // BaseTagList { uint16 baseTagCount; Tag baselineTags[]; }
        var tagList = axis + tagListOffset;
        var tags = new uint[U16(table, tagList)];
        for (var i = 0; i < tags.Length; i++)
        {
            if (tagList + 2 + (4 * i) + 4 > table.Length)
                return result;
            tags[i] = BitConverter.ToUInt32(table.Slice(tagList + 2 + (4 * i), 4));
        }

        // BaseScriptList { uint16 baseScriptCount; { Tag baseScriptTag; Offset16 baseScriptOffset; }[] }
        var scriptList = axis + scriptListOffset;
        var scriptCount = U16(table, scriptList);
        int? script = null;
        foreach (var wanted in scripts)
        {
            for (var i = 0; i < scriptCount && script is null; i++)
            {
                if (scriptList + 2 + (6 * i) + 6 > table.Length)
                    return result;
                if (BitConverter.ToUInt32(table.Slice(scriptList + 2 + (6 * i), 4)) == wanted)
                    script = scriptList + U16(table, scriptList + 2 + (6 * i) + 4);
            }

            if (script is not null)
                break;
        }

        if (script is null && scriptCount != 0)
            script = scriptList + U16(table, scriptList + 6);
        if (script is not { } s)
            return result;

        // BaseScript { Offset16 baseValuesOffset; ... }
        // BaseValues { uint16 defaultBaselineIndex; uint16 baseCoordCount; Offset16 baseCoordOffsets[]; }
        // BaseCoord { uint16 format; int16 coordinate; ... }
        var valuesOffset = U16(table, s);
        if (valuesOffset == 0)
            return result;
        var values = s + valuesOffset;
        var coordCount = U16(table, values + 2);
        for (var i = 0; i < coordCount && i < tags.Length; i++)
        {
            var coord = U16(table, values + 4 + (2 * i));
            if (coord != 0 && values + coord + 4 <= table.Length)
                result.Add((tags[i], (short)U16(table, values + coord + 2)));
        }

        return result;

        static int U16(ReadOnlySpan<byte> t, int offset) => offset >= 0 && offset + 2 <= t.Length ? (t[offset] << 8) | t[offset + 1] : 0;
    }

    /// <summary>Gets a codepoint's glyph in the face at a size and its advance (with emboldening; unscaled); false if none.</summary>
    private bool TryGetGlyph(int codepoint, float px, out IDWriteFontFace* face, out ushort index, out float advance)
    {
        face = this.Font.GetFace(px);
        var cp = (uint)codepoint;
        ushort i;
        advance = 0;
        if (face->GetGlyphIndices(&cp, 1, &i).FAILED || i == 0)
        {
            index = 0;
            return false;
        }

        index = i;
        DWRITE_GLYPH_METRICS gm;
        face->GetDesignGlyphMetrics(&i, 1, &gm, BOOL.FALSE).ThrowOnError();
        advance = (gm.advanceWidth * px / this.Font.Metrics.designUnitsPerEm) + this.GetExtraAdvance(px);
        return true;
    }

    /// <summary>Gets the font's OpenType features as a typography; null if it sets none.</summary>
    private IDWriteTypography* GetTypography()
    {
        if (this.typographyMade)
            return (IDWriteTypography*)this.typography;

        this.typographyMade = true;
        var features = this.def.Lookup.Features;
        if (features.Count == 0)
            return null;
        IDWriteTypography* t;
        this.rasterizer.Factory->CreateTypography(&t).ThrowOnError();
        this.typography = (nint)t;
        foreach (var (tag, value) in features)
            t->AddFontFeature(new DWRITE_FONT_FEATURE { nameTag = (DWRITE_FONT_FEATURE_TAG)tag, parameter = value }).ThrowOnError();
        return t;
    }

    /// <summary>
    /// Gets whether two faces are of the same font (the same file and index, so the same glyphs), whatever their
    /// simulations and axis values. A family's styles often have the same glyph count, so the files are compared; the
    /// answer is kept, with a reference to the faces so their pointers aren't reused.
    /// </summary>
    private bool SameFont(IDWriteFontFace* a, IDWriteFontFace* b)
    {
        if (a == b)
            return true;
        if (this.sameFonts.TryGetValue(((nint)a, (nint)b), out var same))
            return same;

        if (a->GetIndex() == b->GetIndex() && a->GetGlyphCount() == b->GetGlyphCount())
        {
            using var fileA = FontFileKey.Of(a);
            using var fileB = FontFileKey.Of(b);
            same = fileA.Loader is not null && fileA.Loader == fileB.Loader && fileA.Key.SequenceEqual(fileB.Key);
        }

        ((IUnknown*)a)->AddRef();
        ((IUnknown*)b)->AddRef();
        this.sameFonts.Add(((nint)a, (nint)b), same);
        return same;
    }
}

/// <summary>Glyph images (xivres image_fixed_size_font): SVG and PNG files per glyph, drawn with Direct2D.</summary>
internal sealed unsafe class ImageElementFont(GlyphImages? images, ImageRenderer? renderer, GlyphTransform transform, byte[]? gamma) : IElementFont
{
    /// <summary>Gets whether glyphs can be drawn (Direct2D can be used).</summary>
    public bool CanDraw => images is not null && renderer is not null;

    public bool Has(int codepoint, GameFont* game) => images?.Has(codepoint) ?? false;

    public (int Ascent, int LineHeight) GetLineMetrics(float px, GameFont* game) => this.GetLineMetrics(px, transform);

    /// <summary>Gets the line metrics at a size, scaled vertically as glyphs transformed by <paramref name="t"/> are.</summary>
    public (int Ascent, int LineHeight) GetLineMetrics(float px, GlyphTransform t) => images?.GetLineMetrics(px, t) ?? (0, 0);

    public float? GetBaseline(uint tag, float px) => null;

    public RasterGlyph? Rasterize(int codepoint, float px) => this.Rasterize(codepoint, px, transform);

    /// <summary>Rasterizes a codepoint's glyph at a size, transformed by <paramref name="t"/>.</summary>
    public RasterGlyph? Rasterize(int codepoint, float px, GlyphTransform t) =>
        images is not null && renderer is not null && images.Rasterize(renderer, codepoint, px, t) is { } g
            ? gamma is null ? g : g.WithCoverage(gamma)
            : null;

    public float GetAdvance(int codepoint, float px) => this.Rasterize(codepoint, px)?.Advance ?? 0;

    /// <summary>Gets the kerning between two codepoints in pixels at a size, as <paramref name="t"/> scales advances.</summary>
    public int GetKerning(int left, int right, float px, GlyphTransform t) => images?.GetKerning(left, right, px, t) ?? 0;

    public void Dispose()
    {
    }
}

/// <summary>
/// Glyph merging (xivres glyph_merging_fixed_size_font) around an element's font: its merged codepoints only, each a text
/// put in a shape (<see cref="GlyphMerger"/>), the text drawn with fonts <paramref name="textFont"/> makes at a size and
/// horizontal scale (from <paramref name="textOutline"/>, which it owns, if it isn't <paramref name="baseFont"/>).
/// Metrics and baselines are the font's.
/// </summary>
internal sealed unsafe class MergingElementFont(
    IElementFont baseFont,
    ElementDef def,
    GlyphMergingDef merging,
    Func<float, float, IMergeTextFont> textFont,
    GlyphRasterizer rasterizer,
    OutlineElementFont? textOutline) : IElementFont
{
    /// <summary>Gets the fonts of DirectWrite and FreeType it draws with: its own, and its texts'.</summary>
    public IEnumerable<OutlineElementFont> OutlineFonts => new[] { baseFont as OutlineElementFont, textOutline }.OfType<OutlineElementFont>();

    public bool Has(int codepoint, GameFont* game) => merging.Glyphs.ContainsKey(codepoint);

    public (int Ascent, int LineHeight) GetLineMetrics(float px, GameFont* game) => baseFont.GetLineMetrics(px, game);

    public float? GetBaseline(uint tag, float px) => baseFont.GetBaseline(tag, px);

    public RasterGlyph? Rasterize(int codepoint, float px)
    {
        if (!merging.Glyphs.TryGetValue(codepoint, out var glyph))
            return null;

        // Pixel values of the merging are at the face's size, which the element's is to its own.
        return GlyphMerger.Draw(
            glyph.Mapping,
            glyph.Text,
            merging,
            def.Size > 0 ? px / def.Size : 1,
            px,
            def.Size,
            baseFont.GetLineMetrics(px, null).Ascent,
            textFont,
            glyph.Mapping.Shape == MergeShape.Glyph ? baseFont.Rasterize(codepoint, px) : null,
            rasterizer.Images,
            rasterizer.FreeType);
    }

    /// <summary>Gets the advance of the font's own glyph (monospacing's reference glyph is measured in it).</summary>
    public float GetAdvance(int codepoint, float px) => baseFont.GetAdvance(codepoint, px);

    public void Dispose()
    {
        baseFont.Dispose();
        if (!ReferenceEquals(textOutline, baseFont))
            textOutline?.Dispose();
    }
}

/// <summary>Texts of glyph merging in a font, shaped with its features, measured by its glyphs' metrics.</summary>
internal sealed unsafe class OutlineTextFont(OutlineElementFont font, float size, GlyphTransform transform, GlyphRasterizer rasterizer) : IMergeTextFont, IGlyphRunSink
{
    private readonly List<TextRun> runs = [];

    public int Ascent { get; } = font.GetLineMetrics(size, transform).Ascent;

    public bool Has(int codepoint) => font.Has(codepoint, null);

    public MergeTextLine LayOut(string text, int letterSpacing)
    {
        this.runs.Clear();
        var spacing = text.Length != 0 && transform.M11 != 0 ? letterSpacing / transform.M11 : 0;
        if (text.Length != 0)
            this.Shape(text, spacing);

        // Letter spacing goes between glyphs.
        var runs = this.runs.ToArray();
        var extra = font.GetExtraAdvance(size);
        var width = runs.Sum(r => r.Advances.Sum() + (extra * r.Glyphs.Length)) - spacing;
        var advance = (int)Rounding.Round(width * transform.M11);

        // The ink: horizontally from the outlines, vertically in pixels (xivres glyph_merging layout_text).
        float inkX1 = float.MaxValue, inkX2 = float.MinValue;
        int inkY1 = int.MaxValue, inkY2 = int.MinValue;
        var pen = 0f;
        foreach (var r in runs)
        {
            // Each run is drawn from the pen, its glyphs at their advances and offsets, transformed (as RasterizeRun does).
            var runFace = font.GetRunFace((IDWriteFontFace*)r.Face, r.EmSize);
            var along = 0f;
            for (var i = 0; i < r.Glyphs.Length; i++)
            {
                var offset = r.Offsets?[i] ?? default;
                var x = pen + (transform.M11 * (along + offset.advanceOffset)) - (transform.M12 * offset.ascenderOffset);
                if (font.MeasureGlyph(runFace, r.EmSize, r.Glyphs[i], offset with { advanceOffset = 0 }, transform) is { } ink)
                {
                    var (left, right) = font.GetInkExtent(runFace, r.EmSize, r.Glyphs[i], transform);
                    (inkX1, inkX2) = (Math.Min(inkX1, x + left), Math.Max(inkX2, x + right));
                    (inkY1, inkY2) = (Math.Min(inkY1, ink.Top), Math.Max(inkY2, ink.Bottom));
                }

                along += r.Advances[i] + extra;
            }

            pen += along * transform.M11;
        }

        if (inkX1 > inkX2 || inkY1 > inkY2)
            (inkX1, inkX2, inkY1, inkY2) = (0, 0, 0, 0);
        else
            (inkY1, inkY2) = (inkY1 + this.Ascent, inkY2 + this.Ascent);

        return new(advance, true, inkX1, inkX2, inkY1, inkY2, x =>
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
                    var runFace = font.GetRunFace((IDWriteFontFace*)r.Face, r.EmSize);
                    pieces.Add(font.RasterizeRun(runFace, r.EmSize, g, a, o, (uint)r.Glyphs.Length, pen, 0, 1, transform));
                }

                pen += advances.Sum() * transform.M11;
            }

            return RasterGlyph.Merge(pieces, advance);
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
        var format = rasterizer.CreateFormat(font.Lookup.Name, font.Font.LayoutWeight, font.Font.LayoutStyle, font.Font.LayoutStretch, size, rasterizer.NoFallback);
        IDWriteTextLayout* layout = null;
        try
        {
            fixed (char* t = text)
                rasterizer.Factory->CreateTextLayout(t, (uint)text.Length, (IDWriteTextFormat*)format, 1e6f, 1e6f, &layout).ThrowOnError();

            var range = new DWRITE_TEXT_RANGE { startPosition = 0, length = (uint)text.Length };
            font.ApplyTo(layout, range, size);
            if (letterSpacing != 0)
            {
                IDWriteTextLayout1* layout1;
                var iid = IID.IID_IDWriteTextLayout1;
                if (((IUnknown*)layout)->QueryInterface(&iid, (void**)&layout1).SUCCEEDED)
                {
                    layout1->SetCharacterSpacing(0, letterSpacing, 0, range);
                    layout1->Release();
                }
            }

            rasterizer.RunCollector.Collect(layout, this);
        }
        finally
        {
            if (layout is not null)
                layout->Release();
            format->Release();
        }
    }

    /// <summary>The glyph run of a laid out line: its face, size, and glyphs.</summary>
    private sealed record TextRun(nint Face, float EmSize, ushort[] Glyphs, float[] Advances, DWRITE_GLYPH_OFFSET[]? Offsets);
}

/// <summary>Texts of glyph merging in glyph images, glyph by glyph with font.json's kerning.</summary>
internal sealed unsafe class ImageTextFont(ImageElementFont? images, float size, GlyphTransform transform) : IMergeTextFont
{
    public int Ascent { get; } = images?.GetLineMetrics(size, transform).Ascent ?? 0;

    public bool Has(int codepoint) => images is { CanDraw: true } && images.Has(codepoint, null);

    public MergeTextLine LayOut(string text, int letterSpacing)
    {
        var pieces = new List<RasterGlyph>();
        var codepoints = text.EnumerateRunes().Select(r => r.Value).ToArray();
        var pen = 0;
        for (var i = 0; i < codepoints.Length; i++)
        {
            if (images!.Rasterize(codepoints[i], size, transform) is not { } g)
                continue;
            pieces.Add(g with { Left = g.Left + pen });
            pen += i + 1 < codepoints.Length ? g.Advance + images.GetKerning(codepoints[i], codepoints[i + 1], size, transform) + letterSpacing : g.Advance;
        }

        // The ink: the glyphs' boxes (xivres image_fixed_size_font's metrics).
        var merged = RasterGlyph.Merge(pieces, pen);
        var (inkX1, inkX2, inkY1, inkY2) = merged.Width == 0
            ? (0f, 0f, 0, 0)
            : (merged.Left, merged.Left + merged.Width, this.Ascent + merged.Top, this.Ascent + merged.Top + merged.Height);
        return new(pen, false, inkX1, inkX2, inkY1, inkY2, x => merged with { Left = merged.Left + (int)Rounding.Round(x) });
    }
}
