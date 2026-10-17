using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Fonts;

internal sealed unsafe class FontProbe : IFontProbe, IDisposable
{
    private readonly HashSet<int> _bitmapSizes = [];
    private readonly ushort _unitsPerEm;
    private MyComPtr<IDWriteFontFace5> _face;

    /// <summary>Takes over the reference to the face.</summary>
    public FontProbe(MyComPtr<IDWriteFontFace5> face)
    {
        this._face = face;
        DWRITE_FONT_METRICS metrics;
        this._face.Value->GetMetrics(&metrics);
        this._unitsPerEm = metrics.designUnitsPerEm;
        this.Ascent = (float)metrics.ascent / metrics.designUnitsPerEm;
        this.LineHeight = (float)(metrics.ascent + metrics.descent + metrics.lineGap) / metrics.designUnitsPerEm;

        // Bitmap sizes are the ppemY of the 48-byte BitmapSize records after the EBLC or CBLC header.
        foreach (var tag in (ReadOnlySpan<uint>)[Preset.Tag("EBLC"u8), Preset.Tag("CBLC"u8)])
        {
            void* data;
            uint size;
            void* context;
            BOOL exists;
            if (this._face.Value->TryGetFontTable(tag, &data, &size, &context, &exists).FAILED || !exists)
                continue;
            var p = (byte*)data;
            if (size >= 8)
            {
                var count = ((uint)p[4] << 24) | ((uint)p[5] << 16) | ((uint)p[6] << 8) | p[7];
                for (var i = 0u; i < count && 8 + (48 * (i + 1)) <= size; i++)
                    this._bitmapSizes.Add(p[8 + (48 * i) + 45]);
            }

            this._face.Value->ReleaseFontTable(context);
        }
    }

    public bool TryGetGlyph(Rune rune, out GlyphBox box)
    {
        ushort glyph;
        DWRITE_GLYPH_METRICS gm;
        if (this._face.Value->GetGlyphIndices((uint*)&rune, 1, &glyph).FAILED || glyph == 0 ||
            this._face.Value->GetDesignGlyphMetrics(&glyph, 1, &gm, BOOL.FALSE).FAILED)
        {
            box = default;
            return false;
        }

        var top = gm.verticalOriginY - gm.topSideBearing;
        var bottom = top - ((int)gm.advanceHeight - gm.topSideBearing - gm.bottomSideBearing);
        float em = this._unitsPerEm;
        box = new(gm.leftSideBearing / em, gm.rightSideBearing / em, gm.advanceWidth / em, top / em, bottom / em);
        return true;
    }

    public float Ascent { get; }

    public float LineHeight { get; }

    public IEnumerable<Rune> Codepoints
    {
        get
        {
            uint count;
            this._face.Value->GetUnicodeRanges(0, null, &count);
            var ranges = new DWRITE_UNICODE_RANGE[count];
            fixed (DWRITE_UNICODE_RANGE* p = ranges)
            {
                if (count == 0 || this._face.Value->GetUnicodeRanges(count, p, &count).FAILED)
                    return [];
            }

            return ranges.SelectMany(static r => Enumerable.Range((int)r.first, (int)(r.last - r.first + 1)))
                         .Where(Rune.IsValid)
                         .Select(static c => new Rune(c))
                         .Where(r => this.TryGetGlyph(r, out _));
        }
    }

    public bool HasBitmapOf(int ppem) => this._bitmapSizes.Contains(ppem);

    public bool PrefersNatural(float ppem)
    {
        // At 96 dpi, so that pixels are DIPs.
        DWRITE_RENDERING_MODE1 mode;
        DWRITE_GRID_FIT_MODE gridFit;
        return this._face.Value->GetRecommendedRenderingMode(
                   ppem,
                   96,
                   96,
                   null,
                   BOOL.FALSE,
                   DWRITE_OUTLINE_THRESHOLD.DWRITE_OUTLINE_THRESHOLD_ANTIALIASED,
                   DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL,
                   null,
                   &mode,
                   &gridFit).SUCCEEDED
               && mode == DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_NATURAL;
    }

    public void Dispose() => this._face.Dispose();
}
