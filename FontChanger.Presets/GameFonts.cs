using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;

using FontChanger.Presets.Records;

namespace FontChanger.Presets;

public static class GameFonts
{
    private static readonly IReadOnlyList<GameFontDataDefinition> All = Load();

    public static bool TryFind(string name, [NotNullWhen(true)] out GameFontDataDefinition? font)
    {
        font = All.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        return font is not null;
    }

    /// <summary>Gets the faces of a family in the first font type that has it (as FontChanger finds game fonts).</summary>
    public static IReadOnlyList<GameFontDataDefinition> FacesOf(string family)
    {
        var first = All.FirstOrDefault(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase));
        return first is null ? [] : All.Where(f => f.FontType == first.FontType && string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static List<GameFontDataDefinition> Load()
    {
        using var stream = typeof(GameFonts).Assembly.GetManifestResourceStream("FontChanger.Presets.game_fonts.json")
                           ?? throw new InvalidOperationException("The game's fonts aren't embedded.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var res = new List<GameFontDataDefinition>();
        foreach (var type in root.GetProperty("fontTypes").EnumerateObject())
        {
            var textures = type.Value.GetProperty("textures").GetString()!;
            foreach (var face in type.Value.GetProperty("faces").EnumerateArray())
            {
                res.Add(new(type.Name, face[0].GetString()!, face[1].GetString()!, face[2].GetSingle(), textures));
            }
        }

        return res;
    }
}
