using FontChanger.Presets.Enums;

namespace FontChanger.DalamudPlugin.Rendering.Faces;

/// <summary>
/// Where a face's line is at a size, for elements aligned to it: its metrics, roman baseline and ideographic center
/// (below the line's top), and how elements are aligned.
/// </summary>
internal readonly record struct FaceAlignment(int Ascent, int LineHeight, float RomanBaselineY, float IdeographicCenterY, VerticalAlignment Mode);
