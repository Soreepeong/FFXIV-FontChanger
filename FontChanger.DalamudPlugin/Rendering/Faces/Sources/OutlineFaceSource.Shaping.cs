using System;
using System.Collections.Generic;

using FontChanger.DalamudPlugin.Rendering.Records;
using FontChanger.DalamudPlugin.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources;

// Shaping with DirectWrite: the font's features and language, measured as the face measures.
internal abstract unsafe partial class OutlineFaceSource
{
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

    private DWRITE_FONT_FEATURE[]? _features;

    /// <summary>Shapes text with DirectWrite's shaping of the font at a size: its features and language, as the face measures.</summary>
    public int Shape(float px, ReadOnlySpan<char> text, ScriptToken script, List<ShapedCluster> clusters)
    {
        var length = text.Length;
        if (length == 0)
            return 0;

        var face = (IDWriteFontFace*)this.Font.GetFace(px);
        var analysis = new DWRITE_SCRIPT_ANALYSIS { script = script.Script, shapes = (DWRITE_SCRIPT_SHAPES)script.Shapes };
        var clusterMap = new ushort[length];
        var textProps = new DWRITE_SHAPING_TEXT_PROPERTIES[length];
        var maxGlyphs = (length * 3 / 2) + 16;
        ushort[] glyphs;
        DWRITE_SHAPING_GLYPH_PROPERTIES[] glyphProps;
        uint count;
        var features = this.GetFeatures();
        var locale = this.Def.Lookup.LanguageCode.Length != 0 ? this.Def.Lookup.LanguageCode : "en-us";
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
                    hr = this.Services.Analyzer.GetGlyphs(
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
                    this.Services.Analyzer.GetGdiCompatibleGlyphPlacements(
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
                    this.Services.Analyzer.GetGlyphPlacements(
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

                clusters.Add(new(ids, clusterAdvances, clusterOffsets, k, k2));
                k = k2;
            }
        }

        return length;
    }

    /// <summary>
    /// Gets the features shaping applies: null for DirectWrite's defaults; else the font's, with the defaults that a set of
    /// features would otherwise turn off on unless the font's say otherwise.
    /// </summary>
    private DWRITE_FONT_FEATURE[]? GetFeatures()
    {
        var features = this.Def.Lookup.Features;
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
}
