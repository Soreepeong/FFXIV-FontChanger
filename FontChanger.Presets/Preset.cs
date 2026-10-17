using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;

namespace FontChanger.Presets;

public sealed partial class Preset
{
    private Preset(Dictionary<string, Font> fonts) => this.Fonts = fonts;

    public IReadOnlyDictionary<string, Font> Fonts { get; }

    public IEnumerable<string> GlyphImageFolders =>
        this.Fonts.Values.SelectMany(f => f.Elements).Select(e => e.GlyphImages?.Folder ?? string.Empty)
            .Where(f => f.Length != 0).Distinct();

    // Of fonts of one name, the first wins, as when reading a preset file.
    public static Preset FromFonts(IEnumerable<Font> fonts)
    {
        var res = new Dictionary<string, Font>(StringComparer.OrdinalIgnoreCase);
        foreach (var font in fonts)
            res.TryAdd(font.Name, font);
        return new(res);
    }

    public static Preset Combine(IEnumerable<Preset> presets)
    {
        var fonts = new Dictionary<string, Font>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in presets)
        {
            foreach (var (name, font) in preset.Fonts)
                fonts[name] = font;
        }

        return new(fonts);
    }

    public static Preset Overlay(IEnumerable<Preset> presets)
    {
        var fonts = new Dictionary<string, Font>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in presets)
        {
            foreach (var (name, top) in preset.Fonts)
            {
                if (!fonts.TryGetValue(name, out var bottom) || bottom.Elements.Count == 0)
                {
                    fonts[name] = top;
                    continue;
                }

                if (top.Elements.Count == 0)
                    continue;

                var bottomSize = bottom.Elements[0].Size;
                var topSize = top.Elements[0].Size;
                var factor = bottomSize > 0 && topSize > 0 ? bottomSize / topSize : 1;

                // The AddNew elements before the top's game font drew over the game's glyphs; as AddAll in reverse, they
                // keep that precedence over the bottom font.
                var leading = top.Elements.TakeWhile(e => e.Source != ElementSource.Game).Count();
                var elements = bottom.Elements
                                     .Concat(top.Elements.Take(leading).Where(e => e.MergeMode == CodepointMergeMode.AddNew).Reverse().Select(e => e.Scaled(CodepointMergeMode.AddAll, factor)))
                                     .Concat(top.Elements.Where((e, i) => i >= leading || e.MergeMode != CodepointMergeMode.AddNew).Select(e => e.Scaled(e.MergeMode, factor)))
                                     .ToList();
                fonts[name] = new() { Name = bottom.Name, Elements = elements, Alignment = bottom.Alignment };
            }
        }

        return new(fonts);
    }

    public static string FamilyOf(string fontName)
    {
        if (GameFonts.TryFind(fontName, out var font))
            return font.Family;
        var underscore = fontName.LastIndexOf('_');
        return underscore <= 0 ? fontName : fontName[..underscore];
    }

    public Preset OnlyFamily(string family) =>
        new(
            this.Fonts.Where(f => string.Equals(FamilyOf(f.Key), family, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase));

    // OpenType pads tags shorter than 4 characters with spaces.
    public static uint Tag<T>(ReadOnlySpan<T> tag) where T : IBinaryInteger<T>
    {
        if (tag.Length > 4)
            throw new ArgumentException("Invalid tag length", nameof(tag));

        var res = tag.Length == 4 ? 0 : 0x20202020u << (8 * tag.Length);
        for (var i = 0; i < tag.Length; i++)
            res |= uint.CreateTruncating(tag[i]) << (8 * i);
        return res;
    }

    public static uint Tag(string tag) => Tag(tag.AsSpan());
}
