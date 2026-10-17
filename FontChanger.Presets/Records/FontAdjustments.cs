using FontChanger.Presets.Enums;

namespace FontChanger.Presets.Records;

/// <summary>Sizes are fractions larger than fitted and the rest fractions of the font's size, so they look alike at all sizes
/// of a family.</summary>
public sealed record FontAdjustments
{
    public static readonly FontAdjustments None = new();

    public FontScripts Scripts { get; init; } = FontScripts.All;

    public float NonCjkSize { get; init; }

    public float CjkSize { get; init; }

    /// <summary>Positive moves the glyphs up.</summary>
    public float Raise { get; init; }

    public float NonCjkSpacing { get; init; }

    public float CjkSpacing { get; init; }

    public StrokeWeight StrokeWeight { get; init; } = StrokeWeight.Normal;
}
