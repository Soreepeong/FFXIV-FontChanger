using FontChanger.Presets.Enums;

namespace FontChanger.Presets.Records;

public sealed record GlyphMergeMapping(
    MergeShape Shape,
    MergeTextMode TextMode,
    string CustomPath,
    string CustomSvg,
    float CustomAdvance,
    (float X1, float Y1, float X2, float Y2)? CustomTextArea);
