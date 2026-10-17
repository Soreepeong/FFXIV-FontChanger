using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FontChanger.Presets.Records;

public static class RuneRangeExtensions
{
    public static List<RuneRange> Coalesce(this IEnumerable<RuneRange> ranges)
    {
        var result = new List<RuneRange>();
        if (ranges.TryGetNonEnumeratedCount(out var count))
            result.Capacity = count;
        foreach (var r in ranges.OrderBy(r => r.First.Value))
        {
            if (result.Count != 0 && r.First.Value <= result[^1].Last.Value + 1)
            {
                if (r.Last.Value > result[^1].Last.Value)
                    result[^1] = result[^1] with { Last = r.Last };
            }
            else
            {
                result.Add(r);
            }
        }

        return result;
    }

    public static List<RuneRange> Subtract(this IEnumerable<RuneRange> ranges, IEnumerable<RuneRange> others)
    {
        var removed = others.Coalesce();
        var result = new List<RuneRange>();
        foreach (var r in ranges.Coalesce())
        {
            var first = r.First.Value;
            foreach (var o in removed)
            {
                if (o.Last.Value < first)
                    continue;
                if (o.First.Value > r.Last.Value)
                    break;
                AddValid(result, first, o.First.Value - 1);
                first = o.Last.Value + 1;
            }

            AddValid(result, first, r.Last.Value);
        }

        return result;
    }

    public static List<RuneRange> Intersect(this IEnumerable<RuneRange> ranges, IEnumerable<RuneRange> others)
    {
        var coalesced = ranges.Coalesce();
        return coalesced.Subtract(coalesced.Subtract(others));
    }

    // Indexed rather than enumerated, as the plugin calls it for every UTF-16 code unit it lays out.
    public static bool Contains(this IReadOnlyList<RuneRange> ranges, Rune rune)
    {
        for (var i = 0; i < ranges.Count; i++)
        {
            if (ranges[i].First <= rune && rune <= ranges[i].Last)
                return true;
        }

        return false;
    }

    // Runes can't be surrogates, so ranges skip them.
    private static void AddValid(List<RuneRange> result, int first, int last)
    {
        if (first is >= 0xD800 and <= 0xDFFF)
            first = 0xE000;
        if (last is >= 0xD800 and <= 0xDFFF)
            last = 0xD7FF;
        if (first <= last)
            result.Add(new(new(first), new(last)));
    }
}
