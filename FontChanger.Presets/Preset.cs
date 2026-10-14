using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;

using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

namespace FontChanger.Presets;

/// <summary>A FontChanger preset (XivRes.FontGenerator JSON): faces by game font name.</summary>
public sealed class Preset
{
    private Preset(Dictionary<string, Face> faces) => this.Faces = faces;

    public IReadOnlyDictionary<string, Face> Faces { get; }

    public IEnumerable<string> GlyphImageFolders =>
        this.Faces.Values.SelectMany(f => f.Elements).Select(e => e.GlyphImages?.Folder ?? string.Empty)
            .Where(f => f.Length != 0).Distinct();

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
        else if (root.TryGetProperty("fontSets", out var sets) && sets.ValueKind == JsonValueKind.Array)
        {
            foreach (var set in sets.EnumerateArray())
                ReadFontSet(set, faces, directory);
        }

        if (faces.Count == 0)
            throw new InvalidDataException("The preset has no faces.");
        return new(faces);
    }

    public static Preset FromFaces(IEnumerable<Face> faces)
    {
        var res = new Dictionary<string, Face>(StringComparer.OrdinalIgnoreCase);
        foreach (var face in faces)
            res[face.Name] = face;
        return new(res);
    }

    public static Preset Combine(IEnumerable<Preset> presets)
    {
        var faces = new Dictionary<string, Face>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in presets)
        {
            foreach (var (name, face) in preset.Faces)
                faces[name] = face;
        }

        return new(faces);
    }

    public static Preset Overlay(IEnumerable<Preset> presets)
    {
        var faces = new Dictionary<string, Face>(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in presets)
        {
            foreach (var (name, top) in preset.Faces)
            {
                if (!faces.TryGetValue(name, out var bottom) || bottom.Elements.Count == 0)
                {
                    faces[name] = top;
                    continue;
                }

                if (top.Elements.Count == 0)
                    continue;

                var bottomSize = bottom.Elements[0].Size;
                var topSize = top.Elements[0].Size;
                var factor = bottomSize > 0 && topSize > 0 ? bottomSize / topSize : 1;
                var elements = bottom.Elements.ToList();

                var leading = top.Elements.TakeWhile(e => e.Renderer != Renderer.Game).Count();
                for (var i = leading - 1; i >= 0; i--)
                {
                    if (top.Elements[i].MergeMode == MergeMode.AddNew)
                        elements.Add(top.Elements[i].Scaled(MergeMode.AddAll, factor));
                }

                for (var i = 0; i < top.Elements.Count; i++)
                {
                    if (i >= leading || top.Elements[i].MergeMode != MergeMode.AddNew)
                        elements.Add(top.Elements[i].Scaled(top.Elements[i].MergeMode, factor));
                }

                faces[name] = new() { Name = bottom.Name, Elements = elements, Alignment = bottom.Alignment };
            }
        }

        return new(faces);
    }

    public static string FamilyOf(string faceName)
    {
        if (GameFonts.TryFind(faceName, out var font))
            return font.Family;
        var underscore = faceName.LastIndexOf('_');
        return underscore <= 0 ? faceName : faceName[..underscore];
    }

    public Preset OnlyFamily(string family) =>
        new(
            this.Faces.Where(f => string.Equals(FamilyOf(f.Key), family, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase));

    private static void ReadFontSet(JsonElement set, Dictionary<string, Face> faces, string directory)
    {
        if (set.ValueKind != JsonValueKind.Object || !set.TryGetProperty("faces", out var list) ||
            list.ValueKind != JsonValueKind.Array)
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
        if (json.TryGetProperty("elements", out var list) && list.ValueKind == JsonValueKind.Array)
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
        if (json.TryGetProperty("wrapModifiers", out var wrap) && wrap.ValueKind == JsonValueKind.Object)
        {
            if (wrap.TryGetProperty("codepoints", out var codepoints) && codepoints.ValueKind == JsonValueKind.Array)
            {
                foreach (var range in codepoints.EnumerateArray())
                {
                    if (range.ValueKind != JsonValueKind.Array || range.GetArrayLength() == 0)
                        continue;
                    var first = new Rune(range[0].GetInt32());
                    var last = range.GetArrayLength() > 1 ? new(range[1].GetInt32()) : first;
                    ranges.Add(new(first, last));
                }
            }

            if (wrap.TryGetProperty("codepointReplacements", out var map) && map.ValueKind == JsonValueKind.Object)
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
            if (wrap.TryGetProperty("monospacing", out var mono) && mono.ValueKind == JsonValueKind.Object)
                monospacing = TryReadMonospacing(mono, out var parsedMonospacing) ? parsedMonospacing : null;
        }

        float emptyAscent = 0, emptyLineHeight = 0;
        var dw = DirectWriteParams.Default;
        var ft = FreeTypeParams.Default;
        GlyphImagesStruct? images = null;
        if (json.TryGetProperty("renderSpecific", out var specific) && specific.ValueKind == JsonValueKind.Object)
        {
            if (specific.TryGetProperty("glyphImages", out var gi) && gi.ValueKind == JsonValueKind.Object)
                images = ReadGlyphImages(gi, directory);

            if (specific.TryGetProperty("empty", out var empty) && empty.ValueKind == JsonValueKind.Object)
            {
                emptyAscent = empty.GetPropertyNumberOrDefault("ascent", 0);
                emptyLineHeight = empty.GetPropertyNumberOrDefault("lineHeight", 0);
            }

            // FT_LOAD_NO_HINTING, _NO_BITMAP, _FORCE_AUTOHINT, _NO_AUTOHINT; FT_RENDER_MODE_LIGHT by default.
            if (specific.TryGetProperty("freetype", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                var flags = (f.GetPropertyBoolOrDefault("noHinting") ? 0x2 : 0) |
                            (f.GetPropertyBoolOrDefault("noBitmap") ? 0x8 : 0) |
                            (f.GetPropertyBoolOrDefault("forceAutohint") ? 0x20 : 0) |
                            (f.GetPropertyBoolOrDefault("noAutohint") ? 0x8000 : 0);
                ft = new(flags, (int)f.GetPropertyNumberOrDefault("renderMode", ft.RenderMode));
            }

            if (specific.TryGetProperty("directwrite", out var d) && d.ValueKind == JsonValueKind.Object)
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
        if (json.TryGetProperty("glyphMerging", out var m) && m.ValueKind == JsonValueKind.Object &&
            m.TryGetProperty("mappings", out var mappings) && mappings.ValueKind == JsonValueKind.Array &&
            mappings.GetArrayLength() != 0)
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
            if (m.TryGetProperty("customTextArea", out var a) && a.ValueKind == JsonValueKind.Array &&
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
            if (!m.TryGetProperty("texts", out var texts) || texts.ValueKind != JsonValueKind.Array)
                continue;
            for (var i = 0; i < codepoints.Count && i < texts.GetArrayLength(); i++)
            {
                var text = texts[i].ValueKind == JsonValueKind.String ? texts[i].GetString()! : string.Empty;
                if (text.Length != 0)
                    glyphs.TryAdd(codepoints[i], (mapping, text));
            }
        }

        float? textSize = json.TryGetProperty("textSize", out var size) && size.ValueKind == JsonValueKind.Number
                              ? size.GetSingle()
                              : null;
        var (offsetX, offsetY) = json.TryGetProperty("textOffset", out var o) && o.ValueKind == JsonValueKind.Array &&
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
            TextTransform = json.TryGetProperty("textTransform", out var t) && t.ValueKind == JsonValueKind.Object
                                ? Matrix2x2.FromComponents(
                                    t.GetPropertyNumberOrDefault("scaleX", 1),
                                    t.GetPropertyNumberOrDefault("scaleY", 1),
                                    t.GetPropertyNumberOrDefault("skew", 0),
                                    t.GetPropertyNumberOrDefault("rotation", 0))
                                : Matrix2x2.Identity,
            Glyphs = glyphs,
        };
    }

    private static GlyphImagesStruct ReadGlyphImages(JsonElement json, string directory)
    {
        var embedded = new Dictionary<Rune, (string?, string?)>();
        JsonElement? metadata = null;
        if (json.TryGetProperty("embedded", out var e) && e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("metadata", out var md) && md.ValueKind != JsonValueKind.Null)
                metadata = md.Clone();
            if (e.TryGetProperty("glyphs", out var glyphs) && glyphs.ValueKind == JsonValueKind.Object)
            {
                foreach (var g in glyphs.EnumerateObject())
                {
                    if (!g.Name.StartsWith("U+", StringComparison.Ordinal) ||
                        g.Value.ValueKind != JsonValueKind.Object ||
                        !int.TryParse(
                            g.Name.AsSpan(2),
                            System.Globalization.NumberStyles.HexNumber,
                            null,
                            out var codepoint))
                    {
                        continue;
                    }

                    if (g.Value.TryGetProperty("svg", out var svg) && svg.ValueKind == JsonValueKind.String)
                        embedded[new(codepoint)] = (svg.GetString(), null);
                    else if (g.Value.TryGetProperty("png", out var png) && png.ValueKind == JsonValueKind.String)
                        embedded[new(codepoint)] = (null, png.GetString());
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
        if (json.TryGetProperty("transform", out var t) && t.ValueKind == JsonValueKind.Object)
            return Matrix2x2.FromComponents(
                t.GetPropertyNumberOrDefault("scaleX", 1),
                t.GetPropertyNumberOrDefault("scaleY", 1),
                t.GetPropertyNumberOrDefault("skew", 0),
                t.GetPropertyNumberOrDefault("rotation", 0));

        if (json.TryGetProperty("transformationMatrix", out var m) && m.ValueKind == JsonValueKind.Array &&
            m.GetArrayLength() == 4)
        {
            var v = new float[4];
            for (var i = 0; i < 4; i++)
                v[i] = m[i].ValueKind == JsonValueKind.Number ? m[i].GetSingle() : float.NaN;

            if (v.All(float.IsFinite) && MathF.Abs((v[0] * v[3]) - (v[1] * v[2])) > 1e-6f)
                return Matrix2x2.FromRendererMatrix(renderer, v[0], v[1], v[2], v[3]);
        }

        return Matrix2x2.Identity;
    }

    private static bool TryReadMonospacing(JsonElement json, [NotNullWhen(true)] out Monospacing? monospacing)
    {
        float? min = json.TryGetProperty("min", out var a) && a.ValueKind == JsonValueKind.Number
                         ? a.GetSingle()
                         : null;
        float? max = json.TryGetProperty("max", out var b) && b.ValueKind == JsonValueKind.Number
                         ? b.GetSingle()
                         : null;
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
        if (json.TryGetProperty("features", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in list.EnumerateArray())
            {
                if (f.ValueKind == JsonValueKind.String)
                    features[Tag(f.GetString()!)] = 1;
            }
        }

        if (json.TryGetProperty("featureValues", out var values) && values.ValueKind == JsonValueKind.Object)
        {
            foreach (var v in values.EnumerateObject())
            {
                if (v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetUInt32(out var value))
                    features[Tag(v.Name)] = value;
            }
        }

        var variations = new Dictionary<uint, float>();
        if (json.TryGetProperty("variations", out var axes) && axes.ValueKind == JsonValueKind.Object)
        {
            foreach (var axis in axes.EnumerateObject())
            {
                if (axis.Name.Length == 4 && axis.Value.ValueKind == JsonValueKind.Number)
                    variations[Tag(axis.Name)] = axis.Value.GetSingle();
            }
        }

        bool? allowSynthesis = null;
        if (json.TryGetProperty("synthesis", out var synthesis) && synthesis.ValueKind == JsonValueKind.Object)
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

    public static uint Tag<T>(ReadOnlySpan<T> tag) where T : IBinaryInteger<T> => tag.Length switch
    {
        0 => 0x20202020u,
        1 => 0x20202000u | uint.CreateTruncating(tag[0]),
        2 => 0x20200000u | uint.CreateTruncating(tag[0]) | (uint.CreateTruncating(tag[1]) << 8),
        3 => 0x20000000u | uint.CreateTruncating(tag[0]) | (uint.CreateTruncating(tag[1]) << 8)
             | (uint.CreateTruncating(tag[2]) << 16),
        4 => uint.CreateTruncating(tag[0]) | (uint.CreateTruncating(tag[1]) << 8)
             | (uint.CreateTruncating(tag[2]) << 16) | (uint.CreateTruncating(tag[3]) << 24),
        _ => throw new ArgumentException("Invalid tag length", nameof(tag)),
    };

    public static uint Tag(string tag) => Tag(tag.AsSpan());

    private static bool TryGetFirstRune(ReadOnlySpan<char> s, out Rune rune)
    {
        if (s.Length != 0 && Rune.DecodeFromUtf16(s, out rune, out _) == System.Buffers.OperationStatus.Done)
            return true;

        rune = default;
        return false;
    }

    private static Rune GetFirstRuneOrDefault(ReadOnlySpan<char> s, Rune fallback) => TryGetFirstRune(s, out var rune) ? rune : fallback;
}
