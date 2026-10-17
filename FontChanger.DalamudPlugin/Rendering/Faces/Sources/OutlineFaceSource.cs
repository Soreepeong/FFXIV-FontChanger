using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Faces.Sources.Outline;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Rendering.Records;
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
    private readonly byte[]? _gamma;
    private readonly string _faceName;
    private HashSet<Rune>? _codepoints;
    private OutlineFaceSource? _italic;
    private bool _italicMade;

    /// <summary>Takes <paramref name="font"/>, which it disposes.</summary>
    protected OutlineFaceSource(FontServices services, FaceElement def, LookupFont font, string faceName)
    {
        this.Services = services;
        this.Def = def;
        this.Font = font;
        this._gamma = RasterGlyph.GammaTable(def.Gamma);
        this._faceName = faceName;
        this.Transform = def.Transform * font.Transform;
    }

    protected FontServices Services { get; }

    protected FaceElement Def { get; }

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

    /// <summary>
    /// Finds the font an element's lookup names and makes it as the element draws it (its renderer, for synthesis); null
    /// if the family isn't installed.
    /// </summary>
    public static OutlineFaceSource? Create(FontServices services, FaceElement def, string faceName)
    {
        var font = LookupFont.Create(services, def.Lookup, def.Renderer);
        if (font is null)
        {
            Plugin.Log.Warning("{face}: font {family} not found", faceName, def.Lookup.Name);
            return null;
        }

        return FromFont(services, def, font, faceName);
    }

    /// <summary>
    /// Makes the source of a font, drawn by FreeType where the element says so and for fonts with only bitmaps (which
    /// DirectWrite doesn't draw), else by DirectWrite. Takes <paramref name="font"/>.
    /// </summary>
    private static OutlineFaceSource FromFont(FontServices services, FaceElement def, LookupFont font, string faceName)
    {
        var ftFace = services.FreeType.Open(font.Face);
        if (ftFace is not null && (def.Renderer == Renderer.FreeType || !FreeTypeGlyphRasterizer.IsScalable(ftFace)))
            return new FreeTypeFaceSource(services, def, font, ftFace, faceName);

        if (def.Renderer == Renderer.FreeType)
            Plugin.Log.Warning("{family} can't be opened with FreeType; it is drawn with DirectWrite", def.Lookup.Name);
        return new DirectWriteFaceSource(services, def, font, faceName);
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
        return (ascent, ascent + descent + (int)Misc.StdRound(m.lineGap * size));
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
                var lookup = this.Def.Lookup with { Style = (int)DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC };
                var font = LookupFont.Create(this.Services, lookup, this.Def.Renderer);
                if (font is not null && IsRealItalic(font.Face) && !SameFont(font.Face, this.Font.Face))
                {
                    this._italic = FromFont(this.Services, this.Def with { Lookup = lookup }, font, this._faceName);
                    this._italic.MeasureMode = this.MeasureMode;
                }
                else
                {
                    font?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "{face}: the italic of {family} can't be made", this._faceName, this.Def.Lookup.Name);
            }
        }

        return this._italic is not null ? (this._italic, true) : (this, false);
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

        var advance = options.Advance ?? (int)Misc.StdRound(this.GetScreenAdvance(px, run));
        var offsetSpan = MemoryMarshal.Cast<GlyphOffset, DWRITE_GLYPH_OFFSET>(run.Offsets);
        fixed (ushort* g = glyphs)
        fixed (float* a = advances)
        fixed (DWRITE_GLYPH_OFFSET* o = offsetSpan)
        {
            return this.RasterizeRun(
                px,
                g,
                a,
                offsetSpan.IsEmpty ? null : o,
                unchecked((uint)count),
                options.OriginX,
                advance,
                Matrix2x2.CreateScale(options.SqueezeX, 1) * this.Transform);
        }
    }

    /// <summary>Gets how much further glyphs advance than DirectWrite lays them out: none, but for FreeType's emboldening.</summary>
    protected virtual float GetExtraAdvance(float emSize) => 0;

    /// <summary>
    /// Rasterizes a run of glyphs of the font's face at a size (one glyph, a shaped cluster, or a text of glyph merging),
    /// transformed by <paramref name="transform"/>, with the gamma; <paramref name="originX"/> is on screen, after the
    /// transformation, and <paramref name="advance"/> is the advance the glyph is given.
    /// </summary>
    private RasterGlyph RasterizeRun(
        float size,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        Matrix2x2 transform)
    {
        var glyph = this.DrawRun(size, glyphs, advances, offsets, count, originX, advance, transform);
        return this._gamma is null ? glyph : glyph.WithCoverage(this._gamma);
    }

    /// <summary>Draws a run of glyphs of the font's face at a size, transformed (as <see cref="RasterizeRun"/> says), without the gamma.</summary>
    protected abstract RasterGlyph DrawRun(
        float size,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        Matrix2x2 transform);

    /// <summary>Gets the pixels a glyph covers, moved by its offset, with the pen at the origin.</summary>
    protected abstract RECT MeasureRun(float size, ushort glyph, DWRITE_GLYPH_OFFSET offset, Matrix2x2 transform);

    public void Dispose()
    {
        this._italic?.Dispose();
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
    /// Gets whether two faces are of the same font (the same file and index, so the same glyphs), whatever their
    /// simulations and axis values. A family's styles often have the same glyph count, so the files are compared.
    /// </summary>
    private static bool SameFont(IDWriteFontFace5* a, IDWriteFontFace5* b)
    {
        if (a == b)
            return true;
        if (a->GetIndex() != b->GetIndex() || a->GetGlyphCount() != b->GetGlyphCount())
            return false;

        using var fileA = FontFileKey.Of(a);
        using var fileB = FontFileKey.Of(b);
        return fileA.Loader is not null && fileA.Loader == fileB.Loader && fileA.Key.SequenceEqual(fileB.Key);
    }
}
