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

    // Only the first font type that has the family counts, as FontChanger finds game fonts that way.
    public static IReadOnlyList<GameFontDataDefinition> FontsOf(string family)
    {
        var first = All.FirstOrDefault(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase));
        return first is null ? [] : All.Where(f => f.FontType == first.FontType && string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static List<GameFontDataDefinition> Load()
    {
        using var stream = typeof(GameFonts).Assembly.GetManifestResourceStream("FontChanger.Presets.game_fonts.json")
                           ?? throw new InvalidOperationException("The game's fonts aren't embedded.");
        using var document = JsonDocument.Parse(stream);

        var res = new List<GameFontDataDefinition>();
        foreach (var type in document.RootElement.GetProperty("fontTypes").EnumerateObject())
        {
            var textures = type.Value.GetProperty("textures").GetString()!;
            foreach (var font in type.Value.GetProperty("fonts").EnumerateArray())
                res.Add(new(type.Name, font[0].GetString()!, font[1].GetString()!, font[2].GetSingle(), textures));
        }

        return res;
    }
}
