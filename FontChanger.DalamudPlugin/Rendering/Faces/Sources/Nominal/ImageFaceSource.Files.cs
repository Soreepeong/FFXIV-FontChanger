using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Rendering.Records;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources.Nominal;

// Reading an element's glyph files: SVG documents and bitmaps by codepoint, from its folder or the preset, with
// font.json's units, line metrics and kerning.
internal sealed partial class ImageFaceSource
{
    private ImageFaceSource(FaceElement element, GlyphImagesStruct def, Dictionary<Rune, Source> glyphs, JsonElement? metadata, ImageRenderer renderer)
    {
        this._glyphs = glyphs;
        this._renderer = renderer;
        this._transform = element.Transform;
        this._gamma = RasterGlyph.GammaTable(element.Gamma);
        this._coverageMode = def.BitmapCoverage;

        var metadataUnitsPerEm = metadata.GetPropertyNumberOrDefault("unitsPerEm", DefaultUnitsPerEm);
        this._unitsPerEm = def.UnitsPerEm ?? metadataUnitsPerEm;
        this._baselineY = def.BaselineY ?? metadata.GetPropertyNumberOrDefault("baselineY", DefaultBaselineY);
        this._unitsPerEmOverrides = def.UnitsPerEm is not null;
        this._baselineYOverrides = def.BaselineY is not null;
        var fromMetadata = this._unitsPerEm / metadataUnitsPerEm;
        this._ascent = def.Ascent ?? metadata.GetPropertyNumberOrDefault("ascent") * fromMetadata ?? this._baselineY;
        this._lineHeight = def.LineHeight ?? metadata.GetPropertyNumberOrDefault("lineHeight") * fromMetadata ?? this._unitsPerEm;

        if (metadata is { ValueKind: JsonValueKind.Object } m && m.TryGetProperty("kerning", out var pairs) && pairs.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pairs.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() != 3) continue;
                if (!p[0].TryGetInt32(out var a) || !p[1].TryGetInt32(out var b) || !p[2].TryGetSingle(out var c)) continue;
                this._kerning[(new(a), new(b))] = c * fromMetadata;
            }
        }

        this._bitmapUnitsPerEm = def.UnitsPerEm ?? metadata.GetPropertyNumberOrDefault("exportedSize");
        this._bitmapBaselineY = def.BaselineY ?? metadata.GetPropertyNumberOrDefault("ascentPx");
        if (metadata is { ValueKind: JsonValueKind.Object } md && md.TryGetProperty("glyphs", out var entries) && entries.ValueKind == JsonValueKind.Object)
        {
            foreach (var (rune, source) in this._glyphs.ToList())
            {
                if (source.Pixels is null || !entries.TryGetProperty($"U+{rune:X4}", out var entry) || entry.ValueKind != JsonValueKind.Object)
                    continue;
                var origin = entry.TryGetProperty("pngOrigin", out var o)
                             && o.ValueKind == JsonValueKind.Array
                             && o.GetArrayLength() == 2
                             && o[0].ValueKind == JsonValueKind.Number
                             && o[1].ValueKind == JsonValueKind.Number
                                 ? ((int)o[0].GetSingle(), (int)o[1].GetSingle())
                                 : (0, 0);
                this._glyphs[rune] = source with
                {
                    Advance = entry.GetPropertyNumberOrDefault("advancePx"),
                    OriginX = origin.Item1,
                    OriginY = origin.Item2,
                };
            }
        }
    }

    /// <summary>
    /// Loads the glyph files of an element, from its folder or the preset, drawn with <paramref name="renderer"/>
    /// (which decodes the bitmaps), transformed and with the gamma as the element says. Files that can't be read are
    /// left out.
    /// </summary>
    public static ImageFaceSource Create(FaceElement element, ImageRenderer renderer)
    {
        var def = element.GlyphImages ?? new();
        var glyphs = new Dictionary<Rune, Source>();
        JsonElement? metadata = def.EmbeddedMetadata;
        if (def.Embedded.Count != 0)
        {
            foreach (var (rune, (svg, png)) in def.Embedded)
            {
                try
                {
                    if (svg is not null)
                        glyphs[rune] = Svg(svg);
                    else if (png is not null)
                        glyphs[rune] = Bitmap(renderer, Convert.FromBase64String(png));
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "Embedded glyph U+{rune:X4} can't be read", rune);
                }
            }
        }
        else if (Directory.Exists(def.Folder))
        {
            // SVG files over PNG files of the same codepoint, and files without suffixes over ones with them.
            var files = Directory.EnumerateFiles(def.Folder)
                                 .OrderBy(f => Path.GetExtension(f).Equals(".svg", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                                 .ThenBy(f => Path.GetFileNameWithoutExtension(f).Contains('_'))
                                 .ThenBy(f => Path.GetFileName(f), StringComparer.Ordinal);
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                var extension = Path.GetExtension(file).ToLowerInvariant();
                try
                {
                    if (name.Equals("font.json", StringComparison.OrdinalIgnoreCase))
                    {
                        using var document = JsonDocument.Parse(
                            File.ReadAllBytes(file),
                            new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                        metadata = document.RootElement.Clone();
                        continue;
                    }

                    if (extension is not (".svg" or ".png") || !TryParseFileName(name, out var rune) || glyphs.ContainsKey(rune))
                        continue;
                    glyphs[rune] = extension == ".svg" ? Svg(File.ReadAllText(file)) : Bitmap(renderer, File.ReadAllBytes(file));
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "Glyph file {file} can't be read", file);
                }
            }
        }
        else
        {
            Plugin.Log.Warning("Glyph images folder {folder} isn't there", def.Folder);
        }

        return new(element, def, glyphs, metadata, renderer);
    }

    /// <summary>Gets the character of a glyph file's name: uniXXXX, uXXXXX or u+XXXX, with an optional suffix after an underscore.</summary>
    private static bool TryParseFileName(string name, out Rune rune)
    {
        rune = default;
        var stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        if (stem.IndexOf('_') is var suffix and >= 0)
            stem = stem[..suffix];
        var hex = stem.StartsWith("uni", StringComparison.Ordinal) ? stem[3..]
                  : stem.StartsWith("u+", StringComparison.Ordinal) ? stem[2..]
                  : stem.StartsWith('u') ? stem[1..]
                  : null;
        if (hex is not { Length: >= 4 and <= 6 } || !int.TryParse(hex, NumberStyles.AllowHexSpecifier, null, out var value))
            return false;
        return Rune.TryCreate(value, out rune);
    }

    private static Source Svg(string svg) => new(svg, ReadSvgMetrics(svg) ?? throw new InvalidDataException("Not an SVG document."), null, 0, 0);

    private static Source Bitmap(ImageRenderer renderer, byte[] data)
    {
        var (pixels, width, height) = renderer.Decode(data);
        return new(null, null, pixels, width, height);
    }

    /// <summary>Reads an SVG document's root attributes that decide its metrics; null if it isn't one.</summary>
    private static SvgMetrics? ReadSvgMetrics(string svg)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(svg), new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "svg")
                return null;

            float? unitsPerEm = null, baselineY = null, advance = null, viewBoxWidth = null, width = null;
            for (var more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
            {
                if (reader.Prefix.Length != 0)
                    continue;
                switch (reader.LocalName)
                {
                    case "data-xivfont-units-per-em":
                        unitsPerEm = ParseLength(reader.Value) is > 0 and var u ? u : null;
                        break;
                    case "data-xivfont-baseline":
                        baselineY = ParseLength(reader.Value);
                        break;
                    case "data-xivfont-advance":
                        advance = ParseLength(reader.Value);
                        break;
                    case "viewBox":
                        var numbers = reader.Value.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                        if (numbers.Length == 4 && float.TryParse(numbers[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0)
                            viewBoxWidth = w;
                        break;
                    case "width":
                        width = ParseLength(reader.Value);
                        break;
                }
            }

            return new(unitsPerEm, baselineY, advance ?? viewBoxWidth ?? width);
        }
        catch (XmlException)
        {
            return null;
        }

        // Numbers in pixels or without units; others aren't taken.
        static float? ParseLength(string s)
        {
            s = s.Trim();
            if (s.EndsWith("px", StringComparison.Ordinal))
                s = s[..^2];
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : null;
        }
    }

    /// <summary>
    /// A glyph's file: an SVG document with its root's metrics (null if it isn't one), or a bitmap's straight-alpha BGRA
    /// pixels with its advance in pixels (if not its width) and where the origin on the top of the line is in it.
    /// </summary>
    private sealed record Source(string? Svg, SvgMetrics? SvgMetrics, uint[]? Pixels, int Width, int Height)
    {
        public float? Advance { get; init; }

        public int OriginX { get; init; }

        public int OriginY { get; init; }
    }

    private sealed record SvgMetrics(float? UnitsPerEm, float? BaselineY, float? Advance);
}
