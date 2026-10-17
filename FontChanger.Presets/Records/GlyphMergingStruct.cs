using System.Collections.Generic;
using System.Text;

using FontChanger.Presets.Enums;
using FontChanger.Presets.Utilities;

namespace FontChanger.Presets.Records;

public sealed record GlyphMergingStruct
{
    public float? TextSize { get; init; }

    public GlyphMergeFitMode FitMode { get; init; }

    public float TextOffsetX { get; init; }

    public float TextOffsetY { get; init; }

    public float LetterSpacing { get; init; }

    public float LineSpacing { get; init; }

    public GlyphMergeLineAlignment LineAlignment { get; init; } = GlyphMergeLineAlignment.Center;

    public Matrix2x2 TextTransform { get; init; } = Matrix2x2.Identity;

    public IReadOnlyDictionary<Rune, (GlyphMergeMapping Mapping, string Text)> Glyphs { get; init; } =
        new Dictionary<Rune, (GlyphMergeMapping, string)>();
}
