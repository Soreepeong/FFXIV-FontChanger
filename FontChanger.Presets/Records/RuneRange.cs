using System.Linq;
using System.Text;
using System.Text.Unicode;

namespace FontChanger.Presets.Records;

public record struct RuneRange(Rune First, Rune Last)
{
    public static RuneRange[] From(uint first, uint last) => [new(new(first), new(last))];

    /// <summary>Gets the codepoints of Unicode blocks, ordered, with blocks that overlap or follow each other as one range.</summary>
    public static RuneRange[] From(params UnicodeRange[] ranges) =>
        ranges.Where(r => r.Length > 0).Select(r => new RuneRange(new(r.FirstCodePoint), new((r.FirstCodePoint + r.Length) - 1))).Coalesce().ToArray();

    public static implicit operator RuneRange(UnicodeRange r) => new(new(r.FirstCodePoint), new(r.FirstCodePoint + r.Length - 1));
}
