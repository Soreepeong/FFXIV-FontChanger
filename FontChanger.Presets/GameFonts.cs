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

    private static List<GameFontDataDefinition> Load()
    {
        using var stream = typeof(GameFonts).Assembly.GetManifestResourceStream("FontChanger.Presets.game_fonts.json")
                           ?? throw new InvalidOperationException("The game's fonts aren't embedded.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var subfamilies = root.GetProperty("families").EnumerateObject().ToDictionary(f => f.Name, f => f.Value.GetProperty("subfamily").GetString()!);
        var res = new List<GameFontDataDefinition>();
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
