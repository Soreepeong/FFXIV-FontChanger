using System.Collections.Generic;
using System.Text;
using System.Text.Json;

using FontChanger.Presets.Enums;

namespace FontChanger.Presets.Records;

public sealed record GlyphImagesStruct
{
    public string Folder { get; init; } = string.Empty;

    public float? UnitsPerEm { get; init; }

    public float? BaselineY { get; init; }

    public float? Ascent { get; init; }

    public float? LineHeight { get; init; }

    public ImageCoverageMode BitmapCoverage { get; init; }

    public IReadOnlyDictionary<Rune, (string? Svg, string? PngBase64)> Embedded { get; init; } =
        new Dictionary<Rune, (string?, string?)>();

    public JsonElement? EmbeddedMetadata { get; init; }
}
