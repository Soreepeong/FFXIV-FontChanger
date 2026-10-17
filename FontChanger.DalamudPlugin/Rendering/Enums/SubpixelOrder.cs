namespace FontChanger.DalamudPlugin.Rendering.Enums;

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
