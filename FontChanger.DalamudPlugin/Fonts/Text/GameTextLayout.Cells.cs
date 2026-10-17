using System;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Faces;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Text;

internal sealed unsafe partial class GameTextLayout
{
    /// <summary>
    /// The game has no left bearing, so a cell with ink left of its pen starts that much earlier (but not before the previous
    /// cell) and the previous cell advances that much less, keeping the line's width and the ink where shaping put it.
    /// </summary>
    private void PlaceClusters(byte* bytes, Pointer<AtkFontManager.Glyph>[] glyphs)
    {
        var count = this._clusters.Count;
        var starts = count <= 256 ? stackalloc int[count] : new int[count];
        for (var i = 0; i < count; i++)
        {
            var c = this._clusters[i];
            starts[i] = c.X0;
            if (c.Missing)
                continue;

            // A cluster after one left to the per-character path can't move into it.
            var raster = this.GetRaster(c);
            if (i > 0 && !this._clusters[i - 1].Missing && raster.Width > 0 && raster.Left < 0)
                starts[i] = Math.Max(c.X0 + raster.Left, starts[i - 1]);
        }

        for (var i = 0; i < count; i++)
        {
            var c = this._clusters[i];

            // A ligature's text elements share its advance evenly, so that the caret and selection can stop inside it.
            this._elementStarts.Clear();
            for (var t = c.TextStart; t < c.TextEnd; t += this.NextGrapheme(t, c.TextEnd))
                this._elementStarts.Add(t);
            var elements = this._elementStarts.Count;

            var cell = default(Pointer<AtkFontManager.Glyph>);
            var advance = 0;
            if (!c.Missing)
            {
                var end = i + 1 < count && !this._clusters[i + 1].Missing ? starts[i + 1] : c.X1;
                advance = end - starts[i];
                cell = this.GetCell(c, bytes, c.X0 - starts[i], Share(advance, elements, 0));
            }

            var element = 0;
            for (var t = c.TextStart; t < c.TextEnd; t++)
            {
                var startsElement = element + 1 < elements && this._elementStarts[element + 1] == t;
                if (startsElement)
                    element++;

                var b = this._textToByte[t];
                if (b < 0)
                    continue;
                glyphs[b] = cell.IsNull ? default
                            : t == c.TextStart ? cell
                            : startsElement ? this.GetSpacer(Share(advance, elements, element))
                            : this._emptyGlyph;
            }
        }

        static int Share(int advance, int parts, int part) => (advance * (part + 1) / parts) - (advance * part / parts);
    }

    private Pointer<AtkFontManager.Glyph> GetSpacer(int advance)
    {
        var sized = this._shapingSized!;
        var key = (sized, advance);
        if (!this._spacers.TryGetValue(key, out var cell))
        {
            cell = this._replacer.PlaceCell(sized, new(advance, 0, 0, 0, 0, []), 0);
            this._spacers.Add(key, cell);
        }

        return cell;
    }

    /// <summary>Relative to the pen; the advance is final only for a monospaced cluster, as <see cref="GetCell"/> sets the others'.</summary>
    private RasterGlyph GetRaster(Cluster c)
    {
        var sized = this._shapingSized!;
        var key = c.RasterKey ??= new(sized, c);
        if (this._rasters.TryGetValue(key, out var raster))
            return raster;

        var glyphSource = c.GlyphSource!;
        var run = new GlyphRun(c.GlyphIds, c.Advances, c.Offsets);
        raster = c.Source!.Rasterize(c.Px, run, new(c.Origin, c.RawAdvance)) ?? new(c.RawAdvance, 0, 0, 0, 0, []);
        raster = glyphSource.Element!.Wrap(raster, sized.Px, glyphSource.Font.GetAlignment(sized.Px, glyphSource.Game), c.Source, run, c.Origin);

        // The edge margin puts ink left of the pen, so the cell starts earlier (PlaceClusters), keeping the spacing.
        raster = this._replacer.FinishGlyph(sized, raster);
        if (this._rasters.Count >= MaxCachedRasters)
            this._rasters.Clear();
        this._rasters.Add(key, raster);
        return raster;
    }

    /// <summary>The cell's box starts <paramref name="pad"/> pixels left of the cluster's pen.</summary>
    private Pointer<AtkFontManager.Glyph> GetCell(Cluster c, byte* bytes, int pad, int advance)
    {
        var sized = this._shapingSized!;
        var raster = this.GetRaster(c);
        var key = (c.RasterKey!, sized, pad, advance);
        if (this._cells.TryGetValue(key, out var cell))
            return cell;

        raster = raster with { Advance = advance, Left = raster.Left + pad };

        // A cell that couldn't move left is drawn right of where shaping put its margin, so what follows moves along to
        // keep its distance to the ink.
        if (raster.Width != 0 && raster.Left < 0)
            raster = raster with { Advance = raster.Advance + Math.Min(-raster.Left, this._replacer.GetEdgeMargin(sized)) };
        var b = this._textToByte[c.TextStart];
        var utf8 = b < 0 ? 0u : GameUtf8.Pack(bytes + b);
        cell = this._replacer.PlaceCell(sized, raster, utf8);
        if (c.RealItalic)
            this._replacer.MarkRealItalic(cell);
        this._cells.Add(key, cell);
        return cell;
    }

    /// <summary>Compares glyphs by value.</summary>
    private sealed class RasterKey(SizedFont sized, Cluster c) : IEquatable<RasterKey>
    {
        private readonly MaterializedFont _font = sized.Font;
        private readonly float _px = sized.Px;
        private readonly MaterializedFontElement? _element = c.GlyphSource?.Element;
        private readonly IFaceSource? _source = c.Source;
        private readonly float _emSize = c.Px;
        private readonly float _origin = c.Origin;
        private readonly int _rawAdvance = c.RawAdvance;
        private readonly uint[] _glyphs = c.GlyphIds;
        private readonly float[] _advances = c.Advances;
        private readonly GlyphOffset[]? _offsets = c.Offsets;

        public bool Equals(RasterKey? other) =>
            ReferenceEquals(this, other) ||
            (other is not null &&
             this._font == other._font &&
             this._element == other._element &&
             this._source == other._source &&
             this._px.Equals(other._px) &&
             this._emSize.Equals(other._emSize) &&
             this._origin.Equals(other._origin) &&
             this._rawAdvance == other._rawAdvance &&
             this._glyphs.AsSpan().SequenceEqual(other._glyphs) &&
             this._advances.AsSpan().SequenceEqual(other._advances) &&
             MemoryMarshal.AsBytes(this._offsets.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(other._offsets.AsSpan())));

        public override bool Equals(object? obj) => this.Equals(obj as RasterKey);

        public override int GetHashCode()
        {
            var hash = default(HashCode);
            hash.Add(this._font);
            hash.Add(this._px);
            hash.Add(this._element);
            hash.Add(this._source);
            hash.Add(this._emSize);
            hash.Add(this._origin);
            hash.Add(this._rawAdvance);
            hash.AddBytes(MemoryMarshal.AsBytes(this._glyphs.AsSpan()));
            hash.AddBytes(MemoryMarshal.AsBytes(this._advances.AsSpan()));
            return hash.ToHashCode();
        }
    }
}
