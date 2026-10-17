using FontChanger.Presets.Enums;

namespace FontChanger.Presets.Records;

public sealed record GlyphMergeMapping(
    GlyphMergeShape Shape,
    GlyphMergeTextMode TextMode,
    string CustomPath,
    string CustomSvg,
    float CustomAdvance,
    (float X1, float Y1, float X2, float Y2)? CustomTextArea);
