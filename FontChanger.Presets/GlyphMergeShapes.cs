using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace FontChanger.Presets;

/// <summary>
/// The built-in shapes of glyph merging and its presets, as FontChanger.FixedSizeFont's data/glyph_merge_shapes.json describes them
/// (xivres reads it too); embedded.
/// </summary>
public static class GlyphMergeShapes
{
    /// <summary>The shapes by their names in presets and in glyph_merge_shapes.json (xivres glyph_merge_shape_names).</summary>
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

    /// <summary>Gets the built-in shapes by name.</summary>
    public static JsonElement Shapes { get; } = Root.GetProperty("shapes");

    /// <summary>
    /// Gets the glyphs of the game's private use area drawn by glyph merging, in groups as the font editor offers them:
    /// the AM/PM marks, the digits and levels, the IME indicators (texts beginning with "_" are of the underlined boxes),
    /// and so on.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(int Codepoint, MergeMapping Mapping, string Text)>> Presets { get; } = LoadPresets();

    /// <summary>Gets the name of a shape.</summary>
    public static string NameOf(MergeShape shape) => Names.First(n => n.Shape == shape).Name;

    /// <summary>Gets the shape of a name; <see cref="MergeShape.Box"/> if it isn't one.</summary>
    public static MergeShape ShapeOf(string name) => Array.Find(Names, n => n.Name == name) is { Name: not null } n ? n.Shape : MergeShape.Box;

    private static JsonElement Load()
    {
        using var stream = typeof(GlyphMergeShapes).Assembly.GetManifestResourceStream("FontChanger.Presets.glyph_merge_shapes.json")
                           ?? throw new InvalidOperationException("The shapes of glyph merging aren't embedded.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static List<IReadOnlyList<(int, MergeMapping, string)>> LoadPresets()
    {
        var res = new List<IReadOnlyList<(int, MergeMapping, string)>>();
        foreach (var group in Root.GetProperty("presets").EnumerateArray())
        {
            var glyphs = new List<(int, MergeMapping, string)>();
            foreach (var run in group.EnumerateArray())
            {
                var mapping = new MergeMapping(ShapeOf(run.GetProperty("shape").GetString()!), MergeTextMode.Subtract, string.Empty, string.Empty, 1000, null);
                var texts = new List<string>();
                if (run.TryGetProperty("numbers", out var numbers))
                {
                    for (var i = numbers[0].GetInt32(); i <= numbers[1].GetInt32(); i++)
                        texts.Add(i.ToString(CultureInfo.InvariantCulture));
                }

                if (run.TryGetProperty("texts", out var more))
                    texts.AddRange(more.EnumerateArray().Select(t => t.GetString()!));

                var first = int.Parse(run.GetProperty("first").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                glyphs.AddRange(texts.Select((text, i) => (first + i, mapping, text)));
            }

            res.Add(glyphs);
        }

        return res;
    }
}
