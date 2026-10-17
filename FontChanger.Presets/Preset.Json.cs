using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

namespace FontChanger.Presets;

// Reading a preset's JSON.
public sealed partial class Preset
{
    public static Preset Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(
            stream,
            new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        var faces = new Dictionary<string, Face>(StringComparer.OrdinalIgnoreCase);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        if (root.TryGetProperty("faces", out _))
        {
            ReadFontSet(root, faces, directory);
        }
        else if (root.TryGetArray("fontSets", out var sets))
        {
            foreach (var set in sets.EnumerateArray())
                ReadFontSet(set, faces, directory);
        }

        if (faces.Count == 0)
            throw new InvalidDataException("The preset has no faces.");
        return new(faces);
    }

    private static void ReadFontSet(JsonElement set, Dictionary<string, Face> faces, string directory)
    {
        if (!set.TryGetArray("faces", out var list))
            return;
        foreach (var face in list.EnumerateArray())
        {
            var def = ReadFace(face, directory);
            if (def.Name.Length != 0)
                faces.TryAdd(def.Name, def);
        }
    }

    private static Face ReadFace(JsonElement json, string directory)
    {
        var elements = new List<FaceElement>();
        if (json.TryGetArray("elements", out var list))
        {
            foreach (var e in list.EnumerateArray())
                elements.Add(ReadElement(e, directory));
        }

        return new()
        {
            Name = json.GetPropertyStringOrDefault("name"),
            Elements = elements,
            Alignment = json.GetPropertyStringOrDefault("verticalAlignment") switch
            {
                "top" => VerticalAlignment.Top,
                "middle" => VerticalAlignment.Middle,
                "bottom" => VerticalAlignment.Bottom,
                "romanBaseline" => VerticalAlignment.RomanBaseline,
                "ideographicCenter" => VerticalAlignment.IdeographicCenter,
                _ => VerticalAlignment.Baseline,
            },
        };
    }

    private static FaceElement ReadElement(JsonElement json, string directory)
    {
        var mergeMode = MergeMode.AddNew;
        if (json.TryGetProperty("mergeMode", out var mode) && mode.ValueKind == JsonValueKind.Number)
            mergeMode = mode.GetInt32() switch { 1 => MergeMode.AddAll, 2 => MergeMode.Replace, _ => MergeMode.AddNew };
        else if (json.TryGetProperty("overwrite", out var overwrite) && overwrite.ValueKind is JsonValueKind.True)
            mergeMode = MergeMode.AddAll;

        var ranges = new List<RuneRange>();
        var replacements = new Dictionary<Rune, Rune>();
        float letterSpacing = 0, horizontalOffset = 0, baselineShift = 0;
        Monospacing? monospacing = null;
        if (json.TryGetObject("wrapModifiers", out var wrap))
        {
            // A range that isn't one of codepoints is left out.
            if (wrap.TryGetArray("codepoints", out var codepoints))
            {
                foreach (var range in codepoints.EnumerateArray())
                {
                    if (range.ValueKind != JsonValueKind.Array || range.GetArrayLength() == 0 || !TryReadRune(range[0], out var first))
                        continue;
                    var last = first;
                    if (range.GetArrayLength() > 1 && !TryReadRune(range[1], out last))
                        continue;
                    if (first <= last)
                        ranges.Add(new(first, last));
                }
            }

            if (wrap.TryGetObject("codepointReplacements", out var map))
            {
                foreach (var item in map.EnumerateObject())
                {
                    if (TryGetFirstRune(item.Name, out var from) && item.Value.ValueKind == JsonValueKind.String &&
                        TryGetFirstRune(item.Value.GetString()!, out var to))
                        replacements[from] = to;
                }
            }

            letterSpacing = wrap.GetPropertyNumberOrDefault("letterSpacing", 0);
            horizontalOffset = wrap.GetPropertyNumberOrDefault("horizontalOffset", 0);
            baselineShift = wrap.GetPropertyNumberOrDefault("baselineShift", 0);
            if (wrap.TryGetObject("monospacing", out var mono))
                monospacing = TryReadMonospacing(mono, out var parsedMonospacing) ? parsedMonospacing : null;
        }

        float emptyAscent = 0, emptyLineHeight = 0;
        var dw = DirectWriteParams.Default;
        var ft = FreeTypeParams.Default;
        GlyphImagesStruct? images = null;
        if (json.TryGetObject("renderSpecific", out var specific))
        {
            if (specific.TryGetObject("glyphImages", out var gi))
                images = ReadGlyphImages(gi, directory);

            if (specific.TryGetObject("empty", out var empty))
            {
                emptyAscent = empty.GetPropertyNumberOrDefault("ascent", 0);
                emptyLineHeight = empty.GetPropertyNumberOrDefault("lineHeight", 0);
            }

            // FT_LOAD_NO_HINTING, _NO_BITMAP, _FORCE_AUTOHINT, _NO_AUTOHINT; FT_RENDER_MODE_LIGHT by default.
            if (specific.TryGetObject("freetype", out var f))
            {
                var flags = (f.GetPropertyBoolOrDefault("noHinting") ? 0x2 : 0) |
                            (f.GetPropertyBoolOrDefault("noBitmap") ? 0x8 : 0) |
                            (f.GetPropertyBoolOrDefault("forceAutohint") ? 0x20 : 0) |
                            (f.GetPropertyBoolOrDefault("noAutohint") ? 0x8000 : 0);
                ft = new(flags, (int)f.GetPropertyNumberOrDefault("renderMode", ft.RenderMode));
            }

            if (specific.TryGetObject("directwrite", out var d))
            {
                dw = new(
                    (int)d.GetPropertyNumberOrDefault("renderMode", 0),
                    (int)d.GetPropertyNumberOrDefault("measureMode", 2),
                    (int)d.GetPropertyNumberOrDefault("gridFitMode", 0));
            }
        }

        var renderer = (Renderer)(int)json.GetPropertyNumberOrDefault("renderer", 0);
        var transform = ReadTransform(json, renderer);

        GlyphMergingStruct? merging = null;
        if (json.TryGetObject("glyphMerging", out var m) && m.TryGetArray("mappings", out var mappings) && mappings.GetArrayLength() != 0)
        {
            merging = ReadGlyphMerging(m, mappings);
        }

        return new()
        {
            Size = json.GetPropertyNumberOrDefault("size", 0),
            Gamma = json.GetPropertyNumberOrDefault("gamma", 1),
            MergeMode = mergeMode,
            Renderer = renderer,
            Lookup = json.TryGetProperty("lookup", out var lookup) ? ReadLookup(lookup) : LookupStruct.Of(string.Empty),
            Ranges = ranges,
            Replacements = replacements,
            LetterSpacing = letterSpacing,
            HorizontalOffset = horizontalOffset,
            BaselineShift = baselineShift,
            Monospacing = monospacing,
            Transform = transform,
            EmptyAscent = emptyAscent,
            EmptyLineHeight = emptyLineHeight,
            DirectWrite = dw,
            FreeType = ft,
            GlyphMerging = merging,
            GlyphImages = images,
        };
    }

    private static GlyphMergingStruct ReadGlyphMerging(JsonElement json, JsonElement mappings)
    {
        var glyphs = new Dictionary<Rune, (GlyphMergeMapping, string)>();
        foreach (var m in mappings.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object)
                continue;

            (float, float, float, float)? area = null;
            if (m.TryGetArray("customTextArea", out var a) &&
                a.GetArrayLength() == 4 &&
                a.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number && float.IsFinite(v.GetSingle())) &&
                a[0].GetSingle() < a[2].GetSingle() && a[1].GetSingle() < a[3].GetSingle())
            {
                area = (a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle(), a[3].GetSingle());
            }

            var mapping = new GlyphMergeMapping(
                GlyphMergeShapes.ShapeOf(m.GetPropertyStringOrDefault("shape")),
                m.GetPropertyStringOrDefault("textMode") == "difference"
                    ? MergeTextMode.Difference
                    : MergeTextMode.Subtract,
                m.GetPropertyStringOrDefault("customPath"),
                m.GetPropertyStringOrDefault("customSvg"),
                m.GetPropertyNumberOrDefault("customAdvance", 1000),
                area);

            var codepoints = m.GetPropertyStringOrDefault("codepoints").EnumerateRunes().ToList();
            if (!m.TryGetArray("texts", out var texts))
                continue;
            for (var i = 0; i < codepoints.Count && i < texts.GetArrayLength(); i++)
            {
                var text = texts[i].ValueKind == JsonValueKind.String ? texts[i].GetString()! : string.Empty;
                if (text.Length != 0)
                    glyphs.TryAdd(codepoints[i], (mapping, text));
            }
        }

        var textSize = json.GetPropertyNumberOrDefault("textSize");
        var (offsetX, offsetY) = json.TryGetArray("textOffset", out var o) &&
                                 o.GetArrayLength() == 2 &&
                                 o[0].ValueKind == JsonValueKind.Number && o[1].ValueKind == JsonValueKind.Number
                                     ? (o[0].GetSingle(), o[1].GetSingle())
                                     : (0f, 0f);
        return new()
        {
            TextSize = textSize,
            FitMode = json.GetPropertyStringOrDefault("fitMode") switch
            {
                "shrink" => MergeFitMode.Shrink,
                "overflow" => MergeFitMode.Overflow,
                _ => MergeFitMode.CondenseThenShrink,
            },
            TextOffsetX = offsetX,
            TextOffsetY = offsetY,
            LetterSpacing = json.GetPropertyNumberOrDefault("letterSpacing", 0),
            LineSpacing = json.GetPropertyNumberOrDefault("lineSpacing", 0),
            LineAlignment = json.GetPropertyStringOrDefault("lineAlignment") switch
            {
                "left" => MergeLineAlignment.Left,
                "right" => MergeLineAlignment.Right,
                _ => MergeLineAlignment.Center,
            },
            TextTransform = json.TryGetObject("textTransform", out var t) ? ReadComponents(t) : Matrix2x2.Identity,
            Glyphs = glyphs,
        };
    }

    private static GlyphImagesStruct ReadGlyphImages(JsonElement json, string directory)
    {
        var embedded = new Dictionary<Rune, (string?, string?)>();
        JsonElement? metadata = null;
        if (json.TryGetObject("embedded", out var e))
        {
            if (e.TryGetProperty("metadata", out var md) && md.ValueKind != JsonValueKind.Null)
                metadata = md.Clone();
            if (e.TryGetObject("glyphs", out var glyphs))
            {
                foreach (var g in glyphs.EnumerateObject())
                {
                    if (!g.Name.StartsWith("U+", StringComparison.Ordinal) ||
                        g.Value.ValueKind != JsonValueKind.Object ||
                        !int.TryParse(
                            g.Name.AsSpan(2),
                            System.Globalization.NumberStyles.HexNumber,
                            null,
                            out var codepoint) ||
                        !Rune.TryCreate(codepoint, out var rune))
                    {
                        continue;
                    }

                    if (g.Value.TryGetProperty("svg", out var svg) && svg.ValueKind == JsonValueKind.String)
                        embedded[rune] = (svg.GetString(), null);
                    else if (g.Value.TryGetProperty("png", out var png) && png.ValueKind == JsonValueKind.String)
                        embedded[rune] = (null, png.GetString());
                }
            }
        }

        var folder = json.GetPropertyStringOrDefault("path");
        return new()
        {
            Folder = embedded.Count != 0 || folder.Length == 0
                         ? string.Empty
                         : Path.GetFullPath(Path.Combine(directory, folder)),
            UnitsPerEm = json.GetPropertyNumberOrDefault("unitsPerEm"),
            BaselineY = json.GetPropertyNumberOrDefault("baselineY"),
            Ascent = json.GetPropertyNumberOrDefault("ascent"),
            LineHeight = json.GetPropertyNumberOrDefault("lineHeight"),
            BitmapCoverage = json.GetPropertyStringOrDefault("bitmapCoverage") switch
            {
                "alpha" => ImageCoverageMode.Alpha,
                "darkness" => ImageCoverageMode.Darkness,
                "brightness" => ImageCoverageMode.Brightness,
                _ => ImageCoverageMode.Auto,
            },
            Embedded = embedded,
            EmbeddedMetadata = metadata,
        };
    }

    private static Matrix2x2 ReadTransform(JsonElement json, Renderer renderer)
    {
        if (json.TryGetObject("transform", out var t))
            return ReadComponents(t);

        if (json.TryGetArray("transformationMatrix", out var m) && m.GetArrayLength() == 4)
        {
            var v = new float[4];
            for (var i = 0; i < 4; i++)
                v[i] = m[i].ValueKind == JsonValueKind.Number ? m[i].GetSingle() : float.NaN;

            if (v.All(float.IsFinite) && MathF.Abs((v[0] * v[3]) - (v[1] * v[2])) > 1e-6f)
                return Matrix2x2.FromRendererMatrix(renderer, v[0], v[1], v[2], v[3]);
        }

        return Matrix2x2.Identity;
    }

    /// <summary>Reads a transformation by its components: scales, skew and rotation.</summary>
    private static Matrix2x2 ReadComponents(JsonElement json) => Matrix2x2.FromComponents(
        json.GetPropertyNumberOrDefault("scaleX", 1),
        json.GetPropertyNumberOrDefault("scaleY", 1),
        json.GetPropertyNumberOrDefault("skew", 0),
        json.GetPropertyNumberOrDefault("rotation", 0));

    private static bool TryReadMonospacing(JsonElement json, [NotNullWhen(true)] out Monospacing? monospacing)
    {
        var min = json.GetPropertyNumberOrDefault("min");
        var max = json.GetPropertyNumberOrDefault("max");
        if (min is null && max is null)
        {
            monospacing = null;
            return false;
        }

        monospacing = new(
            min,
            max,
            json.GetPropertyStringOrDefault("unit") switch
            {
                "px" => MonospacingUnit.Pixels,
                "glyph" => MonospacingUnit.ReferenceGlyph,
                _ => MonospacingUnit.Em,
            },
            GetFirstRuneOrDefault(json.GetPropertyStringOrDefault("referenceChar"), new('0')),
            json.GetPropertyStringOrDefault("alignment") switch
            {
                "left" => MonospacingAlignment.Left,
                "centerInk" => MonospacingAlignment.CenterInk,
                "right" => MonospacingAlignment.Right,
                _ => MonospacingAlignment.CenterAdvance,
            },
            json.GetPropertyBoolOrDefault("dropKerning", true));
        return true;
    }

    private static LookupStruct ReadLookup(JsonElement json)
    {
        var features = new Dictionary<uint, uint>();
        if (json.TryGetArray("features", out var list))
        {
            foreach (var f in list.EnumerateArray())
            {
                if (f.ValueKind == JsonValueKind.String)
                    features[Tag(f.GetString()!)] = 1;
            }
        }

        if (json.TryGetObject("featureValues", out var values))
        {
            foreach (var v in values.EnumerateObject())
            {
                if (v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetUInt32(out var value))
                    features[Tag(v.Name)] = value;
            }
        }

        var variations = new Dictionary<uint, float>();
        if (json.TryGetObject("variations", out var axes))
        {
            foreach (var axis in axes.EnumerateObject())
            {
                if (axis.Name.Length == 4 && axis.Value.ValueKind == JsonValueKind.Number)
                    variations[Tag(axis.Name)] = axis.Value.GetSingle();
            }
        }

        bool? allowSynthesis = null;
        if (json.TryGetObject("synthesis", out var synthesis))
            allowSynthesis = synthesis.GetPropertyBoolOrDefault("allow", true);

        return new(
            json.GetPropertyStringOrDefault("name"),
            (int)json.GetPropertyNumberOrDefault("weight", 400),
            (int)json.GetPropertyNumberOrDefault("stretch", 5),
            (int)json.GetPropertyNumberOrDefault("style", 0),
            features)
        {
            LanguageCode = json.GetPropertyStringOrDefault("language"),
            Variations = variations,
            AllowSynthesis = allowSynthesis,
        };
    }

    private static bool TryReadRune(JsonElement json, out Rune rune)
    {
        rune = default;
        return json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var value) && Rune.TryCreate(value, out rune);
    }

    private static bool TryGetFirstRune(ReadOnlySpan<char> s, out Rune rune)
    {
        if (s.Length != 0 && Rune.DecodeFromUtf16(s, out rune, out _) == System.Buffers.OperationStatus.Done)
            return true;

        rune = default;
        return false;
    }

    private static Rune GetFirstRuneOrDefault(ReadOnlySpan<char> s, Rune fallback) => TryGetFirstRune(s, out var rune) ? rune : fallback;
}
