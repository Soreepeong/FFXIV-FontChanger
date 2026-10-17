using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using FontChanger.DalamudPlugin.Fonts.Faces.Sources.Outline;
using FontChanger.DalamudPlugin.Fonts.Rasterization;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using FreeTypeSharp;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Fonts.Faces.Sources;

/// <summary>A font's glyphs, shaped by DirectWrite and drawn by DirectWrite or FreeType (xivres directwrite_ and freetype_fixed_size_font).</summary>
internal sealed unsafe partial class OutlineFaceSource : IFaceSource
{
    private readonly byte[]? _gamma;
    private readonly string _fontName;

    // Null when DirectWrite draws; owned by FreeTypeGlyphRasterizer.
    private readonly FT_FaceRec_* _freeTypeFace;

    private HashSet<Rune>? _codepoints;
    private OutlineFaceSource? _italic;
    private bool _italicMade;

    /// <summary>Takes <paramref name="font"/>, which it disposes.</summary>
    private OutlineFaceSource(FontServices services, FontElement def, LookupFont font, FT_FaceRec_* freeTypeFace, string fontName)
    {
        this.Services = services;
        this.Def = def;
        this.Font = font;
        this._freeTypeFace = freeTypeFace;
        this._gamma = RasterGlyph.GammaTable(def.Gamma);
        this._fontName = fontName;
        this.Transform = def.Transform * font.Transform;
    }

    /// <summary>The DWRITE_MEASURING_MODE of the whole font, which text is shaped and drawn with.</summary>
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

    private FontServices Services { get; }

    private FontElement Def { get; }

    private LookupFont Font { get; }

    private Matrix2x2 Transform { get; }

    private DirectWriteParams DirectWriteParams => this.Def.DirectWrite with { MeasureMode = this.MeasureMode };

    public static OutlineFaceSource? Create(FontServices services, FontElement def, string fontName)
    {
        var font = LookupFont.Create(services, def.Lookup, def.Source);
        if (font is null)
        {
            Plugin.Log.Warning("{font}: font {family} not found", fontName, def.Lookup.FamilyName);
            return null;
        }

        return FromFont(services, def, font, fontName);
    }

    public FontProbe CreateProbe() => new(new(this.Font.Face, true));

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
                var font = LookupFont.Create(this.Services, lookup, this.Def.Source);
                if (font is not null && IsRealItalic(font.Face) && !SameFont(font.Face, this.Font.Face))
                {
                    this._italic = FromFont(this.Services, this.Def with { Lookup = lookup }, font, this._fontName);
                    this._italic.MeasureMode = this.MeasureMode;
                }
                else
                {
                    font?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "{font}: the italic of {family} can't be made", this._fontName, this.Def.Lookup.FamilyName);
            }
        }

        return this._italic is not null ? (this._italic, true) : (this, false);
    }

    public RasterGlyph? Rasterize(float px, in GlyphRun run, in RasterizeOptions options = default)
    {
        var count = run.GlyphIds.Length;
        if (count == 0)
            return null;

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

    public void Dispose()
    {
        this._italic?.Dispose();
        this.Font.Dispose();
    }

    /// <summary>Takes <paramref name="font"/>.</summary>
    private static OutlineFaceSource FromFont(FontServices services, FontElement def, LookupFont font, string fontName)
    {
        // DirectWrite doesn't draw fonts with only bitmaps.
        var ftFace = services.FreeType.Open(font.Face);
        var useFreeType = ftFace is not null && (def.Source == ElementSource.FreeType || !FreeTypeGlyphRasterizer.IsScalable(ftFace));
        if (!useFreeType && def.Source == ElementSource.FreeType)
            Plugin.Log.Warning("{family} can't be opened with FreeType; it is drawn with DirectWrite", def.Lookup.FamilyName);
        return new(services, def, font, useFreeType ? ftFace : null, fontName);
    }

    /// <summary>Excludes oblique simulations, which aren't the family's own italic.</summary>
    private static bool IsRealItalic(IDWriteFontFace5* face) =>
        (face->GetSimulations() & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE) == 0 &&
        face->GetStyle() != DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL;

    /// <summary>Compares the files, since a family's styles often have the same glyph count; simulations and axis values are ignored.</summary>
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

    /// <summary>Scales vertically as glyphs transformed by <paramref name="transform"/> are.</summary>
    private (int Ascent, int LineHeight) GetLineMetrics(float px, Matrix2x2 transform)
    {
        var m = this.Font.Metrics;
        var size = px / m.designUnitsPerEm * MathF.Abs(transform.M22);
        var ascent = (int)MathF.Ceiling(m.ascent * size);
        var descent = (int)MathF.Ceiling(m.descent * size);
        return (ascent, ascent + descent + (int)Misc.StdRound(m.lineGap * size));
    }

    // FreeType's emboldening advances each glyph further than DirectWrite lays it out.
    private float GetExtraAdvance(float px) => this._freeTypeFace is not null ? this.Font.Embolden * px : 0;

    /// <summary><paramref name="originX"/> is on screen, after the transformation.</summary>
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

    private RasterGlyph DrawRun(
        float size,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        Matrix2x2 transform) =>
        this._freeTypeFace is not null
            ? this.Services.FreeType.RasterizeRun(
                this._freeTypeFace,
                size,
                glyphs,
                advances,
                offsets,
                count,
                originX,
                advance,
                this.Def.FreeType,
                transform,
                this.Font.GetAxisValues(size),
                this.Font.Embolden)
            : this.Services.DirectWrite.RasterizeRun(
                this.Font.GetFace(size),
                size,
                glyphs,
                advances,
                offsets,
                count,
                originX,
                advance,
                this.DirectWriteParams,
                transform);

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
}
