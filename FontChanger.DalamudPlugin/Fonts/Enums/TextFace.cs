using System;
using System.Collections.Generic;
using System.Linq;

namespace FontChanger.DalamudPlugin.Fonts.Enums;

internal enum TextFace
{
    Default,
    NamePlate,
    NamePlateTitle,
}

/// <summary>A face's name is the settings' key of its sources among the families', which no game font family is named.</summary>
internal static class TextFaceNames
{
    public const string NamePlates = "Nameplates";
    public const string NamePlateTitles = "Nameplate titles";

    public static readonly IReadOnlyList<(TextFace Face, string Name)> Faces =
    [
        (TextFace.NamePlate, NamePlates),
        (TextFace.NamePlateTitle, NamePlateTitles),
    ];

    private static readonly Dictionary<string, TextFace> ByName = Faces.ToDictionary(f => f.Name, f => f.Face, StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<TextFace, string> ByFace = Faces.ToDictionary(f => f.Face, f => f.Name);

    /// <summary>These share an atlas, as a plate's title is drawn with the textures of the font the plate's text picked.</summary>
    public static bool IsNamePlates(TextFace face) => face is TextFace.NamePlate or TextFace.NamePlateTitle;

    /// <summary>Whose fonts a face's text takes for the game fonts the face has none of.</summary>
    public static TextFace FallbackOf(TextFace face) => face == TextFace.NamePlateTitle ? TextFace.NamePlate : TextFace.Default;

    public static string NameOf(TextFace face) => ByFace[face];

    /// <summary>False for a game font family.</summary>
    public static bool TryGetFace(string family, out TextFace face) => ByName.TryGetValue(family, out face);
}
