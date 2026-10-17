using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Rendering.Rasterization;

internal unsafe interface IGlyphRunSink
{
    /// <summary>Takes a glyph run of a layout; <paramref name="face"/> is the run's face (instead of its fontFace).</summary>
    void OnGlyphRun(float baselineX, IDWriteFontFace5* face, in DWRITE_GLYPH_RUN run, in DWRITE_GLYPH_RUN_DESCRIPTION description);
}
