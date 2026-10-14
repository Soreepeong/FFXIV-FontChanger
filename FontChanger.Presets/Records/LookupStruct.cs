using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace FontChanger.Presets.Records;

public sealed record LookupStruct(
    string Name,
    int Weight,
    int Stretch,
    int Style,
    IReadOnlyDictionary<uint, uint> Features)
{
    private static readonly IReadOnlyDictionary<uint, uint> NoFeatures = ImmutableDictionary<uint, uint>.Empty;
    private static readonly IReadOnlyDictionary<uint, float> NoVariations = ImmutableDictionary<uint, float>.Empty;

    public string LanguageCode { get; init; } = string.Empty;

    public IReadOnlyDictionary<uint, float> Variations { get; init; } = NoVariations;

    public bool? AllowSynthesis { get; init; }

    public static string DescribeWeight(int weight) => weight switch
    {
        100 => "THIN",
        200 => "EXTRA_LIGHT",
        300 => "LIGHT",
        350 => "SEMI_LIGHT",
        400 => "REGULAR",
        500 => "MEDIUM",
        600 => "SEMI_BOLD",
        700 => "BOLD",
        800 => "EXTRA_BOLD",
        900 => "BLACK",
        950 => "EXTRA_BLACK",
        _ => $"unknown({weight})",
    };

    public static string DescribeStretch(int stretch) => stretch switch
    {
        0 => "UNDEFINED",
        1 => "ULTRA_CONDENSED",
        2 => "EXTRA_CONDENSED",
        3 => "CONDENSED",
        4 => "SEMI_CONDENSED",
        5 => "NORMAL",
        6 => "SEMI_EXPANDED",
        7 => "EXPANDED",
        8 => "EXTRA_EXPANDED",
        9 => "ULTRA_EXPANDED",
        _ => $"unknown({stretch})",
    };

    public static string DescribeStyle(int style) => style switch
    {
        0 => "NORMAL",
        1 => "OBLIQUE",
        2 => "ITALIC",
        _ => $"unknown({style})",
    };

    public static string DescribeTag(uint tag) => string.Create(
        4,
        tag,
        static (span, t) =>
        {
            for (var i = 0; i < 4; i++)
                span[i] = (char)((t >> (8 * i)) & 0xFF);
        });

    public override string ToString()
    {
        var text = new StringBuilder();
        text.Append($"{nameof(LookupStruct)}({nameof(Name)}={this.Name}");
        text.Append($", {nameof(Weight)}={DescribeWeight(this.Weight)}");
        text.Append($", {nameof(Stretch)}={DescribeStretch(this.Stretch)}");
        text.Append($", {nameof(Style)}={DescribeStyle(this.Style)}");
        if (this.LanguageCode.Length != 0)
            text.Append($", {nameof(LanguageCode)}={this.LanguageCode}");
        if (this.Features.Count != 0)
            text.Append($", {nameof(Features)}={{{string.Join(", ", this.Features.Select(f => $"{DescribeTag(f.Key)}={f.Value}"))}}}");
        if (this.Variations.Count != 0)
            text.Append($", {nameof(Variations)}={{{string.Join(", ", this.Variations.Select(v => $"{DescribeTag(v.Key)}={v.Value}"))}}}");
        if (this.AllowSynthesis is { } synthesis)
            text.Append($", {nameof(AllowSynthesis)}={synthesis}");
        return text.Append(')').ToString();
    }

    public static LookupStruct Of(string family) => new(family, 400, 5, 0, NoFeatures);
}
