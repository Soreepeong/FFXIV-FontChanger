using System.Linq;
using System.Text;
using System.Text.Unicode;

namespace FontChanger.Presets.Records;

public record struct RuneRange(Rune First, Rune Last)
{
    public static RuneRange[] From(uint first, uint last) => [new(new(first), new(last))];

    public static RuneRange[] From(params UnicodeRange[] ranges) => ranges.Where(r => r.Length > 0).Select(r => (RuneRange)r).Coalesce().ToArray();

    public static implicit operator RuneRange(UnicodeRange r) => new(new(r.FirstCodePoint), new(r.FirstCodePoint + r.Length - 1));
}
