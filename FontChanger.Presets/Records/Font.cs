using System.Collections.Generic;

using FontChanger.Presets.Enums;

namespace FontChanger.Presets.Records;

public sealed record Font
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<FontElement> Elements { get; init; } = [];

    public VerticalAlignment Alignment { get; init; } = VerticalAlignment.Baseline;
}
