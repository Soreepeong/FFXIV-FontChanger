using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Rendering.Rasterization;

internal interface IGlyphRunSink
{
    void OnGlyphRun(float baselineX, in DWRITE_GLYPH_RUN run, in DWRITE_GLYPH_RUN_DESCRIPTION description);
}
