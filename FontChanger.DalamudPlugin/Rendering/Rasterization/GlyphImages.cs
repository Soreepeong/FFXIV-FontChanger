using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

namespace FontChanger.DalamudPlugin.Rendering.Rasterization;

/// <summary>
/// The glyphs of a glyph images element (FontChanger GlyphFiles, xivres image_fixed_size_font): SVG documents and bitmaps
/// by codepoint, with font.json's units, line metrics and kerning.
/// </summary>
/// <remarks>
/// SVG documents are in units of <c>UnitsPerEm</c> per em, with y growing downwards and the baseline at y =
/// <c>BaselineY</c>, unless their root's data-xivfont-units-per-em and data-xivfont-baseline say otherwise (and the
/// element doesn't set them). The origin is at x = 0, and the advance is data-xivfont-advance, the viewBox's width, or
/// the width. Bitmaps are in pixels at the size they were exported at, with the baseline at its ascent.
/// </remarks>
internal sealed class GlyphImages
{
    private const float DefaultUnitsPerEm = 1000;
    private const float DefaultBaselineY = 880;

    private readonly Dictionary<Rune, Source> _glyphs;
    private readonly float _unitsPerEm;
    private readonly float _baselineY;
    private readonly bool _unitsPerEmOverrides;
    private readonly bool _baselineYOverrides;
    private readonly float? _bitmapUnitsPerEm;
    private readonly float? _bitmapBaselineY;
    private readonly ImageCoverageMode _coverageMode;
    private readonly Dictionary<(Rune, Rune), float> _kerning = [];

    private GlyphImages(GlyphImagesStruct def, Dictionary<Rune, Source> glyphs, JsonElement? metadata)
    {
        this._glyphs = glyphs;
        this._coverageMode = def.BitmapCoverage;

        var metadataUnitsPerEm = metadata.GetPropertyNumberOrDefault("unitsPerEm", DefaultUnitsPerEm);
        this._unitsPerEm = def.UnitsPerEm ?? metadataUnitsPerEm;
        this._baselineY = def.BaselineY ?? metadata.GetPropertyNumberOrDefault("baselineY", DefaultBaselineY);
        this._unitsPerEmOverrides = def.UnitsPerEm is not null;
        this._baselineYOverrides = def.BaselineY is not null;
        var fromMetadata = this._unitsPerEm / metadataUnitsPerEm;
        this.Ascent = def.Ascent ?? metadata.GetPropertyNumberOrDefault("ascent") * fromMetadata ?? this._baselineY;
        this.LineHeight = def.LineHeight ?? metadata.GetPropertyNumberOrDefault("lineHeight") * fromMetadata ?? this._unitsPerEm;

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

    public float Ascent { get; }

    public float LineHeight { get; }

    public IEnumerable<Rune> Codepoints => this._glyphs.Keys;

    /// <summary>Loads the files of an element, from its folder or the preset. Files that can't be read are left out.</summary>
    public static GlyphImages Load(GlyphImagesStruct def, ImageRenderer? renderer)
    {
        var glyphs = new Dictionary<Rune, Source>();
        JsonElement? metadata = def.EmbeddedMetadata;
        if (def.Embedded.Count != 0)
        {
            foreach (var (rune, (svg, png)) in def.Embedded)
            {
                try
                {
                    if (svg is not null)
                        glyphs[rune] = new(svg, ReadSvgMetrics(svg), null, 0, 0);
                    else if (png is not null && renderer is not null)
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
                    if (extension == ".svg")
                    {
                        var svg = File.ReadAllText(file);
                        glyphs[rune] = new(svg, ReadSvgMetrics(svg) ?? throw new InvalidDataException("Not an SVG document."), null, 0, 0);
                    }
                    else if (renderer is not null)
                    {
                        glyphs[rune] = Bitmap(renderer, File.ReadAllBytes(file));
                    }
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

        return new(def, glyphs, metadata);
    }

    public bool Has(Rune rune) => this._glyphs.ContainsKey(rune);

    public int GetKerning(Rune left, Rune right, float px, Matrix2x2 transform) =>
        this._kerning.TryGetValue((left, right), out var k) ? (int)Rounding.Round((k * px * transform.M11) / this._unitsPerEm) : 0;

    /// <summary>Gets the line metrics at a size, scaled vertically as the glyphs are.</summary>
    public (int Ascent, int LineHeight) GetLineMetrics(float px, Matrix2x2 transform)
    {
        var scale = px / this._unitsPerEm * MathF.Abs(transform.M22);
        return ((int)Rounding.Round(this.Ascent * scale), (int)Rounding.Round(this.LineHeight * scale));
    }

    public RasterGlyph? Rasterize(ImageRenderer renderer, Rune rune, float px, Matrix2x2 transform)
    {
        if (!this._glyphs.TryGetValue(rune, out var source))
            return null;

        try
        {
            float unitsPerEm, baselineY, advance, x1, y1, x2, y2;
            if (source.Svg is not null)
            {
                var metrics = source.SvgMetrics ?? throw new InvalidDataException("Not an SVG document.");
                unitsPerEm = this._unitsPerEmOverrides ? this._unitsPerEm : metrics.UnitsPerEm ?? this._unitsPerEm;
                baselineY = this._baselineYOverrides ? this._baselineY : metrics.BaselineY ?? this._baselineY;
                advance = metrics.Advance ?? unitsPerEm;

                // Drawings may go past the line box: this much room is left around it, and the ink is trimmed afterwards.
                (x1, y1, x2, y2) = (-unitsPerEm, baselineY - (2 * unitsPerEm), Math.Max(advance, 0) + unitsPerEm, baselineY + unitsPerEm);
            }
            else
            {
                unitsPerEm = this._bitmapUnitsPerEm ?? px;
                baselineY = this._bitmapBaselineY ?? Rounding.Round(unitsPerEm * DefaultBaselineY / DefaultUnitsPerEm);
                advance = source.Advance ?? source.Width;
                (x1, y1) = (-source.OriginX, -source.OriginY);
                (x2, y2) = (x1 + source.Width, y1 + source.Height);
            }

            if (!(unitsPerEm > 0))
                return null;

            // Units to pixels relative to the pen on the baseline: scaled, the baseline moved to the origin, transformed.
            var s = px / unitsPerEm;
            Span<float> m =
            [
                transform.M11 * s, transform.M12 * s, -transform.M12 * s * baselineY,
                transform.M21 * s, transform.M22 * s, -transform.M22 * s * baselineY,
            ];
            var advancePx = (int)Rounding.Round(advance * s * transform.M11);

            // The pixels the rectangle covers, with one to spare for antialiasing.
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var (x, y) in (ReadOnlySpan<(float, float)>)[(x1, y1), (x2, y1), (x1, y2), (x2, y2)])
            {
                var px1 = (m[0] * x) + (m[1] * y) + m[2];
                var py1 = (m[3] * x) + (m[4] * y) + m[5];
                (minX, maxX, minY, maxY) = (Math.Min(minX, px1), Math.Max(maxX, px1), Math.Min(minY, py1), Math.Max(maxY, py1));
            }

            var left = (int)MathF.Floor(minX) - 1;
            var top = (int)MathF.Floor(minY) - 1;
            var width = (int)MathF.Ceiling(maxX) + 1 - left;
            var height = (int)MathF.Ceiling(maxY) + 1 - top;
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                return new RasterGlyph(advancePx, 0, 0, 0, 0, []);
            m[2] -= left;
            m[5] -= top;

            byte[] coverage;
            if (source.Svg is not null)
            {
                coverage = renderer.DrawSvg(source.Svg, m, width, height);
            }
            else
            {
                // Pixel art keeps its pixels when it is only moved, or scaled by whole numbers.
                var whole = transform is {M12: 0, M21: 0 }
                            && MathF.Abs(m[0] - Rounding.Round(m[0])) < 1e-4f
                            && MathF.Abs(m[4] - Rounding.Round(m[4])) < 1e-4f;
                coverage = renderer.DrawCoverage(this.GetBitmapCoverage(source), source.Width, source.Height, (x1, y1, x2, y2), m, width, height, whole);
            }

            return new RasterGlyph(advancePx, left, top, width, height, coverage).Trimmed();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Glyph image U+{rune:X4} can't be drawn", rune);
            return new RasterGlyph(0, 0, 0, 0, 0, []);
        }
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

    /// <summary>Gets a bitmap's coverage, by its alpha, darkness or brightness.</summary>
    private byte[] GetBitmapCoverage(Source source)
    {
        var pixels = source.Pixels!;
        var mode = this._coverageMode;
        if (mode == ImageCoverageMode.Auto)
            mode = pixels.Any(p => p >> 24 != 0xFF) ? ImageCoverageMode.Alpha : ImageCoverageMode.Darkness;

        var coverage = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            int b = (byte)p, g = (byte)(p >> 8), r = (byte)(p >> 16), a = (byte)(p >> 24);
            var luminance = ((2126 * r) + (7152 * g) + (722 * b) + 5000) / 10000;
            coverage[i] = mode switch
            {
                ImageCoverageMode.Darkness => (byte)((255 - luminance) * a / 255),
                ImageCoverageMode.Brightness => (byte)(luminance * a / 255),
                _ => (byte)a,
            };
        }

        return coverage;
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
