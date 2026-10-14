using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;

namespace FontChanger.Presets;

/// <summary>Built-in shapes of glyph merging and its presets.</summary>
public static class GlyphMergeShapes
{
    private static readonly (MergeShape Shape, string Name)[] Names =
    [
        (MergeShape.None, "none"),
        (MergeShape.AmPm, "amPm"),
        (MergeShape.Ime, "ime"),
        (MergeShape.Box, "box"),
        (MergeShape.NumberBox, "numberBox"),
        (MergeShape.HollowBox, "hollowBox"),
        (MergeShape.Hexagon, "hexagon"),
        (MergeShape.Rhombus, "rhombus"),
        (MergeShape.Bozja, "bozja"),
        (MergeShape.Time, "time"),
        (MergeShape.Star, "star"),
        (MergeShape.Custom, "custom"),
        (MergeShape.Glyph, "glyph"),
    ];

    private static readonly JsonElement Root = Load();

    public static JsonElement Shapes { get; } = Root.GetProperty("shapes");

    public static IReadOnlyList<IReadOnlyList<(Rune Codepoint, GlyphMergeMapping Mapping, string Text)>> Presets { get; } = LoadPresets();

    public static string NameOf(MergeShape shape) => Names.First(n => n.Shape == shape).Name;

    public static MergeShape ShapeOf(string name) => Array.Find(Names, n => n.Name == name) is { Name: not null } n2 ? n2.Shape : MergeShape.Box;

    private static JsonElement Load()
    {
        using var stream = typeof(GlyphMergeShapes).Assembly.GetManifestResourceStream("FontChanger.Presets.glyph_merge_shapes.json")
                           ?? throw new InvalidOperationException("The shapes of glyph merging aren't embedded.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static List<IReadOnlyList<(Rune, GlyphMergeMapping, string)>> LoadPresets()
    {
        var res = new List<IReadOnlyList<(Rune, GlyphMergeMapping, string)>>();
        foreach (var group in Root.GetProperty("presets").EnumerateArray())
        {
            var glyphs = new List<(Rune, GlyphMergeMapping, string)>();
            foreach (var run in group.EnumerateArray())
            {
                var mapping = new GlyphMergeMapping(
                    ShapeOf(run.GetProperty("shape").GetString()!),
                    MergeTextMode.Subtract,
                    string.Empty,
                    string.Empty,
                    1000,
                    null);
                var texts = new List<string>();
                if (run.TryGetProperty("numbers", out var numbers))
                {
                    for (var i = numbers[0].GetInt32(); i <= numbers[1].GetInt32(); i++)
                        texts.Add(i.ToString(CultureInfo.InvariantCulture));
                }

                if (run.TryGetProperty("texts", out var more))
                    texts.AddRange(more.EnumerateArray().Select(t => t.GetString()!));

                var first = int.Parse(run.GetProperty("first").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                glyphs.AddRange(texts.Select((text, i) => (new Rune(first + i), mapping, text)));
            }

            res.Add(glyphs);
        }

        return res;
    }
}
