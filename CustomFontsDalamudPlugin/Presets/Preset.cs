using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CustomFonts;

/// <summary>How a face element takes codepoints from those before it (xivres codepoint_merge_mode).</summary>
internal enum MergeMode
{
    /// <summary>Codepoints no earlier element has.</summary>
    AddNew = 0,

    /// <summary>Every codepoint it has, over earlier elements.</summary>
    AddAll = 1,

    /// <summary>Only codepoints an earlier element has, which it takes over.</summary>
    Replace = 2,
}

/// <summary>What draws a face element's glyphs (FontChanger RendererEnum).</summary>
internal enum ElementRenderer
{
    /// <summary>No glyphs; line metrics only.</summary>
    Empty = 0,

    /// <summary>The game's own glyphs.</summary>
    Game = 1,

    DirectWrite = 2,

    FreeType = 3,

    /// <summary>SVG and PNG files per glyph.</summary>
    GlyphImages = 4,
}

/// <summary>Where elements' glyphs sit vertically in their face (xivres vertical_alignment).</summary>
internal enum FaceAlignment
{
    Top,
    Middle,
    Baseline,
    Bottom,
    RomanBaseline,
    IdeographicCenter,
}

/// <summary>A font family to look up, with the properties (DWRITE_FONT_WEIGHT, _STRETCH, _STYLE) and OpenType features to use.</summary>
internal sealed record LookupDef(string Name, int Weight, int Stretch, int Style, IReadOnlyDictionary<uint, uint> Features)
{
    private static readonly Dictionary<uint, uint> NoFeatures = [];
    private static readonly Dictionary<uint, float> NoVariations = [];

    /// <summary>Gets the language the text is shaped in (a BCP 47 name), or empty for the text's own.</summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>Gets axis values of variable fonts by tag (as DWRITE_FONT_AXIS_TAG), over the font's instance and synthesis.</summary>
    public IReadOnlyDictionary<uint, float> Variations { get; init; } = NoVariations;

    /// <summary>
    /// Gets whether properties the family lacks are synthesized from its closest real face (FontChanger's explicit
    /// synthesis), or null for presets of earlier versions: the font is drawn as DirectWrite matches it, with its
    /// simulations.
    /// </summary>
    public bool? AllowSynthesis { get; init; }

    /// <summary>Gets a family's regular face (normal weight, stretch and style), without features.</summary>
    public static LookupDef Of(string family) => new(family, 400, 5, 0, NoFeatures);
}

/// <summary>
/// A linear transformation of glyphs on screen, where y grows downwards: x' = M11 x + M12 y, y' = M21 x + M22 y
/// (FontChanger's screen matrix). Advances scale by <see cref="M11"/>, and line metrics by |<see cref="M22"/>|.
/// </summary>
internal readonly record struct GlyphTransform(float M11, float M12, float M21, float M22)
{
    public static readonly GlyphTransform Identity = new(1, 0, 0, 1);

    public bool IsIdentity => this == Identity;

    /// <summary>Gets the transformation that applies <paramref name="first"/>, then this one.</summary>
    public GlyphTransform After(GlyphTransform first) => new(
        (this.M11 * first.M11) + (this.M12 * first.M21),
        (this.M11 * first.M12) + (this.M12 * first.M22),
        (this.M21 * first.M11) + (this.M22 * first.M21),
        (this.M21 * first.M12) + (this.M22 * first.M22));

    /// <summary>Gets the transformation that applies this one, and then scales horizontally by <paramref name="x"/>.</summary>
    public GlyphTransform ScaledX(float x) => this with { M11 = this.M11 * x, M12 = this.M12 * x };

    /// <summary>Makes a transformation from FontChanger's components: rotation * skew * scale.</summary>
    public static GlyphTransform FromComponents(float scaleX, float scaleY, float skewDegrees, float rotationDegrees)
    {
        var c = MathF.Cos(rotationDegrees * MathF.PI / 180);
        var s = MathF.Sin(rotationDegrees * MathF.PI / 180);
        var t = MathF.Tan(skewDegrees * MathF.PI / 180);

        // skew * scale = [[scaleX, -t scaleY], [0, scaleY]], then rotated counterclockwise on screen: [[c, s], [-s, c]].
        var a12 = -t * scaleY;
        return new(c * scaleX, (c * a12) + (s * scaleY), -s * scaleX, (-s * a12) + (c * scaleY));
    }

    /// <summary>Gets the transformation a renderer's matrix (as FontChanger stored them before) makes on screen.</summary>
    public static GlyphTransform FromRendererMatrix(ElementRenderer renderer, float m11, float m12, float m21, float m22) => renderer switch
    {
        // DirectWrite transforms row vectors; FreeType column vectors with y growing upwards.
        ElementRenderer.DirectWrite => new(m11, m21, m12, m22),
        ElementRenderer.GlyphImages => new(m11, m12, m21, m22),
        _ => new(m11, -m12, -m21, m22),
    };
}

/// <summary>How monospacing's widths are measured (xivres monospacing_unit).</summary>
internal enum MonospacingUnit
{
    Pixels,
    Em,

    /// <summary>Multiples of the advance of the reference character.</summary>
    ReferenceGlyph,
}

/// <summary>Where a glyph goes in its monospaced cell (xivres monospacing_alignment).</summary>
internal enum MonospacingAlignment
{
    /// <summary>Keeps the left side bearing.</summary>
    Left,

    /// <summary>Splits the width added or removed evenly between both sides.</summary>
    CenterAdvance,

    /// <summary>Centers the ink in the cell.</summary>
    CenterInk,

    /// <summary>Keeps the right side bearing.</summary>
    Right,
}

/// <summary>Limits of the advances of an element's glyphs, each placed in a cell of the resulting width.</summary>
internal sealed record MonospacingDef(float? Min, float? Max, MonospacingUnit Unit, int ReferenceCharacter, MonospacingAlignment Alignment);

/// <summary>What a merged glyph's text is put in (xivres glyph_merge_shape); the shapes follow the Lodestone web font's.</summary>
internal enum MergeShape
{
    /// <summary>No shape: the text alone, smaller, on the baseline (level digits, units).</summary>
    None,
    AmPm,
    Ime,
    Box,

    /// <summary>A box sized for numbers of one digit, or slightly smaller for more.</summary>
    NumberBox,

    /// <summary>A box outline the text is drawn inside of, instead of cut out of.</summary>
    HollowBox,
    Hexagon,
    Rhombus,
    Bozja,
    Time,

    /// <summary>An SVG path, or a whole SVG document.</summary>
    Custom,

    /// <summary>The element's own glyph of the codepoint.</summary>
    Glyph,
}

/// <summary>How a merged glyph's text combines with its shape.</summary>
internal enum MergeTextMode
{
    /// <summary>Cut out of the shape.</summary>
    Subtract,

    /// <summary>Cut out of the shape, and drawn where it goes past the shape.</summary>
    Difference,
}

/// <summary>How text wider than its shape's area is fitted.</summary>
internal enum MergeFitMode
{
    /// <summary>Narrowed to 70%, then made smaller.</summary>
    CondenseThenShrink,
    Shrink,
    Overflow,
}

internal enum MergeLineAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>
/// A shape of glyph merging, in units of 1/1000 em with y growing downwards and the baseline at y = 880. Custom shapes
/// have an SVG path (<see cref="CustomPath"/>) or a whole document (<see cref="CustomSvg"/>), and an advance; a text
/// area (x1, y1, x2, y2) overrides the shape's own.
/// </summary>
internal sealed record MergeMapping(
    MergeShape Shape,
    MergeTextMode TextMode,
    string CustomPath,
    string CustomSvg,
    float CustomAdvance,
    (float X1, float Y1, float X2, float Y2)? CustomTextArea);

/// <summary>
/// Glyph merging of an element (xivres glyph_merging_fixed_size_font): codepoints drawn as texts in shapes, such as
/// boxed letters, with the element's font. An element with it draws only these codepoints. Pixel values are at the face's
/// size.
/// </summary>
internal sealed class GlyphMergingDef
{
    /// <summary>Gets the texts' size in pixels; null to fit them to their shapes.</summary>
    public float? TextSize { get; init; }

    public MergeFitMode FitMode { get; init; }

    public float TextOffsetX { get; init; }

    public float TextOffsetY { get; init; }

    public float LetterSpacing { get; init; }

    public float LineSpacing { get; init; }

    public MergeLineAlignment LineAlignment { get; init; } = MergeLineAlignment.Center;

    /// <summary>Gets the texts' transformation, after the element's.</summary>
    public GlyphTransform TextTransform { get; init; } = GlyphTransform.Identity;

    /// <summary>Gets each codepoint's shape and text (lines separated by line feeds).</summary>
    public IReadOnlyDictionary<int, (MergeMapping Mapping, string Text)> Glyphs { get; init; } =
        new Dictionary<int, (MergeMapping, string)>();
}

/// <summary>How a bitmap glyph's pixels become coverage.</summary>
internal enum ImageCoverageMode
{
    /// <summary>Alpha, if any pixel isn't opaque; darkness otherwise.</summary>
    Auto,
    Alpha,
    Darkness,
    Brightness,
}

/// <summary>
/// Glyph images of an element: an SVG or PNG file per glyph (named uniXXXX or uXXXXX), from a folder or embedded in the
/// preset, with font.json for their units, line metrics and kerning. Values set here override font.json's.
/// </summary>
internal sealed class GlyphImagesDef
{
    /// <summary>Gets the folder's absolute path, or empty if the files are embedded.</summary>
    public string Folder { get; init; } = string.Empty;

    public float? UnitsPerEm { get; init; }

    public float? BaselineY { get; init; }

    public float? Ascent { get; init; }

    public float? LineHeight { get; init; }

    public ImageCoverageMode BitmapCoverage { get; init; }

    /// <summary>Gets the embedded files: SVG documents, or PNG files as base64.</summary>
    public IReadOnlyDictionary<int, (string? Svg, string? PngBase64)> Embedded { get; init; } =
        new Dictionary<int, (string?, string?)>();

    /// <summary>Gets the embedded font.json, if any.</summary>
    public JsonElement? EmbeddedMetadata { get; init; }
}

/// <summary>DirectWrite rendering parameters of an element; values of the DWRITE_* enums.</summary>
internal readonly record struct DirectWriteParams(int RenderMode, int MeasureMode, int GridFitMode)
{
    public static readonly DirectWriteParams Default = new(5, 0, 0);
}

/// <summary>A face element: one font, the codepoints it draws, and how its glyphs are adjusted.</summary>
internal sealed class ElementDef
{
    /// <summary>Gets the size, in the face's units: an element draws at the drawn size times this over the first element's.</summary>
    public float Size { get; init; }

    public float Gamma { get; init; } = 1;

    public MergeMode MergeMode { get; init; }

    public ElementRenderer Renderer { get; init; }

    public LookupDef Lookup { get; init; } = LookupDef.Of(string.Empty);

    /// <summary>Gets the inclusive codepoint ranges the element may draw; it draws those its font has.</summary>
    public IReadOnlyList<(int First, int Last)> Ranges { get; init; } = [];

    /// <summary>Gets codepoints drawn with the glyph of another codepoint of the same font.</summary>
    public IReadOnlyDictionary<int, int> Replacements { get; init; } = new Dictionary<int, int>();

    /// <summary>Gets the pixels, at the face's size, added to each advance, moving the ink right, and moving it down.</summary>
    public float LetterSpacing { get; init; }

    public float HorizontalOffset { get; init; }

    public float BaselineShift { get; init; }

    /// <summary>Gets the limits of the element's advances, or null.</summary>
    public MonospacingDef? Monospacing { get; init; }

    /// <summary>Gets how the element's glyphs are transformed on screen.</summary>
    public GlyphTransform Transform { get; init; } = GlyphTransform.Identity;

    /// <summary>Gets the line metrics of an <see cref="ElementRenderer.Empty"/> element, in pixels at the face's size.</summary>
    public float EmptyAscent { get; init; }

    public float EmptyLineHeight { get; init; }

    public DirectWriteParams DirectWrite { get; init; } = DirectWriteParams.Default;

    public FreeTypeParams FreeType { get; init; } = FreeTypeParams.Default;

    /// <summary>Gets the element's glyph merging, or null; with it, the element draws only the merged codepoints.</summary>
    public GlyphMergingDef? GlyphMerging { get; init; }

    /// <summary>Gets the glyph files of a <see cref="ElementRenderer.GlyphImages"/> element.</summary>
    public GlyphImagesDef? GlyphImages { get; init; }

    public bool Contains(int codepoint)
    {
        foreach (var (first, last) in this.Ranges)
        {
            if (first <= codepoint && codepoint <= last)
                return true;
        }

        return false;
    }
}

/// <summary>A face of a preset: the elements that make up one game font (such as <c>AXIS_12</c>).</summary>
internal sealed class FaceDef
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<ElementDef> Elements { get; init; } = [];

    public FaceAlignment Alignment { get; init; } = FaceAlignment.Baseline;
}

/// <summary>A FontChanger preset (XivRes.FontGenerator JSON): faces by game font name.</summary>
internal sealed class Preset
{
    private Preset(Dictionary<string, FaceDef> faces) => this.Faces = faces;

    /// <summary>Gets the faces by name (case-insensitive); of a name in several font sets, the first.</summary>
    public IReadOnlyDictionary<string, FaceDef> Faces { get; }

    /// <summary>Gets the folders of glyph images the preset reads (to watch them for changes).</summary>
    public IEnumerable<string> GlyphImageFolders =>
        this.Faces.Values.SelectMany(f => f.Elements).Select(e => e.GlyphImages?.Folder ?? string.Empty).Where(f => f.Length != 0).Distinct();

    /// <summary>Reads a preset: a single font set (<c>faces</c> at the top), or several (<c>fontSets</c>).</summary>
    public static Preset Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream, new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        var faces = new Dictionary<string, FaceDef>(StringComparer.OrdinalIgnoreCase);

        // Relative paths in the preset are from its folder.
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

    private static void ReadFontSet(JsonElement set, Dictionary<string, FaceDef> faces, string directory)
    {
        if (set.ValueKind != JsonValueKind.Object || !set.TryGetProperty("faces", out var list) || list.ValueKind != JsonValueKind.Array)
            return;
        foreach (var face in list.EnumerateArray())
        {
            var def = ReadFace(face, directory);
            if (def.Name.Length != 0)
                faces.TryAdd(def.Name, def);
        }
    }

    private static FaceDef ReadFace(JsonElement json, string directory)
    {
        var elements = new List<ElementDef>();
        if (json.TryGetProperty("elements", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in list.EnumerateArray())
                elements.Add(ReadElement(e, directory));
        }

        return new()
        {
            Name = GetString(json, "name"),
            Elements = elements,
            Alignment = GetString(json, "verticalAlignment") switch
            {
                "top" => FaceAlignment.Top,
                "middle" => FaceAlignment.Middle,
                "bottom" => FaceAlignment.Bottom,
                "romanBaseline" => FaceAlignment.RomanBaseline,
                "ideographicCenter" => FaceAlignment.IdeographicCenter,
                _ => FaceAlignment.Baseline,
            },
        };
    }

    private static ElementDef ReadElement(JsonElement json, string directory)
    {
        var mergeMode = MergeMode.AddNew;
        if (json.TryGetProperty("mergeMode", out var mode) && mode.ValueKind == JsonValueKind.Number)
            mergeMode = mode.GetInt32() switch { 1 => MergeMode.AddAll, 2 => MergeMode.Replace, _ => MergeMode.AddNew };
        else if (json.TryGetProperty("overwrite", out var overwrite) && overwrite.ValueKind is JsonValueKind.True)
            mergeMode = MergeMode.AddAll;

        var ranges = new List<(int, int)>();
        var replacements = new Dictionary<int, int>();
        float letterSpacing = 0, horizontalOffset = 0, baselineShift = 0;
        MonospacingDef? monospacing = null;
        if (json.TryGetProperty("wrapModifiers", out var wrap) && wrap.ValueKind == JsonValueKind.Object)
        {
            if (wrap.TryGetProperty("codepoints", out var codepoints) && codepoints.ValueKind == JsonValueKind.Array)
            {
                foreach (var range in codepoints.EnumerateArray())
                {
                    if (range.ValueKind != JsonValueKind.Array || range.GetArrayLength() == 0)
                        continue;
                    var first = range[0].GetInt32();
                    ranges.Add((first, range.GetArrayLength() > 1 ? range[1].GetInt32() : first));
                }
            }

            if (wrap.TryGetProperty("codepointReplacements", out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in map.EnumerateObject())
                {
                    if (FirstRune(item.Name) is { } from && item.Value.ValueKind == JsonValueKind.String && FirstRune(item.Value.GetString()!) is { } to)
                        replacements[from] = to;
                }
            }

            letterSpacing = GetFloat(wrap, "letterSpacing", 0);
            horizontalOffset = GetFloat(wrap, "horizontalOffset", 0);
            baselineShift = GetFloat(wrap, "baselineShift", 0);
            if (wrap.TryGetProperty("monospacing", out var mono) && mono.ValueKind == JsonValueKind.Object)
                monospacing = ReadMonospacing(mono);
        }

        float emptyAscent = 0, emptyLineHeight = 0;
        var dw = DirectWriteParams.Default;
        var ft = FreeTypeParams.Default;
        GlyphImagesDef? images = null;
        if (json.TryGetProperty("renderSpecific", out var specific) && specific.ValueKind == JsonValueKind.Object)
        {
            if (specific.TryGetProperty("glyphImages", out var gi) && gi.ValueKind == JsonValueKind.Object)
                images = ReadGlyphImages(gi, directory);

            if (specific.TryGetProperty("empty", out var empty) && empty.ValueKind == JsonValueKind.Object)
            {
                emptyAscent = GetFloat(empty, "ascent", 0);
                emptyLineHeight = GetFloat(empty, "lineHeight", 0);
            }

            // FT_LOAD_NO_HINTING, _NO_BITMAP, _FORCE_AUTOHINT, _NO_AUTOHINT; FT_RENDER_MODE_LIGHT by default.
            if (specific.TryGetProperty("freetype", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                var flags = (GetBool(f, "noHinting") ? 0x2 : 0) | (GetBool(f, "noBitmap") ? 0x8 : 0) |
                            (GetBool(f, "forceAutohint") ? 0x20 : 0) | (GetBool(f, "noAutohint") ? 0x8000 : 0);
                ft = new(flags, (int)GetFloat(f, "renderMode", ft.RenderMode));
            }

            if (specific.TryGetProperty("directwrite", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                dw = new(
                    (int)GetFloat(d, "renderMode", 0),
                    (int)GetFloat(d, "measureMode", 2),
                    (int)GetFloat(d, "gridFitMode", 0));
            }
        }

        var renderer = (ElementRenderer)(int)GetFloat(json, "renderer", 0);
        var transform = ReadTransform(json, renderer);

        // Glyph merging is in use when it has any mapping.
        GlyphMergingDef? merging = null;
        if (json.TryGetProperty("glyphMerging", out var m) && m.ValueKind == JsonValueKind.Object &&
            m.TryGetProperty("mappings", out var mappings) && mappings.ValueKind == JsonValueKind.Array && mappings.GetArrayLength() != 0)
        {
            merging = ReadGlyphMerging(m, mappings);
        }

        return new()
        {
            Size = GetFloat(json, "size", 0),
            Gamma = GetFloat(json, "gamma", 1),
            MergeMode = mergeMode,
            Renderer = renderer,
            Lookup = json.TryGetProperty("lookup", out var lookup) ? ReadLookup(lookup) : LookupDef.Of(string.Empty),
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

    private static GlyphMergingDef ReadGlyphMerging(JsonElement json, JsonElement mappings)
    {
        // A codepoint takes the first mapping that gives it a text.
        var glyphs = new Dictionary<int, (MergeMapping, string)>();
        foreach (var m in mappings.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object)
                continue;

            (float, float, float, float)? area = null;
            if (m.TryGetProperty("customTextArea", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() == 4 &&
                a.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number && float.IsFinite(v.GetSingle())) &&
                a[0].GetSingle() < a[2].GetSingle() && a[1].GetSingle() < a[3].GetSingle())
            {
                area = (a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle(), a[3].GetSingle());
            }

            var mapping = new MergeMapping(
                GetString(m, "shape") switch
                {
                    "none" => MergeShape.None,
                    "amPm" => MergeShape.AmPm,
                    "ime" => MergeShape.Ime,
                    "numberBox" => MergeShape.NumberBox,
                    "hollowBox" => MergeShape.HollowBox,
                    "hexagon" => MergeShape.Hexagon,
                    "rhombus" => MergeShape.Rhombus,
                    "bozja" => MergeShape.Bozja,
                    "time" => MergeShape.Time,
                    "custom" => MergeShape.Custom,
                    "glyph" => MergeShape.Glyph,
                    _ => MergeShape.Box,
                },
                GetString(m, "textMode") == "difference" ? MergeTextMode.Difference : MergeTextMode.Subtract,
                GetString(m, "customPath"),
                GetString(m, "customSvg"),
                GetFloat(m, "customAdvance", 1000),
                area);

            var codepoints = GetString(m, "codepoints").EnumerateRunes().Select(r => r.Value).ToList();
            if (!m.TryGetProperty("texts", out var texts) || texts.ValueKind != JsonValueKind.Array)
                continue;
            for (var i = 0; i < codepoints.Count && i < texts.GetArrayLength(); i++)
            {
                var text = texts[i].ValueKind == JsonValueKind.String ? texts[i].GetString()! : string.Empty;
                if (text.Length != 0)
                    glyphs.TryAdd(codepoints[i], (mapping, text));
            }
        }

        float? textSize = json.TryGetProperty("textSize", out var size) && size.ValueKind == JsonValueKind.Number ? size.GetSingle() : null;
        var (offsetX, offsetY) = json.TryGetProperty("textOffset", out var o) && o.ValueKind == JsonValueKind.Array && o.GetArrayLength() == 2 &&
                                 o[0].ValueKind == JsonValueKind.Number && o[1].ValueKind == JsonValueKind.Number
            ? (o[0].GetSingle(), o[1].GetSingle())
            : (0f, 0f);
        return new()
        {
            TextSize = textSize,
            FitMode = GetString(json, "fitMode") switch
            {
                "shrink" => MergeFitMode.Shrink,
                "overflow" => MergeFitMode.Overflow,
                _ => MergeFitMode.CondenseThenShrink,
            },
            TextOffsetX = offsetX,
            TextOffsetY = offsetY,
            LetterSpacing = GetFloat(json, "letterSpacing", 0),
            LineSpacing = GetFloat(json, "lineSpacing", 0),
            LineAlignment = GetString(json, "lineAlignment") switch
            {
                "left" => MergeLineAlignment.Left,
                "right" => MergeLineAlignment.Right,
                _ => MergeLineAlignment.Center,
            },
            TextTransform = json.TryGetProperty("textTransform", out var t) && t.ValueKind == JsonValueKind.Object
                ? GlyphTransform.FromComponents(GetFloat(t, "scaleX", 1), GetFloat(t, "scaleY", 1), GetFloat(t, "skew", 0), GetFloat(t, "rotation", 0))
                : GlyphTransform.Identity,
            Glyphs = glyphs,
        };
    }

    private static GlyphImagesDef ReadGlyphImages(JsonElement json, string directory)
    {
        var embedded = new Dictionary<int, (string?, string?)>();
        JsonElement? metadata = null;
        if (json.TryGetProperty("embedded", out var e) && e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("metadata", out var md) && md.ValueKind != JsonValueKind.Null)
                metadata = md.Clone();
            if (e.TryGetProperty("glyphs", out var glyphs) && glyphs.ValueKind == JsonValueKind.Object)
            {
                foreach (var g in glyphs.EnumerateObject())
                {
                    if (!g.Name.StartsWith("U+", StringComparison.Ordinal) || g.Value.ValueKind != JsonValueKind.Object ||
                        !int.TryParse(g.Name.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var codepoint))
                    {
                        continue;
                    }

                    if (g.Value.TryGetProperty("svg", out var svg) && svg.ValueKind == JsonValueKind.String)
                        embedded[codepoint] = (svg.GetString(), null);
                    else if (g.Value.TryGetProperty("png", out var png) && png.ValueKind == JsonValueKind.String)
                        embedded[codepoint] = (null, png.GetString());
                }
            }
        }

        var folder = GetString(json, "path");
        return new()
        {
            Folder = embedded.Count != 0 || folder.Length == 0 ? string.Empty : Path.GetFullPath(Path.Combine(directory, folder)),
            UnitsPerEm = GetOptionalFloat(json, "unitsPerEm"),
            BaselineY = GetOptionalFloat(json, "baselineY"),
            Ascent = GetOptionalFloat(json, "ascent"),
            LineHeight = GetOptionalFloat(json, "lineHeight"),
            BitmapCoverage = GetString(json, "bitmapCoverage") switch
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

    /// <summary>Reads an element's transformation: its components, or the renderer's matrix earlier versions stored.</summary>
    private static GlyphTransform ReadTransform(JsonElement json, ElementRenderer renderer)
    {
        if (json.TryGetProperty("transform", out var t) && t.ValueKind == JsonValueKind.Object)
            return GlyphTransform.FromComponents(GetFloat(t, "scaleX", 1), GetFloat(t, "scaleY", 1), GetFloat(t, "skew", 0), GetFloat(t, "rotation", 0));

        if (json.TryGetProperty("transformationMatrix", out var m) && m.ValueKind == JsonValueKind.Array && m.GetArrayLength() == 4)
        {
            var v = new float[4];
            for (var i = 0; i < 4; i++)
                v[i] = m[i].ValueKind == JsonValueKind.Number ? m[i].GetSingle() : float.NaN;

            // One that can't be inverted is taken as no transformation, as FontChanger does.
            if (v.All(float.IsFinite) && MathF.Abs((v[0] * v[3]) - (v[1] * v[2])) > 1e-6f)
                return GlyphTransform.FromRendererMatrix(renderer, v[0], v[1], v[2], v[3]);
        }

        return GlyphTransform.Identity;
    }

    private static MonospacingDef? ReadMonospacing(JsonElement json)
    {
        float? min = json.TryGetProperty("min", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetSingle() : null;
        float? max = json.TryGetProperty("max", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetSingle() : null;
        if (min is null && max is null)
            return null;

        return new(
            min,
            max,
            GetString(json, "unit") switch
            {
                "px" => MonospacingUnit.Pixels,
                "glyph" => MonospacingUnit.ReferenceGlyph,
                _ => MonospacingUnit.Em,
            },
            FirstRune(GetString(json, "referenceChar")) ?? '0',
            GetString(json, "alignment") switch
            {
                "left" => MonospacingAlignment.Left,
                "centerInk" => MonospacingAlignment.CenterInk,
                "right" => MonospacingAlignment.Right,
                _ => MonospacingAlignment.CenterAdvance,
            });
    }

    private static LookupDef ReadLookup(JsonElement json)
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

        // Values other than 1, and features turned off (0), are stored apart from the list.
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
            allowSynthesis = !synthesis.TryGetProperty("allow", out var allow) || allow.ValueKind != JsonValueKind.False;

        return new(
            GetString(json, "name"),
            (int)GetFloat(json, "weight", 400),
            (int)GetFloat(json, "stretch", 5),
            (int)GetFloat(json, "style", 0),
            features)
        {
            Language = GetString(json, "language"),
            Variations = variations,
            AllowSynthesis = allowSynthesis,
        };
    }

    /// <summary>Gets an OpenType tag as DirectWrite takes it: the first byte lowest, padded with spaces.</summary>
    private static uint Tag(string s)
    {
        var t = s.PadRight(4);
        return TerraFX.Interop.DirectX.DirectX.DWRITE_MAKE_OPENTYPE_TAG((byte)t[0], (byte)t[1], (byte)t[2], (byte)t[3]);
    }

    private static int? FirstRune(string s) =>
        s.Length != 0 && Rune.DecodeFromUtf16(s, out var rune, out _) == System.Buffers.OperationStatus.Done ? rune.Value : null;

    private static string GetString(JsonElement json, string name) =>
        json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;

    private static bool GetBool(JsonElement json, string name) =>
        json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static float GetFloat(JsonElement json, string name, float fallback) =>
        json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : fallback;

    private static float? GetOptionalFloat(JsonElement json, string name) =>
        json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
}
