namespace FontChanger.Presets.Records;

public readonly record struct GlyphBox(
    float LeftSideBearing,
    float RightSideBearing,
    float AdvanceWidth,
    float BoundingBoxTop,
    float BoundingBoxBottom);
