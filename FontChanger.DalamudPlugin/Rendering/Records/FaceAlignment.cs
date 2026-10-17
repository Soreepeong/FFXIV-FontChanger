using FontChanger.Presets.Enums;

namespace FontChanger.DalamudPlugin.Rendering.Records;

/// <summary>Where a face's line is at a size, for elements aligned to it.</summary>
internal readonly record struct FaceAlignment(int Ascent, int LineHeight, float RomanBaselineY, float IdeographicCenterY, VerticalAlignment Mode);
