using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FontChanger.Presets;

/// <summary>A face of the game's fonts (xivres game_fontdata_definition): its FDT file name without the extension, family and size.</summary>
public sealed record GameFontDef(string FontType, string Name, string Family, string Subfamily, float Size);

/// <summary>
/// The game's fonts, as FontChanger.FixedSizeFont's data/game_fonts.json lists them (FontChanger.FixedSizeFont reads it too); embedded.
/// </summary>
public static class GameFonts
{
    private static readonly IReadOnlyList<GameFontDef> All = Load();

    /// <summary>Finds a face of any font type by its name (<c>AXIS_12</c>, <c>AXIS_12_lobby</c>); null if the game has none of the name.</summary>
    public static GameFontDef? Find(string name) => All.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    private static List<GameFontDef> Load()
    {
        using var stream = typeof(GameFonts).Assembly.GetManifestResourceStream("FontChanger.Presets.game_fonts.json")
                           ?? throw new InvalidOperationException("The game's fonts aren't embedded.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var subfamilies = root.GetProperty("families").EnumerateObject().ToDictionary(f => f.Name, f => f.Value.GetProperty("subfamily").GetString()!);
        var res = new List<GameFontDef>();
        foreach (var type in root.GetProperty("fontTypes").EnumerateObject())
        {
            foreach (var face in type.Value.GetProperty("faces").EnumerateArray())
            {
                var family = face[1].GetString()!;
                res.Add(new(type.Name, face[0].GetString()!, family, subfamilies[family], face[2].GetSingle()));
            }
        }

        return res;
    }
}
