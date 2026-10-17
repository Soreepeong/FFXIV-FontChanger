using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Faces.Sources.Outline;
using FontChanger.DalamudPlugin.Rendering.Fonts;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources;

/// <summary>
/// A font's glyphs: found by the element's lookup, shaped with DirectWrite, and drawn by <see cref="DirectWriteFaceSource"/>
/// or <see cref="FreeTypeFaceSource"/>. Glyph IDs are the font's glyph indices.
/// </summary>
internal abstract unsafe partial class OutlineFaceSource : IFaceSource
{
    private static readonly uint[] BaselineTags =
    [
        Preset.Tag("romn"u8), Preset.Tag("ideo"u8), Preset.Tag("idtp"u8), Preset.Tag("icfb"u8), Preset.Tag("icft"u8),
    ];

    // DirectWrite's GetGlyphs: HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER).
    private const int InsufficientBuffer = unchecked((int)0x8007007A);

    /// <summary>The OpenType features DirectWrite turns on by default for horizontal text.</summary>
    private static readonly DWRITE_FONT_FEATURE_TAG[] DefaultOnFeatures =
    [
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_STANDARD_LIGATURES,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_CONTEXTUAL_LIGATURES,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_CONTEXTUAL_ALTERNATES,
        DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_KERNING,
    ];

    private readonly GlyphRasterizer _rasterizer;
    private readonly FaceElement _def;
    private readonly Renderer _renderer;
    private readonly byte[]? _gamma;
    private readonly string _faceName;
    private readonly Dictionary<(FFXIVClientStructs.Interop.Pointer<IDWriteFontFace5> A, FFXIVClientStructs.Interop.Pointer<IDWriteFontFace5> B), bool> _sameFonts = [];

    // References to the faces compared in _sameFonts, so their pointers aren't reused.
    private readonly List<ComPtr<IDWriteFontFace5>> _comparedFaces = [];
    private Dictionary<uint, int>? _baselines;
    private HashSet<Rune>? _codepoints;
    private DWRITE_FONT_FEATURE[]? _features;
    private ComPtr<IDWriteTypography> _typography;
    private bool _typographyMade;
    private OutlineFaceSource? _italic;
    private bool _italicMade;

    protected OutlineFaceSource(GlyphRasterizer rasterizer, FaceElement def, Renderer renderer, LookupFont font, byte[]? gamma, string faceName)
    {
        this._rasterizer = rasterizer;
        this._def = def;
        this._renderer = renderer;
        this.Font = font;
        this._gamma = gamma;
        this._faceName = faceName;
        this.Transform = def.Transform * font.Transform;
    }

    protected LookupFont Font { get; }

    /// <summary>Gets how the glyphs are transformed on screen: by synthesis, then by the element.</summary>
    public Matrix2x2 Transform { get; }

    /// <summary>Gets or sets how glyphs are measured (DWRITE_MEASURING_MODE): the face's, which its text is shaped with.</summary>
    public int MeasureMode
    {
        get;
        set
        {
            field = value;
            if (this._italic is { } italic)
                italic.MeasureMode = value;
        }
    }

    public IEnumerable<Rune> Codepoints => this._codepoints ??= this.ReadCodepoints();

    protected GlyphRasterizer Rasterizer => this._rasterizer;

    protected FaceElement Def => this._def;

    /// <summary>
    /// Finds the font an element's lookup names and makes it as it draws (<paramref name="renderer"/>, for synthesis);
    /// null if the family isn't installed.
    /// </summary>
    public static OutlineFaceSource? Create(GlyphRasterizer rasterizer, FaceElement def, Renderer renderer, byte[]? gamma, string faceName)
    {
        var font = LookupFont.Create(rasterizer, def.Lookup, renderer);
        if (font is null)
        {
            Plugin.Log.Warning("{face}: font {family} not found", faceName, def.Lookup.Name);
            return null;
        }

        // FreeType draws what the preset says it does, and fonts with only bitmaps, which DirectWrite doesn't draw.
        var ftFace = rasterizer.FreeType is { } freeType ? freeType.Open(font.Face) : null;
        if (ftFace is not null && (renderer == Renderer.FreeType || !FreeTypeFonts.IsScalable(ftFace)))
            return new FreeTypeFaceSource(rasterizer, def, renderer, font, ftFace, gamma, faceName);

        if (renderer == Renderer.FreeType)
            Plugin.Log.Warning("{family} can't be opened with FreeType; it is drawn with DirectWrite", def.Lookup.Name);
        return new DirectWriteFaceSource(rasterizer, def, renderer, font, gamma, faceName);
    }

    public uint GetGlyphId(Rune rune)
    {
        var codepoint = (uint)rune.Value;
        ushort index;
        return this.Font.Face->GetGlyphIndices(&codepoint, 1, &index).SUCCEEDED ? index : 0u;
    }

    public float GetAdvance(float px, uint glyphId)
    {
        var index = (ushort)glyphId;
        DWRITE_GLYPH_METRICS gm;
        if (this.Font.GetFace(px)->GetDesignGlyphMetrics(&index, 1, &gm, BOOL.FALSE).FAILED)
            return 0;
        return gm.advanceWidth * px / this.Font.Metrics.designUnitsPerEm;
    }

    public float GetScreenAdvance(float px, in GlyphRun run)
    {
        var sum = 0f;
        foreach (var a in run.Advances)
            sum += a;
        return (sum + (this.GetExtraAdvance(px) * run.GlyphIds.Length)) * this.Transform.M11;
    }

    public (int Ascent, int LineHeight) GetLineMetrics(float px) => this.GetLineMetrics(px, this.Transform);

    /// <summary>Gets the line metrics at a size, scaled vertically as glyphs transformed by <paramref name="transform"/> are.</summary>
    private (int Ascent, int LineHeight) GetLineMetrics(float px, Matrix2x2 transform)
    {
        var m = this.Font.Metrics;
        var size = px / m.designUnitsPerEm * MathF.Abs(transform.M22);
        var ascent = (int)MathF.Ceiling(m.ascent * size);
        var descent = (int)MathF.Ceiling(m.descent * size);
        return (ascent, ascent + descent + (int)Rounding.Round(m.lineGap * size));
    }

    public bool TryGetBaseline(float px, Baseline baseline, out float y)
    {
        this._baselines ??= ReadBaselines(this.Font.Face);
        if (!this._baselines.TryGetValue(BaselineTags[(int)baseline], out var value))
        {
            y = 0;
            return false;
        }

        y = value * px * this.Transform.M22 / this.Font.Metrics.designUnitsPerEm;
        return true;
    }

    /// <summary>
    /// Gets the source of its family's italic, drawn as this one is; itself if its font is an italic, or (not real) if the
    /// family has none of its own.
    /// </summary>
    public (IFaceSource Source, bool Real) GetItalic()
    {
        if (IsRealItalic(this.Font.Face))
            return (this, true);

        if (!this._italicMade)
        {
            this._italicMade = true;
            try
            {
                var lookup = this._def.Lookup with { Style = (int)DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC };
                var font = LookupFont.Create(this._rasterizer, lookup, this._renderer);
                if (font is not null && IsRealItalic(font.Face) && !this.SameFont(font.Face, this.Font.Face))
                {
                    font.Dispose();
                    this._italic = Create(this._rasterizer, this._def with { Lookup = lookup }, this._renderer, this._gamma, this._faceName);
                    if (this._italic is not null)
                        this._italic.MeasureMode = this.MeasureMode;
                }
                else
                {
                    font?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "{face}: the italic of {family} can't be made", this._faceName, this._def.Lookup.Name);
            }
        }

        return this._italic is not null ? (this._italic, true) : (this, false);
    }

    /// <summary>Shapes text with DirectWrite's shaping of the font at a size: its features and language, as the face measures.</summary>
    public int Shape(float px, ReadOnlySpan<char> text, ScriptToken script, List<ShapedCluster> clusters)
    {
        var length = text.Length;
        if (length == 0)
            return 0;

        var face = (IDWriteFontFace*)this.Font.GetFace(px);
        var analyzer = this._rasterizer.Analyzer;
        var analysis = new DWRITE_SCRIPT_ANALYSIS { script = script.Script, shapes = (DWRITE_SCRIPT_SHAPES)script.Shapes };
        var clusterMap = new ushort[length];
        var textProps = new DWRITE_SHAPING_TEXT_PROPERTIES[length];
        var maxGlyphs = (length * 3 / 2) + 16;
        ushort[] glyphs;
        DWRITE_SHAPING_GLYPH_PROPERTIES[] glyphProps;
        uint count;
        var features = this.GetFeatures();
        var locale = this._def.Lookup.LanguageCode.Length != 0 ? this._def.Lookup.LanguageCode : "en-us";
        var featureLength = (uint)length;
        var measureMode = this.MeasureMode;

        fixed (char* t = text)
        fixed (char* l = locale)
        fixed (DWRITE_FONT_FEATURE* f = features)
        {
            var typographic = new DWRITE_TYPOGRAPHIC_FEATURES { features = f, featureCount = (uint)(features?.Length ?? 0) };
            var typographicPointer = &typographic;
            var featureRanges = features is null ? 0u : 1u;
            var featureSets = features is null ? null : &typographicPointer;
            var featureLengths = features is null ? null : &featureLength;

            while (true)
            {
                glyphs = new ushort[maxGlyphs];
                glyphProps = new DWRITE_SHAPING_GLYPH_PROPERTIES[maxGlyphs];
                HRESULT hr;
                fixed (ushort* cm = clusterMap)
                fixed (DWRITE_SHAPING_TEXT_PROPERTIES* tp = textProps)
                fixed (ushort* g = glyphs)
                fixed (DWRITE_SHAPING_GLYPH_PROPERTIES* gp = glyphProps)
                {
                    hr = analyzer->GetGlyphs(
                        t,
                        (uint)length,
                        face,
                        BOOL.FALSE,
                        BOOL.FALSE,
                        &analysis,
                        l,
                        null,
                        featureSets,
                        featureLengths,
                        featureRanges,
                        (uint)maxGlyphs,
                        cm,
                        tp,
                        g,
                        gp,
                        &count);
                }

                if (hr.Value == InsufficientBuffer)
                {
                    maxGlyphs *= 2;
                    continue;
                }

                hr.ThrowOnError();
                break;
            }

            var advances = new float[count];
            var offsets = new DWRITE_GLYPH_OFFSET[count];
            fixed (ushort* cm = clusterMap)
            fixed (DWRITE_SHAPING_TEXT_PROPERTIES* tp = textProps)
            fixed (ushort* g = glyphs)
            fixed (DWRITE_SHAPING_GLYPH_PROPERTIES* gp = glyphProps)
            fixed (float* a = advances)
            fixed (DWRITE_GLYPH_OFFSET* o = offsets)
            {
                // A face measuring as GDI does (hinted, whole-pixel advances) is shaped so; its glyphs are drawn to match.
                if (measureMode is 1 or 2)
                {
                    analyzer->GetGdiCompatibleGlyphPlacements(
                        t,
                        cm,
                        tp,
                        (uint)length,
                        g,
                        gp,
                        count,
                        face,
                        px,
                        1,
                        null,
                        measureMode == 2 ? BOOL.TRUE : BOOL.FALSE,
                        BOOL.FALSE,
                        BOOL.FALSE,
                        &analysis,
                        l,
                        featureSets,
                        featureLengths,
                        featureRanges,
                        a,
                        o).ThrowOnError();
                }
                else
                {
                    analyzer->GetGlyphPlacements(
                        t,
                        cm,
                        tp,
                        (uint)length,
                        g,
                        gp,
                        count,
                        face,
                        px,
                        BOOL.FALSE,
                        BOOL.FALSE,
                        &analysis,
                        l,
                        featureSets,
                        featureLengths,
                        featureRanges,
                        a,
                        o).ThrowOnError();
                }
            }

            // Code units mapping to the same first glyph are a cluster, which ends where the next one's glyphs start.
            var pen = 0f;
            for (var k = 0; k < length;)
            {
                var glyphStart = clusterMap[k];
                var k2 = k + 1;
                while (k2 < length && clusterMap[k2] == glyphStart)
                    k2++;
                var glyphEnd = k2 < length ? clusterMap[k2] : (int)count;
                var n = glyphEnd - glyphStart;

                var ids = new uint[n];
                var clusterAdvances = new float[n];
                GlyphOffset[]? clusterOffsets = null;
                for (var i = 0; i < n; i++)
                {
                    var glyph = glyphs[glyphStart + i];
                    if (glyph == 0)
                        return k;
                    ids[i] = glyph;
                    clusterAdvances[i] = advances[glyphStart + i];
                    var offset = offsets[glyphStart + i];
                    if (offset.advanceOffset != 0 || offset.ascenderOffset != 0)
                    {
                        clusterOffsets ??= new GlyphOffset[n];
                        clusterOffsets[i] = new(offset.advanceOffset, offset.ascenderOffset);
                    }
                }

                clusters.Add(new(this, ids, clusterAdvances, clusterOffsets, pen, k, k2));
                foreach (var a in clusterAdvances)
                    pen += a;
                k = k2;
            }
        }

        return length;
    }

    public RasterGlyph? Rasterize(float px, in GlyphRun run, in RasterizeOptions options = default)
    {
        var count = run.GlyphIds.Length;
        if (count == 0)
            return null;

        // Emboldening widens each glyph's advance.
        var extra = this.GetExtraAdvance(px);
        var glyphs = count <= 64 ? stackalloc ushort[count] : new ushort[count];
        var advances = count <= 64 ? stackalloc float[count] : new float[count];
        for (var i = 0; i < count; i++)
        {
            glyphs[i] = (ushort)run.GlyphIds[i];
            advances[i] = run.Advances[i] + extra;
        }

        var advance = options.Advance ?? (int)Rounding.Round(this.GetScreenAdvance(px, run));
        var offsetSpan = MemoryMarshal.Cast<GlyphOffset, DWRITE_GLYPH_OFFSET>(run.Offsets);
        fixed (ushort* g = glyphs)
        fixed (float* a = advances)
        fixed (DWRITE_GLYPH_OFFSET* o = offsetSpan)
        {
            return this.RasterizeRun(
                this.Font.GetFace(px),
                px,
                g,
                a,
                offsetSpan.IsEmpty ? null : o,
                unchecked((uint)count),
                options.OriginX,
                advance,
                options.SqueezeX);
        }
    }

    /// <summary>Gets how much further glyphs advance than DirectWrite lays them out: none, but for FreeType's emboldening.</summary>
    protected virtual float GetExtraAdvance(float emSize) => 0;

    /// <summary>
    /// Rasterizes a run of glyphs of a font face (one glyph, or a shaped cluster), with the gamma. The glyphs are
    /// transformed as the element says (or by <paramref name="transform"/>), then squeezed horizontally by
    /// <paramref name="squeezeX"/>; <paramref name="originX"/> is on screen, after the transformation, and
    /// <paramref name="advance"/> is the advance the glyph is given.
    /// </summary>
    /// <remarks>The face is the font's own at the size, but for glyph merging's texts, whose layouts may pick another.</remarks>
    private RasterGlyph RasterizeRun(
        IDWriteFontFace5* face,
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
        var glyph = this.DrawRun(face, size, glyphs, advances, offsets, count, originX, advance, t);
        return this._gamma is null ? glyph : glyph.WithCoverage(this._gamma);
    }

    /// <summary>
    /// Gets the vertical extent in pixels of a glyph's ink, moved by its offset in a shaped run, as
    /// <see cref="RasterizeRun"/> would draw it; null if it has none.
    /// </summary>
    private (int Top, int Bottom)? MeasureGlyph(IDWriteFontFace5* face, float size, ushort glyph, DWRITE_GLYPH_OFFSET offset, Matrix2x2 transform)
    {
        var bounds = this.MeasureRun(face, size, glyph, offset, transform);
        return bounds.right <= bounds.left || bounds.bottom <= bounds.top ? null : (bounds.top, bounds.bottom);
    }

    /// <summary>Draws a run of glyphs, transformed (as <see cref="RasterizeRun"/> says), without the gamma.</summary>
    protected abstract RasterGlyph DrawRun(
        IDWriteFontFace5* face,
        float size,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        Matrix2x2 transform);

    /// <summary>Gets the pixels a glyph covers, moved by its offset, with the pen at the origin.</summary>
    protected abstract RECT MeasureRun(IDWriteFontFace5* face, float size, ushort glyph, DWRITE_GLYPH_OFFSET offset, Matrix2x2 transform);

    /// <summary>Draws a run of glyphs with DirectWrite, measured as the face measures.</summary>
    protected RasterGlyph DrawWithDirectWrite(
        IDWriteFontFace5* face,
        float size,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        Matrix2x2 transform) =>
        this._rasterizer.RasterizeRun(
            face,
            size,
            glyphs,
            advances,
            offsets,
            count,
            originX,
            advance,
            this._def.DirectWrite with { MeasureMode = this.MeasureMode },
            transform);

    /// <summary>Gets the pixels a glyph drawn with DirectWrite covers.</summary>
    protected RECT MeasureWithDirectWrite(IDWriteFontFace5* face, float size, ushort glyph, DWRITE_GLYPH_OFFSET offset, Matrix2x2 transform)
    {
        var advance = 0f;
        return this._rasterizer.GetRunBounds(
            face,
            size,
            &glyph,
            &advance,
            &offset,
            1,
            this._def.DirectWrite with { MeasureMode = this.MeasureMode },
            transform);
    }

    /// <summary>Gets a glyph's ink extent horizontally in pixels from its origin, from its outline's side bearings.</summary>
    private (float Left, float Right) GetInkExtent(IDWriteFontFace5* face, float size, ushort glyph, Matrix2x2 transform)
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
    /// font with its simulations (glyph merging's texts are laid out so).
    /// </summary>
    private void ApplyTo(IDWriteTextLayout4* layout, DWRITE_TEXT_RANGE range, float emSize)
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
        var axes = font.LayoutAxes;
        if (!axes.IsEmpty)
        {
            fixed (DWRITE_FONT_AXIS_VALUE* a = axes)
                layout->SetFontAxisValues(a, (uint)axes.Length, range).ThrowOnError();
        }

        if (font.AutoOpticalSize)
            layout->SetAutomaticFontAxes(DWRITE_AUTOMATIC_FONT_AXES.DWRITE_AUTOMATIC_FONT_AXES_OPTICAL_SIZE).ThrowOnError();
    }

    /// <summary>
    /// Gets the face a glyph run of a layout is drawn from: the font's own (with its simulations and axis values at the
    /// size) if the layout picked the font, else the layout's.
    /// </summary>
    private IDWriteFontFace5* GetRunFace(IDWriteFontFace5* runFace, float emSize)
    {
        var own = this.Font.GetFace(emSize);
        return this.SameFont(own, runFace) ? own : runFace;
    }

    public void Dispose()
    {
        this._italic?.Dispose();
        this._typography.Dispose();

        foreach (var face in this._comparedFaces)
            face.Get()->Release();
        this._comparedFaces.Clear();
        this._sameFonts.Clear();
        this.Font.Dispose();
    }

    /// <summary>Gets whether a face is a real italic (or oblique) style of its family, not one synthesized.</summary>
    private static bool IsRealItalic(IDWriteFontFace5* face) =>
        (face->GetSimulations() & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE) == 0 &&
        face->GetStyle() != DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL;

    private HashSet<Rune> ReadCodepoints()
    {
        var res = new HashSet<Rune>();
        var face = this.Font.Face;
        uint count;
        face->GetUnicodeRanges(0, null, &count);
        var ranges = new DWRITE_UNICODE_RANGE[count];
        fixed (DWRITE_UNICODE_RANGE* p = ranges)
        {
            if (face->GetUnicodeRanges(count, p, &count).FAILED)
                return res;
        }

        foreach (var r in ranges.AsSpan(0, (int)count))
        {
            // Ranges may span surrogates, which aren't codepoints.
            for (var c = r.first; c <= r.last; c++)
            {
                if (Rune.IsValid(c))
                    res.Add(new(c));
            }
        }

        return res;
    }

    /// <summary>
    /// Gets the features shaping applies: null for DirectWrite's defaults; else the font's, with the defaults that a set of
    /// features would otherwise turn off on unless the font's say otherwise.
    /// </summary>
    private DWRITE_FONT_FEATURE[]? GetFeatures()
    {
        var features = this._def.Lookup.Features;
        if (features.Count == 0)
            return null;
        if (this._features is not null)
            return this._features;

        var list = new List<DWRITE_FONT_FEATURE>();
        foreach (var tag in DefaultOnFeatures)
        {
            if (!features.ContainsKey((uint)tag))
                list.Add(new() { nameTag = tag, parameter = 1 });
        }

        foreach (var (tag, value) in features)
            list.Add(new() { nameTag = (DWRITE_FONT_FEATURE_TAG)tag, parameter = value });
        return this._features = list.ToArray();
    }

    /// <summary>Gets the font's OpenType features as a typography (for layouts); null if it sets none.</summary>
    private IDWriteTypography* GetTypography()
    {
        if (this._typographyMade)
            return this._typography.Get();

        this._typographyMade = true;
        if (this.GetFeatures() is not { } features)
            return null;
        this._rasterizer.Factory->CreateTypography(this._typography.GetAddressOf()).ThrowOnError();
        foreach (var f in features)
            this._typography.Get()->AddFontFeature(f).ThrowOnError();
        return this._typography.Get();
    }

    /// <summary>
    /// Reads the baselines of a font's BASE table (xivres read_baselines), in font units above the glyphs' origin: the
    /// roman baseline of Latin (or the default script), and the ideographic ones of Han, kana or Hangul (or the default).
    /// </summary>
    private static Dictionary<uint, int> ReadBaselines(IDWriteFontFace5* face)
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
                if (tag == BaselineTags[(int)Baseline.Roman])
                    result.TryAdd(tag, value);
            }

            foreach (var (tag, value) in ReadHorizontalBaselines(
                         table,
                         [Preset.Tag("hani"u8), Preset.Tag("kana"u8), Preset.Tag("hang"u8), Preset.Tag("DFLT"u8)]))
            {
                if (tag != BaselineTags[(int)Baseline.Roman] && Array.IndexOf(BaselineTags, tag) >= 0)
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

    /// <summary>
    /// Gets whether two faces are of the same font (the same file and index, so the same glyphs), whatever their
    /// simulations and axis values. A family's styles often have the same glyph count, so the files are compared; the
    /// answer is kept, with a reference to the faces so their pointers aren't reused.
    /// </summary>
    protected bool SameFont(IDWriteFontFace5* a, IDWriteFontFace5* b)
    {
        if (a == b)
            return true;
        var key = ((FFXIVClientStructs.Interop.Pointer<IDWriteFontFace5>)a, (FFXIVClientStructs.Interop.Pointer<IDWriteFontFace5>)b);
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
