using FontChanger.Presets.Enums;

namespace FontChanger.DalamudPlugin.Fonts.Records;

internal readonly record struct FontAlignment(int Ascent, int LineHeight, float RomanBaselineY, float IdeographicCenterY, VerticalAlignment Mode);
