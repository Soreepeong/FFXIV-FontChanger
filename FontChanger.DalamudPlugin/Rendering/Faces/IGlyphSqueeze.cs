using FontChanger.DalamudPlugin.Rendering.Rasterization;

namespace FontChanger.DalamudPlugin.Rendering.Faces;

/// <summary>Draws a glyph again, narrower, for monospacing (a struct, so that passing one allocates nothing).</summary>
internal interface IGlyphSqueeze
{
    /// <summary>Draws the glyph scaled horizontally by <paramref name="scale"/>.</summary>
    RasterGlyph Squeeze(float scale);
}
