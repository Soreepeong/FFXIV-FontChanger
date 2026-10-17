using System.Collections.Generic;
using System.Linq;

namespace FontChanger.Presets.Records;

public static class RuneRangeExtensions
{
    /// <summary>Gets ranges ordered, with those that overlap or follow each other merged.</summary>
    public static IEnumerable<RuneRange> Coalesce(this IEnumerable<RuneRange> ranges)
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
}
