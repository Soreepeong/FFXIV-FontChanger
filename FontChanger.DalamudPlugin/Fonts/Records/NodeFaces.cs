using FontChanger.DalamudPlugin.Fonts.Enums;

namespace FontChanger.DalamudPlugin.Fonts.Records;

internal enum TitleLines : byte
{
    None,
    AfterFirstLine,
    BeforeLastLine,
    AllLines
}

/// <summary><paramref name="OtherLines"/> is the face of the lines marked by <paramref name="Title"/>.</summary>
internal readonly record struct NodeFaces(TextFace Face, TextFace OtherLines = TextFace.Default, TitleLines Title = TitleLines.None);
