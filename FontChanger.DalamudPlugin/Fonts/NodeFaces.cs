using FontChanger.DalamudPlugin.Fonts.Enums;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary><paramref name="OtherLines"/> is the face of the lines that don't have <paramref name="MainLine"/> (a plate's title).</summary>
internal readonly record struct NodeFaces(TextFace Face, TextFace OtherLines = TextFace.Default, byte[]? MainLine = null);
