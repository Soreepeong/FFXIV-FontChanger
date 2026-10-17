namespace FontChanger.DalamudPlugin.Rendering.Faces;

/// <param name="OriginX">The fraction of a pixel the pen is at.</param>
/// <param name="Advance">The advance the glyph is given; null for its own, rounded.</param>
/// <param name="SqueezeX">Narrowed by monospacing: outline fonts draw it so, the others resample it.</param>
internal readonly record struct RasterizeOptions(float OriginX = 0, int? Advance = null, float SqueezeX = 1);
