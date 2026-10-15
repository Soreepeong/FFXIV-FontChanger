namespace FontChanger.DalamudPlugin.Rendering.Rasterization;

/// <summary>The order of a pixel's color stripes, left to right; None draws grayscale.</summary>
internal enum SubpixelOrder
{
    None,
    Rgb,
    Bgr,
}