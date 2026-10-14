using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

using FontChanger.Presets.Enums;
using FontChanger.Presets.Utilities;

namespace FontChanger.Presets.Records;

public sealed record FaceElement
{
    public float Size { get; init; }

    public float Gamma { get; init; } = 1;

    public MergeMode MergeMode { get; init; }

    public Renderer Renderer { get; init; }

    public LookupStruct Lookup { get; init; } = LookupStruct.Of(string.Empty);

    [JsonConverter(typeof(RuneToIntConverter))]
    public IReadOnlyList<RuneRange> Ranges { get; init; } = [];

    [JsonConverter(typeof(RuneToIntConverter))]
    public IReadOnlyDictionary<Rune, Rune> Replacements { get; init; } = new Dictionary<Rune, Rune>();

    public float LetterSpacing { get; init; }

    public float HorizontalOffset { get; init; }

    public float BaselineShift { get; init; }

    public Monospacing? Monospacing { get; init; }

    public Matrix2x2 Transform { get; init; } = Matrix2x2.Identity;

    public float EmptyAscent { get; init; }

    public float EmptyLineHeight { get; init; }

    public DirectWriteParams DirectWrite { get; init; } = DirectWriteParams.Default;

    public FreeTypeParams FreeType { get; init; } = FreeTypeParams.Default;

    public GlyphMergingStruct? GlyphMerging { get; init; }

    public GlyphImagesStruct? GlyphImages { get; init; }

    public bool Contains(Rune rune)
    {
        foreach (var (first, last) in this.Ranges)
        {
            if (first <= rune && rune <= last)
                return true;
        }

        return false;
    }

    public FaceElement Scaled(MergeMode mergeMode, float factor) => new()
    {
        Size = this.Size * factor,
        Gamma = this.Gamma,
        MergeMode = mergeMode,
        Renderer = this.Renderer,
        Lookup = this.Lookup,
        Ranges = this.Ranges,
        Replacements = this.Replacements,
        LetterSpacing = this.LetterSpacing * factor,
        HorizontalOffset = this.HorizontalOffset * factor,
        BaselineShift = this.BaselineShift * factor,
        Monospacing = this.Monospacing is { Unit: MonospacingUnit.Pixels }
                          ? this.Monospacing with { Min = this.Monospacing.Min * factor, Max = this.Monospacing.Max * factor }
                          : this.Monospacing,
        Transform = this.Transform,
        EmptyAscent = this.EmptyAscent * factor,
        EmptyLineHeight = this.EmptyLineHeight * factor,
        DirectWrite = this.DirectWrite,
        FreeType = this.FreeType,
        GlyphMerging = this.GlyphMerging is { } g
                           ? new()
                           {
                               TextSize = g.TextSize * factor,
                               FitMode = g.FitMode,
                               TextOffsetX = g.TextOffsetX * factor,
                               TextOffsetY = g.TextOffsetY * factor,
                               LetterSpacing = g.LetterSpacing * factor,
                               LineSpacing = g.LineSpacing * factor,
                               LineAlignment = g.LineAlignment,
                               TextTransform = g.TextTransform,
                               Glyphs = g.Glyphs,
                           }
                           : null,
        GlyphImages = this.GlyphImages,
    };
}
