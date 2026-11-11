using System;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Faces;
using FontChanger.DalamudPlugin.Fonts.Glyphs;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Text;

/// <summary>Clusters' rasters, and the cells and spacers made of them.</summary>
internal sealed unsafe class ClusterGlyphCache(GlyphStore store, GlyphFactory factory, FrameState state)
{
    // Keyed by sized font too, as families may share a font but each places cells in its own atlas with its own metrics.
    private readonly AgedMap<(RasterKey Raster, SizedFont Sized, int Pad, int Advance), Pointer<AtkFontManager.Glyph>> _cells = new(state);
    private readonly AgedMap<(SizedFont Sized, int Advance), Pointer<AtkFontManager.Glyph>> _spacers = new(state);

    // Of outline sources only, as bitmaps are only scaled.
    private readonly AgedMap<RasterKey, RasterGlyph> _rasters = new(state);

    public Pointer<AtkFontManager.Glyph> GetSpacer(SizedFont sized, int advance)
    {
        var key = (sized, advance);
        if (this._spacers.TryGetValue(key, out var cell))
            return cell;
        return this._spacers.Add(key, factory.Spacer(sized, advance));
    }

    public int GetEdgeMargin(SizedFont sized) => factory.GetEdgeMargin(sized);

    public RasterKey KeyOf(SizedFont sized, PlacedCluster c) => c.RasterKey ??= new(sized, c);

    /// <summary>Whether the cluster still needs rasterizing, which the shaper may do ahead on the thread pool.</summary>
    public bool NeedsRaster(SizedFont sized, PlacedCluster c)
    {
        if (c.Raster is not null)
            return false;
        if (c.Source!.DrawsBitmaps || !this._rasters.TryGetValue(this.KeyOf(sized, c), out var raster))
            return true;
        c.Raster = raster;
        return false;
    }

    /// <summary>Relative to the pen. The advance is final only for a monospaced cluster, as <see cref="GetCell"/> sets the others'.</summary>
    public RasterGlyph GetRaster(SizedFont sized, PlacedCluster c)
    {
        if (c.Raster is { } known)
            return known;
        if (!c.Source!.DrawsBitmaps && this._rasters.TryGetValue(this.KeyOf(sized, c), out var raster))
            return (c.Raster = raster).Value;
        return this.AddRaster(sized, c, c.Rasterize());
    }

    /// <summary>Places what the source rasterized as the element does, and caches it.</summary>
    public RasterGlyph AddRaster(SizedFont sized, PlacedCluster c, RasterGlyph? raw)
    {
        var glyphSource = c.Pick!;
        var run = new GlyphRun(c.GlyphIds, c.Advances, c.Offsets);
        var raster = raw ?? new(c.RawAdvance, 0, 0, 0, 0, []);
        raster = glyphSource.Element!.Wrap(raster, sized.Px, this.KeyOf(sized, c).Alignment, c.Source!, run, c.Origin);

        // The edge margin puts ink left of the pen, so the cell starts earlier (PlaceClusters), keeping the spacing.
        raster = factory.Finish(sized, raster);
        c.Raster = raster;
        return c.Source!.DrawsBitmaps ? raster : this._rasters.Add(c.RasterKey!, raster);
    }

    /// <summary>The cell's box starts <paramref name="pad"/> pixels left of the cluster's pen.</summary>
    public Pointer<AtkFontManager.Glyph> GetCell(SizedFont sized, PlacedCluster c, byte* bytes, int b, int pad, int advance)
    {
        var raster = this.GetRaster(sized, c);
        var key = (c.RasterKey!, sized, pad, advance);
        if (this._cells.TryGetValue(key, out var cell))
            return cell;

        raster = raster with { Advance = advance, Left = raster.Left + pad };

        // A cell that couldn't move left is drawn right of where shaping put its margin, so what follows moves along to
        // keep its distance to the ink.
        if (GlyphBox.InkLeftOfPen(raster) is var overhang and > 0)
            raster = raster with { Advance = raster.Advance + Math.Min(overhang, factory.GetEdgeMargin(sized)) };
        var utf8 = b < 0 ? 0u : GameUtf8.Pack(bytes + b);
        cell = factory.FromRaster(sized, raster, utf8);
        if (c.RealItalic)
            store.CellOf(cell).RealItalic = true;
        return this._cells.Add(key, cell);
    }

    public void Sweep()
    {
        this._cells.Sweep(store.HeldOnce, store.ReleaseAction);
        this._spacers.Sweep(store.HeldOnce, store.ReleaseAction);
        this._rasters.Sweep(null, null);
    }

    public void Clear()
    {
        this._cells.Clear(store.ReleaseAction);
        this._spacers.Clear(store.ReleaseAction);
        this._rasters.Clear(null);
    }
}

/// <summary>What a cluster's raster is made from, compared by value.</summary>
/// <remarks>
/// <ul>
/// <li>The element implies the font, and the source includes its bold or italic.</li>
/// <li>The edge margin, subpixel order and vertical renderer aren't in it, as changing any makes every glyph again.</li>
/// </ul>
/// </remarks>
internal sealed unsafe class RasterKey(SizedFont sized, PlacedCluster c) : IEquatable<RasterKey>
{
    private readonly float _px = sized.Px;
    private readonly LoadedFontElement? _element = c.Pick?.Element;
    private readonly IFaceSource? _source = c.Source;
    private readonly float _emSize = c.Px;
    private readonly float _origin = c.Origin;
    private readonly int _rawAdvance = c.RawAdvance;
    private readonly uint[] _glyphs = c.GlyphIds;
    private readonly float[] _advances = c.Advances;
    private readonly GlyphOffset[]? _offsets = c.Offsets;
    private int? _hash;

    /// <summary>Gets the alignment of the picked font, whose game font's metrics it may take.</summary>
    public FontAlignment Alignment { get; } = c.Pick is { } g ? g.Font.GetAlignment(sized.Px, g.Game) : default;

    public bool Equals(RasterKey? other) =>
        ReferenceEquals(this, other) ||
        (other is not null &&
         this._element == other._element &&
         this._source == other._source &&
         this._px.Equals(other._px) &&
         this._emSize.Equals(other._emSize) &&
         this._origin.Equals(other._origin) &&
         this._rawAdvance == other._rawAdvance &&
         this.Alignment == other.Alignment &&
         this._glyphs.AsSpan().SequenceEqual(other._glyphs) &&
         this._advances.AsSpan().SequenceEqual(other._advances) &&
         MemoryMarshal.AsBytes(this._offsets.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(other._offsets.AsSpan())));

    public override bool Equals(object? obj) => this.Equals(obj as RasterKey);

    // Looked up several times per cluster.
    public override int GetHashCode() => this._hash ??= this.ComputeHash();

    private int ComputeHash()
    {
        var hash = default(HashCode);
        hash.Add(this._px);
        hash.Add(this._element);
        hash.Add(this._source);
        hash.Add(this._emSize);
        hash.Add(this._origin);
        hash.Add(this._rawAdvance);
        hash.Add(this.Alignment);
        hash.AddBytes(MemoryMarshal.AsBytes(this._glyphs.AsSpan()));
        hash.AddBytes(MemoryMarshal.AsBytes(this._advances.AsSpan()));
        return hash.ToHashCode();
    }
}
