namespace FontChanger.DalamudPlugin.Rendering.Rasterization;

/// <summary>
/// The order of a pixel's color stripes: left to right, or top to bottom for the vertical ones (a panel turned to portrait,
/// or one made so and turned to landscape); None draws grayscale.
/// </summary>
internal enum SubpixelOrder
{
    None,
    Rgb,
    Bgr,
    VerticalRgb,
    VerticalBgr,
}

internal static class SubpixelOrderExtensions
{
    /// <summary>Gets whether the stripes are stacked vertically.</summary>
    public static bool IsVertical(this SubpixelOrder order) => order is SubpixelOrder.VerticalRgb or SubpixelOrder.VerticalBgr;

    /// <summary>Gets whether blue comes first: R, G, B coverage is stored swapped from the stripes' order.</summary>
    public static bool IsBlueFirst(this SubpixelOrder order) => order is SubpixelOrder.Bgr or SubpixelOrder.VerticalBgr;
}
