using System;
using System.Collections.Generic;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Fonts;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using FreeTypeSharp;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Faces.ElementFonts;

/// <summary>
/// A font's glyphs (xivres directwrite_fixed_size_font and freetype_fixed_size_font): with FreeType if the element is drawn
/// so or its font has only bitmaps, else with DirectWrite. Its glyphs are also shaped with DirectWrite, in text laid out
/// with <see cref="ApplyTo"/>.
/// </summary>
internal sealed unsafe class OutlineElementFont : IElementFont
{
    private static readonly uint RomanBaseline = Preset.Tag("romn"u8);
    private static readonly uint[] IdeographicBaselines = [Preset.Tag("ideo"u8), Preset.Tag("idtp"u8), Preset.Tag("icfb"u8), Preset.Tag("icft"u8)];

    private readonly GlyphRasterizer _rasterizer;
    private readonly FaceElement _def;
    private readonly byte[]? _gamma;
    private readonly Dictionary<(FFXIVClientStructs.Interop.Pointer<IDWriteFontFace> A, FFXIVClientStructs.Interop.Pointer<IDWriteFontFace> B), bool> _sameFonts = [];

    // References to the faces compared in _sameFonts, so their pointers aren't reused.
    private readonly List<ComPtr<IDWriteFontFace>> _comparedFaces = [];
    private Dictionary<uint, int>? _baselines;
    private HashSet<int>? _codepoints;
    private ComPtr<IDWriteTypography> _typography;
    private bool _typographyMade;

    private OutlineElementFont(GlyphRasterizer rasterizer, FaceElement def, LookupFont font, FT_FaceRec_* freeTypeFace, byte[]? gamma)
    {
        this._rasterizer = rasterizer;
        this._def = def;
        this.Font = font;
        this.FreeTypeFace = freeTypeFace;
        this._gamma = gamma;
        this.Transform = def.Transform * font.Transform;
    }

    public LookupFont Font { get; }

    /// <summary>Gets the lookup the font was found by, which layouts ask for (with its language and features).</summary>
    public LookupStruct Lookup => this._def.Lookup;

    /// <summary>Gets how the glyphs are transformed on screen: by synthesis, then by the element.</summary>
    public Matrix2x2 Transform { get; }

    /// <summary>Gets or sets how glyphs are measured (DWRITE_MEASURING_MODE): the face's, which its text is laid out with.</summary>
    public int MeasureMode { get; set; }

    /// <summary>The FreeType face the glyphs are drawn with (owned by <see cref="FreeTypeFonts"/>), or null for DirectWrite.</summary>
    private FT_FaceRec_* FreeTypeFace { get; }

    /// <summary>
    /// Finds the font an element's lookup names and makes it as it draws (<paramref name="renderer"/>, for synthesis);
    /// null if the family isn't installed.
    /// </summary>
    public static OutlineElementFont? Create(GlyphRasterizer rasterizer, FaceElement def, Renderer renderer, byte[]? gamma, string faceName)
    {
        var font = LookupFont.Create(rasterizer, def.Lookup, renderer);
        if (font is null)
        {
            Plugin.Log.Warning("{face}: font {family} not found", faceName, def.Lookup.Name);
            return null;
        }

        // FreeType draws what the preset says it does, and fonts with only bitmaps, which DirectWrite doesn't draw.
        var ftFace = rasterizer.FreeType is { } freeType ? freeType.Open(font.Face) : null;
        if (ftFace is null || (renderer != Renderer.FreeType && FreeTypeFonts.IsScalable(ftFace)))
        {
            if (renderer == Renderer.FreeType)
                Plugin.Log.Warning("{family} can't be opened with FreeType; it is drawn with DirectWrite", def.Lookup.Name);
            ftFace = null;
        }

        return new(rasterizer, def, font, ftFace, gamma);
    }

    public bool Has(Rune rune, AtkFontManager.Font* game)
    {
        BOOL exists;
        return this.Font.Font->HasCharacter((uint)rune.Value, &exists).SUCCEEDED && exists;
    }

    public IEnumerable<int> Codepoints => this._codepoints ??= this.ReadCodepoints();

    private HashSet<int> ReadCodepoints()
    {
        var res = new HashSet<int>();
        using var face1 = default(ComPtr<IDWriteFontFace1>);
        var iid = IID.IID_IDWriteFontFace1;
        if (this.Font.Face->QueryInterface(&iid, (void**)face1.GetAddressOf()).FAILED)
            return res;

        uint count;
        face1.Get()->GetUnicodeRanges(0, null, &count);
        var ranges = new DWRITE_UNICODE_RANGE[count];
        fixed (DWRITE_UNICODE_RANGE* p = ranges)
        {
            if (face1.Get()->GetUnicodeRanges(count, p, &count).FAILED)
                return res;
        }

        foreach (var r in ranges.AsSpan(0, (int)count))
        {
            for (var c = r.first; c <= r.last; c++)
                res.Add((int)c);
        }

        return res;
    }

    public (int Ascent, int LineHeight) GetLineMetrics(float px, AtkFontManager.Font* game) => this.GetLineMetrics(px, this.Transform);

    /// <summary>Gets the line metrics at a size, scaled vertically as glyphs transformed by <paramref name="transform"/> are.</summary>
    public (int Ascent, int LineHeight) GetLineMetrics(float px, Matrix2x2 transform)
    {
        var m = this.Font.Metrics;
        var size = px / m.designUnitsPerEm * MathF.Abs(transform.M22);
        var ascent = (int)MathF.Ceiling(m.ascent * size);
        var descent = (int)MathF.Ceiling(m.descent * size);
        return (ascent, ascent + descent + (int)Rounding.Round(m.lineGap * size));
    }

    public bool TryGetBaseline(uint tag, float px, out float baseline)
    {
        this._baselines ??= ReadBaselines(this.Font.Face);
        if (!this._baselines.TryGetValue(tag, out var value))
        {
            baseline = 0;
            return false;
        }

        baseline = value * px * this.Transform.M22 / this.Font.Metrics.designUnitsPerEm;
        return true;
    }

    public RasterGlyph? Rasterize(Rune rune, float px) =>
        this.TryGetGlyph(rune, px, out var face, out var index, out var advance)
            ? this.RasterizeRun(face, px, &index, &advance, null, 1, 0, (int)Rounding.Round(advance * this.Transform.M11))
            : null;

    /// <summary>Draws the glyph narrower: rasterized at the scale, which keeps its hinting and stroke weight.</summary>
    public RasterGlyph Squeeze(Rune rune, float px, RasterGlyph glyph, float scale) =>
        this.TryGetGlyph(rune, px, out var face, out var index, out var advance)
            ? this.RasterizeRun(face, px, &index, &advance, null, 1, 0, glyph.Advance, scale)
            : glyph;

    public float GetAdvance(Rune rune, float px) =>
        this.TryGetGlyph(rune, px, out _, out _, out var advance) ? advance * this.Transform.M11 : 0;

    /// <summary>Gets how much further glyphs advance than DirectWrite lays them out: FreeType's emboldening.</summary>
    public float GetExtraAdvance(float emSize) => this.FreeTypeFace is not null ?this.Font.Embolden * emSize : 0;

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
        Matrix2x2? transform = null)
    {
        var t = Matrix2x2.CreateScale(squeezeX, 1) * (transform ?? this.Transform);

        // FreeType draws the font's own face; another face's glyphs (its family's italic) are DirectWrite's.
        var glyph = this.FreeTypeFace is not null &&this.SameFont(this.Font.GetFace(size), face)
                        ? this._rasterizer.FreeType!.RasterizeRun(
                            this.FreeTypeFace,
                            size,
                            glyphs,
                            advances,
                            offsets,
                            count,
                            originX,
                            advance,
                            this._def.FreeType,
                            t,
                            this.Font.GetAxisValues(size),
                            this.Font.Embolden)
                        : this._rasterizer.RasterizeRun(
                            face,
                            size,
                            glyphs,
                            advances,
                            offsets,
                            count,
                            originX,
                            advance,
                            this._def.DirectWrite with { MeasureMode = this.MeasureMode },
                            t);
        return this._gamma is null ? glyph : glyph.WithCoverage(this._gamma);
    }

    /// <summary>
    /// Gets the vertical extent in pixels of a glyph's ink, moved by its offset in a shaped run, as
    /// <see cref="RasterizeRun"/> would draw it; null if it has none.
    /// </summary>
    public (int Top, int Bottom)? MeasureGlyph(IDWriteFontFace* face, float size, ushort glyph, DWRITE_GLYPH_OFFSET offset, Matrix2x2 transform)
    {
        var advance = 0f;
        RECT bounds;
        if (this.FreeTypeFace is not null &&this.SameFont(this.Font.GetFace(size), face))
        {
            var g = this._rasterizer.FreeType!.RasterizeRun(
                this.FreeTypeFace,
                size,
                &glyph,
                &advance,
                &offset,
                1,
                0,
                0,
                this._def.FreeType,
                transform,
                this.Font.GetAxisValues(size),
                this.Font.Embolden);
            bounds = new() { left = g.Left, top = g.Top, right = g.Left + g.Width, bottom = g.Top + g.Height };
        }
        else
        {
            bounds = this._rasterizer.GetRunBounds(
                face,
                size,
                &glyph,
                &advance,
                &offset,
                1,
                this._def.DirectWrite with { MeasureMode = this.MeasureMode },
                transform);
        }

        return bounds.right <= bounds.left || bounds.bottom <= bounds.top ? null : (bounds.top, bounds.bottom);
    }

    /// <summary>Gets a glyph's ink extent horizontally in pixels from its origin, from its outline's side bearings.</summary>
    public (float Left, float Right) GetInkExtent(IDWriteFontFace* face, float size, ushort glyph, Matrix2x2 transform)
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
        var lookup = this._def.Lookup;
        var font = this.Font;
        fixed (char* family = lookup.Name)
            layout->SetFontFamilyName(family, range).ThrowOnError();
        layout->SetFontWeight((DWRITE_FONT_WEIGHT)font.LayoutWeight, range).ThrowOnError();
        layout->SetFontStretch((DWRITE_FONT_STRETCH)font.LayoutStretch, range).ThrowOnError();
        layout->SetFontStyle((DWRITE_FONT_STYLE)font.LayoutStyle, range).ThrowOnError();
        layout->SetFontSize(emSize, range).ThrowOnError();
        if (lookup.LanguageCode.Length != 0)
        {
            fixed (char* languageCode = lookup.LanguageCode)
                layout->SetLocaleName(languageCode, range).ThrowOnError();
        }

        if (this.GetTypography() is var typography && typography is not null)
            layout->SetTypography(typography, range).ThrowOnError();

        // A variable font's axes; the optical size follows the size unless set.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            using var layout4 = default(ComPtr<IDWriteTextLayout4>);
            var iid = IID.IID_IDWriteTextLayout4;
            if (layout->QueryInterface(&iid, (void**)layout4.GetAddressOf()).SUCCEEDED)
            {
                var axes = font.LayoutAxes;
                if (!axes.IsEmpty)
                {
                    fixed (DWRITE_FONT_AXIS_VALUE* a = axes)
                        layout4.Get()->SetFontAxisValues(a, (uint)axes.Length, range).ThrowOnError();
                }

                if (font.AutoOpticalSize)
                    layout4.Get()->SetAutomaticFontAxes(DWRITE_AUTOMATIC_FONT_AXES.DWRITE_AUTOMATIC_FONT_AXES_OPTICAL_SIZE).ThrowOnError();
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
        this._typography.Dispose();

        foreach (var face in this._comparedFaces)
            face.Get()->Release();
        this._comparedFaces.Clear();
        this._sameFonts.Clear();
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
        if (face->TryGetFontTable(Preset.Tag("BASE"u8), &data, &size, &context, &exists).FAILED || !exists)
            return result;
        try
        {
            var table = new ReadOnlySpan<byte>(data, (int)size);
            foreach (var (tag, value) in ReadHorizontalBaselines(table, [Preset.Tag("latn"u8), Preset.Tag("DFLT"u8)]))
            {
                if (tag == RomanBaseline)
                    result.TryAdd(tag, value);
            }

            foreach (var (tag, value) in ReadHorizontalBaselines(
                         table,
                         [Preset.Tag("hani"u8), Preset.Tag("kana"u8), Preset.Tag("hang"u8), Preset.Tag("DFLT"u8)]))
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
    private bool TryGetGlyph(Rune rune, float px, out IDWriteFontFace* face, out ushort index, out float advance)
    {
        face = this.Font.GetFace(px);
        ushort i;
        advance = 0;
        if (face->GetGlyphIndices((uint*)&rune, 1, &i).FAILED || i == 0)
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

    /// <summary>The OpenType features DirectWrite turns on by default for horizontal text.</summary>
    private static readonly DWRITE_FONT_FEATURE_TAG[] DefaultOnFeatures =
    [
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STANDARD_LIGATURES,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_CONTEXTUAL_LIGATURES,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_CONTEXTUAL_ALTERNATES,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_KERNING,
    ];

    /// <summary>Gets the font's OpenType features as a typography; null if it sets none.</summary>
    private IDWriteTypography* GetTypography()
    {
        if (this._typographyMade)
            return this._typography.Get();

        this._typographyMade = true;
        var features = this._def.Lookup.Features;
        if (features.Count == 0)
            return null;
        this._rasterizer.Factory->CreateTypography(this._typography.GetAddressOf()).ThrowOnError();
        var t = this._typography.Get();

        // A typography replaces the features that are on by default instead of adding to them; those stay on unless
        // the font's features say otherwise.
        foreach (var tag in DefaultOnFeatures)
        {
            if (!features.ContainsKey((uint)tag))
                t->AddFontFeature(new() { nameTag = tag, parameter = 1 }).ThrowOnError();
        }

        foreach (var (tag, value) in features)
            t->AddFontFeature(new() { nameTag = (DWRITE_FONT_FEATURE_TAG)tag, parameter = value }).ThrowOnError();
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
        var key = ((FFXIVClientStructs.Interop.Pointer<IDWriteFontFace>)a, (FFXIVClientStructs.Interop.Pointer<IDWriteFontFace>)b);
        if (this._sameFonts.TryGetValue(key, out var same))
            return same;

        if (a->GetIndex() == b->GetIndex() && a->GetGlyphCount() == b->GetGlyphCount())
        {
            using var fileA = FontFileKey.Of(a);
            using var fileB = FontFileKey.Of(b);
            same = fileA.Loader is not null && fileA.Loader == fileB.Loader && fileA.Key.SequenceEqual(fileB.Key);
        }

        this._comparedFaces.Add(new(a));
        this._comparedFaces.Add(new(b));
        this._sameFonts.Add(key, same);
        return same;
    }
}