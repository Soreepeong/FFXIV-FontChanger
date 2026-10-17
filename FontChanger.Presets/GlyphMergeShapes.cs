using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;

namespace FontChanger.Presets;

public static class GlyphMergeShapes
{
    private static readonly (GlyphMergeShape Shape, string Name)[] Names =
    [
        (GlyphMergeShape.None, "none"),
        (GlyphMergeShape.AmPm, "amPm"),
        (GlyphMergeShape.Ime, "ime"),
        (GlyphMergeShape.Box, "box"),
        (GlyphMergeShape.NumberBox, "numberBox"),
        (GlyphMergeShape.HollowBox, "hollowBox"),
        (GlyphMergeShape.Hexagon, "hexagon"),
        (GlyphMergeShape.Rhombus, "rhombus"),
        (GlyphMergeShape.Bozja, "bozja"),
        (GlyphMergeShape.Time, "time"),
        (GlyphMergeShape.Star, "star"),
        (GlyphMergeShape.Custom, "custom"),
        (GlyphMergeShape.Glyph, "glyph"),
    ];

    private static readonly JsonElement Root = Load();

    public static JsonElement Shapes { get; } = Root.GetProperty("shapes");

    public static IReadOnlyList<(Rune Codepoint, GlyphMergeMapping Mapping, string Text)> Presets { get; } = LoadPresets();

    public static string NameOf(GlyphMergeShape shape) => Names.First(n => n.Shape == shape).Name;

    public static GlyphMergeShape ShapeOf(string name) => Array.Find(Names, n => n.Name == name) is { Name: not null } n ? n.Shape : GlyphMergeShape.Box;

    private static JsonElement Load()
    {
        using var stream = typeof(GlyphMergeShapes).Assembly.GetManifestResourceStream("FontChanger.Presets.glyph_merge_shapes.json")
                           ?? throw new InvalidOperationException("The shapes of glyph merging aren't embedded.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static List<(Rune, GlyphMergeMapping, string)> LoadPresets()
    {
        var res = new List<(Rune, GlyphMergeMapping, string)>();
        foreach (var run in Root.GetProperty("presets").EnumerateArray().SelectMany(group => group.EnumerateArray()))
        {
            var mapping = new GlyphMergeMapping(ShapeOf(run.GetProperty("shape").GetString()!), GlyphMergeTextMode.Subtract, string.Empty, string.Empty, 1000, null);
            var texts = new List<string>();
            if (run.TryGetProperty("numbers", out var numbers))
            {
                for (var i = numbers[0].GetInt32(); i <= numbers[1].GetInt32(); i++)
                    texts.Add(i.ToString(CultureInfo.InvariantCulture));
            }

            if (run.TryGetProperty("texts", out var more))
                texts.AddRange(more.EnumerateArray().Select(t => t.GetString()!));

            var first = int.Parse(run.GetProperty("first").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            res.AddRange(texts.Select((text, i) => (new Rune(first + i), mapping, text)));
        }

        return res;
    }
}
