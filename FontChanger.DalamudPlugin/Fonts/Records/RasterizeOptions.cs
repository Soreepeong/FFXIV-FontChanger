namespace FontChanger.DalamudPlugin.Fonts.Records;

/// <summary>
/// OriginX is the pen's fraction of a pixel, a null Advance keeps the glyph's own (rounded), and SqueezeX narrows for
/// monospacing, which outline fonts draw so and the others resample.
/// </summary>
internal readonly record struct RasterizeOptions(float OriginX = 0, int? Advance = null, float SqueezeX = 1);
