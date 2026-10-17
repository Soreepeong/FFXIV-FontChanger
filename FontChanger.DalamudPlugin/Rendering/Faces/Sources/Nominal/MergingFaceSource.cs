using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using FontChanger.DalamudPlugin.Rendering.Enums;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Rendering.Records;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources.Nominal;

/// <summary>
/// Merged glyphs (xivres glyph_merging_fixed_size_font) around an element's source: texts in shapes, for the codepoints
/// the merging maps; the line metrics and baselines are the source's. Glyph IDs are codepoints.
/// </summary>
/// <param name="inner">The element's own source.</param>
/// <param name="textOutline">The font the texts are drawn with; null to draw them with <paramref name="inner"/>'s glyph images.</param>
internal sealed partial class MergingFaceSource(
    IFaceSource inner,
    FaceElement def,
    GlyphMergingStruct merging,
    FontServices services,
    OutlineFaceSource? textOutline) : NominalFaceSource
{
    /// <summary>Gets the outline sources it draws with (its own and its texts'), which the face measures alike.</summary>
    public IEnumerable<OutlineFaceSource> OutlineSources => new[] { inner as OutlineFaceSource, textOutline }.OfType<OutlineFaceSource>();

    public override IEnumerable<Rune> Codepoints => merging.Glyphs.Keys;

    /// <summary>
    /// Makes glyph merging around an element's source. Its texts are drawn with the element's font; glyph images draw
    /// them with the font the lookup names, if any, else with their own files.
    /// </summary>
    public static MergingFaceSource Create(IFaceSource inner, FaceElement def, GlyphMergingStruct merging, FontServices services, string faceName)
    {
        var textOutline = inner is ImageFaceSource && def.Lookup.Name.Length != 0
                              ? OutlineFaceSource.Create(services, def with { Renderer = Renderer.DirectWrite }, faceName)
                              : inner as OutlineFaceSource;
        return new(inner, def, merging, services, textOutline);
    }

    public override uint GetGlyphId(Rune rune) => merging.Glyphs.ContainsKey(rune) ? (uint)rune.Value : 0;

    public override (int Ascent, int LineHeight) GetLineMetrics(float px) => inner.GetLineMetrics(px);

    public override bool TryGetBaseline(float px, Baseline baseline, out float y) => inner.TryGetBaseline(px, baseline, out y);

    protected override RasterGlyph? RasterizeGlyph(float px, uint glyphId)
    {
        if (!Rune.IsValid(glyphId) || !merging.Glyphs.TryGetValue(new((int)glyphId), out var glyph))
            return null;
        var baseGlyph = glyph.Mapping.Shape == MergeShape.Glyph ? this.DrawInner(new((int)glyphId), px) : null;
        return this.Draw(glyph.Mapping, glyph.Text, px, baseGlyph);
    }

    public override void Dispose()
    {
        inner.Dispose();
        if (!ReferenceEquals(textOutline, inner))
            textOutline?.Dispose();
    }

    /// <summary>
    /// Makes the font texts are drawn with at a size, condensed horizontally: transformed by the element, then the texts'
    /// transformation, then the condensing.
    /// </summary>
    private IMergeTextFont CreateTextFont(float size, float condense)
    {
        var transform = new Matrix2x2(condense, 0, 0, 1) * merging.TextTransform * def.Transform;
        return textOutline?.CreateMergeTextFont(size, transform)
               ?? (inner as ImageFaceSource)?.CreateMergeTextFont(size, transform)
               ?? NoTextFont.Instance;
    }

    /// <summary>Draws the source's own glyph of a codepoint.</summary>
    private RasterGlyph? DrawInner(Rune rune, float px)
    {
        var id = inner.GetGlyphId(rune);
        if (id == 0)
            return null;
        ReadOnlySpan<uint> ids = [id];
        ReadOnlySpan<float> advances = [inner.GetAdvance(px, id)];
        return inner.Rasterize(px, new(ids, advances));
    }

    /// <summary>The text font of an element whose font isn't there: it has no glyphs.</summary>
    private sealed class NoTextFont : IMergeTextFont
    {
        public static readonly NoTextFont Instance = new();

        public int Ascent => 0;

        public bool Has(Rune rune) => false;

        public MergeTextLine LayOut(string text, int letterSpacing) => new(0, false, 0, 0, 0, 0, _ => new(0, 0, 0, 0, 0, []));
    }
}
