using System.Collections.Generic;

namespace FontChanger.Presets.Records;

public readonly record struct FreeTypeParams(int LoadFlags, int RenderMode)
{
    public static readonly FreeTypeParams Default = new(0, 1);

    private static readonly (int Bit, string Name)[] LoadFlagNames =
    [
        (1 << 0, "NO_SCALE"),
        (1 << 1, "NO_HINTING"),
        (1 << 2, "RENDER"),
        (1 << 3, "NO_BITMAP"),
        (1 << 4, "VERTICAL_LAYOUT"),
        (1 << 5, "FORCE_AUTOHINT"),
        (1 << 6, "CROP_BITMAP"),
        (1 << 7, "PEDANTIC"),
        (1 << 8, "ADVANCE_ONLY"),
        (1 << 9, "IGNORE_GLOBAL_ADVANCE_WIDTH"),
        (1 << 10, "NO_RECURSE"),
        (1 << 11, "IGNORE_TRANSFORM"),
        (1 << 12, "MONOCHROME"),
        (1 << 13, "LINEAR_DESIGN"),
        (1 << 14, "SBITS_ONLY"),
        (1 << 15, "NO_AUTOHINT"),
        (1 << 20, "COLOR"),
        (1 << 21, "COMPUTE_METRICS"),
        (1 << 22, "BITMAP_METRICS_ONLY"),
        (1 << 23, "SVG_ONLY"),
        (1 << 24, "NO_SVG"),
    ];

    public static string DescribeRenderMode(int mode) => mode switch
    {
        0 => "NORMAL",
        1 => "LIGHT",
        2 => "MONO",
        3 => "LCD",
        4 => "LCD_V",
        5 => "SDF",
        _ => $"unknown({mode})",
    };

    public static string DescribeLoadFlags(int flags)
    {
        var names = new List<string>();
        var remaining = flags;
        foreach (var (bit, name) in LoadFlagNames)
        {
            if ((remaining & bit) == 0)
                continue;
            names.Add(name);
            remaining &= ~bit;
        }

        var target = (remaining >> 16) & 15;
        remaining &= ~(15 << 16);
        if (target != 0)
            names.Add($"TARGET_{DescribeRenderMode(target)}");
        if (remaining != 0)
            names.Add($"unknown(0x{remaining:X})");
        return names.Count == 0 ? "DEFAULT" : string.Join(" | ", names);
    }

    public override string ToString() =>
        $"{nameof(FreeTypeParams)}({nameof(LoadFlags)}={DescribeLoadFlags(LoadFlags)}, {nameof(RenderMode)}={DescribeRenderMode(RenderMode)})";
}
