using System;
using System.Collections.Generic;
using System.Globalization;

using FontChanger.DalamudPlugin.Fonts.Faces;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Text;

/// <summary>The run being shaped, reused from run to run.</summary>
internal sealed class ShapingContext
{
    public const int MaxRunBytes = 4096;

    // Cells are made at a quarter-pixel resolution of the cluster's position.
    public const float SubpixelSteps = 4;

    /// <summary>Gets or sets the sized font the run is shaped in.</summary>
    public SizedFont Sized { get; set; } = null!;

    /// <summary>Gets the run's UTF-16 text. Per code unit, a byte offset of -1 (a low surrogate or replaced character) gets no glyph.</summary>
    public char[] Text { get; private set; } = new char[256];

    public int[] TextToByte { get; private set; } = new int[256];

    public GlyphSource?[] TextSource { get; private set; } = new GlyphSource?[256];

    public bool[] TextItalic { get; private set; } = new bool[256];

    /// <summary>Gets, per byte of the run, whether an italic macro has it in italics.</summary>
    public bool[] ByteItalic { get; } = new bool[MaxRunBytes];

    /// <summary>Gets the bytes of characters composed into the one before, with that one's byte.</summary>
    public List<(int Byte, int Owner)> Absorbed { get; } = [];

    public List<Cluster> Clusters { get; } = [];

    public List<TextRun> TextRuns { get; } = [];

    public List<ShapedCluster> Shaped { get; } = [];

    public List<int> ElementStarts { get; } = [];

    public void EnsureCapacity(int n)
    {
        if (n <= this.Text.Length)
            return;
        var size = Math.Max(n, this.Text.Length * 2);
        var text = this.Text;
        var textToByte = this.TextToByte;
        var textSource = this.TextSource;
        var textItalic = this.TextItalic;
        Array.Resize(ref text, size);
        Array.Resize(ref textToByte, size);
        Array.Resize(ref textSource, size);
        Array.Resize(ref textItalic, size);
        (this.Text, this.TextToByte, this.TextSource, this.TextItalic) = (text, textToByte, textSource, textItalic);
    }

    public int NextGrapheme(int t, int end) => StringInfo.GetNextTextElementLength(this.Text.AsSpan(t, end - t));
}

/// <summary>A shaped cluster, placed one after another by advances on screen (with the elements' transformations, emboldening, letter spacing and monospacing).</summary>
internal sealed record Cluster
{
    /// <summary>Gets the source, null if the cluster is left to the per-character path.</summary>
    public IFaceSource? Source { get; init; }

    /// <summary>Gets the element's size the source draws at.</summary>
    public float Px { get; init; }

    public uint[] GlyphIds { get; init; } = [];

    public float[] Advances { get; init; } = [];

    public GlyphOffset[]? Offsets { get; init; }

    /// <summary>Gets a monospaced cluster's own advance in whole pixels, which its cell is made from, or 0 for the others.</summary>
    public int RawAdvance { get; init; }

    /// <summary>Gets or sets the kerning, in pixels on screen towards the next cluster.</summary>
    public float Kerning { get; set; }

    /// <summary>Gets or sets the raster key, made on first use.</summary>
    public RasterKey? RasterKey { get; set; }

    /// <summary>Gets or sets its raster, once made in this shaping.</summary>
    public RasterGlyph? Raster { get; set; }

    /// <summary>Gets the fraction of a pixel the pen is at, in <see cref="ShapingContext.SubpixelSteps"/>.</summary>
    public float Origin { get; init; }

    /// <summary>Gets the whole-pixel pen position the cluster starts at.</summary>
    public int X0 { get; init; }

    /// <summary>Gets or sets the whole-pixel pen position the cluster ends at.</summary>
    public int X1 { get; set; }

    public int TextStart { get; init; }

    public int TextEnd { get; init; }

    /// <summary>Gets the glyph source of the cluster's first character.</summary>
    public GlyphSource? GlyphSource { get; init; }

    public bool Missing => this.Source is null;

    /// <summary>Gets a value indicating whether it is a real italic, which the game must not shear.</summary>
    public bool RealItalic { get; init; }

    public RasterGlyph? Rasterize() => this.Source!.Rasterize(this.Px, new(this.GlyphIds, this.Advances, this.Offsets), new(this.Origin, this.RawAdvance));

    public Cluster MovedBy(float start, float end)
    {
        var pen = this.X0 + this.Origin + start;
        var x0 = Misc.StdRound(pen);
        return this with
        {
            RasterKey = null,
            Raster = null,
            Origin = Misc.StdRound((pen - x0) * ShapingContext.SubpixelSteps) / ShapingContext.SubpixelSteps,
            X0 = (int)x0,
            X1 = (int)Misc.StdRound(this.X1 + end),
        };
    }
}
