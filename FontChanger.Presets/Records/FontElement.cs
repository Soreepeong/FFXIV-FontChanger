using System.Collections.Generic;
using System.Text;

using FontChanger.Presets.Enums;
using FontChanger.Presets.Utilities;

namespace FontChanger.Presets.Records;

public sealed record FontElement
{
    public float Size { get; init; }

    public float Gamma { get; init; } = 1;

    public CodepointMergeMode MergeMode { get; init; }

    public ElementSource Source { get; init; }

    public LookupStruct Lookup { get; init; } = LookupStruct.Of(string.Empty);

    public IReadOnlyList<RuneRange> Ranges { get; init; } = [];

    public IReadOnlyDictionary<Rune, Rune> Replacements { get; init; } = new Dictionary<Rune, Rune>();

    public float LetterSpacing { get; init; }

    public float HorizontalOffset { get; init; }

    public float VerticalOffset { get; init; }

    public Monospacing? Monospacing { get; init; }

    public Matrix2x2 Transform { get; init; } = Matrix2x2.Identity;

    public float EmptyAscent { get; init; }

    public float EmptyLineHeight { get; init; }

    public DirectWriteParams DirectWrite { get; init; } = DirectWriteParams.Default;

    public FreeTypeParams FreeType { get; init; } = FreeTypeParams.Default;

    public GlyphMergingStruct? GlyphMerging { get; init; }

    public GlyphImagesStruct? GlyphImages { get; init; }

    public bool Contains(Rune rune) => this.Ranges.Contains(rune);

    public FontElement Scaled(CodepointMergeMode mergeMode, float factor) => this with
    {
        Size = this.Size * factor,
        MergeMode = mergeMode,
        LetterSpacing = this.LetterSpacing * factor,
        HorizontalOffset = this.HorizontalOffset * factor,
        VerticalOffset = this.VerticalOffset * factor,
        Monospacing = this.Monospacing is { Unit: MonospacingUnit.Pixels }
                          ? this.Monospacing with { Min = this.Monospacing.Min * factor, Max = this.Monospacing.Max * factor }
                          : this.Monospacing,
        EmptyAscent = this.EmptyAscent * factor,
        EmptyLineHeight = this.EmptyLineHeight * factor,
        GlyphMerging = this.GlyphMerging is { } g
                           ? g with
                           {
                               TextSize = g.TextSize * factor,
                               TextOffsetX = g.TextOffsetX * factor,
                               TextOffsetY = g.TextOffsetY * factor,
                               LetterSpacing = g.LetterSpacing * factor,
                               LineSpacing = g.LineSpacing * factor,
                           }
                           : null,
    };
}
