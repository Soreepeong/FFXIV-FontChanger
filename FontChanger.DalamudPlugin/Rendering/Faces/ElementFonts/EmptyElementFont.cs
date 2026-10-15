using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Rendering.Rasterization;

namespace FontChanger.DalamudPlugin.Rendering.Faces.ElementFonts;

internal sealed unsafe class EmptyElementFont(float size, float ascent, float lineHeight) : IElementFont
{
    public bool Has(Rune rune, AtkFontManager.Font* game) => false;

    public (int Ascent, int LineHeight) GetLineMetrics(float px, AtkFontManager.Font* game) =>
        size > 0 ? ((int)Rounding.Round(ascent * px / size), (int)Rounding.Round(lineHeight * px / size)) : (0, 0);

    public bool TryGetBaseline(uint tag, float px, out float baseline)
    {
        baseline = 0;
        return false;
    }

    public RasterGlyph? Rasterize(Rune rune, float px) => null;

    public float GetAdvance(Rune rune, float px) => 0;

    public void Dispose()
    {
    }
}
