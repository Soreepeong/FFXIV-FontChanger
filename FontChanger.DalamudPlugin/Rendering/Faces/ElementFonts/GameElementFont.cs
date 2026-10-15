using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.Rendering.Faces.ElementFonts;

internal sealed unsafe class GameElementFont : IElementFont
{
    public static readonly GameElementFont Instance = new();

    private GameElementFont()
    {
    }

    public bool Has(Rune rune, AtkFontManager.Font* game) => game is not null && game->FindGlyph(rune) is not null;

    public (int Ascent, int LineHeight) GetLineMetrics(float px, AtkFontManager.Font* game)
    {
        if (game is null || game->Size <= 0)
            return (0, 0);
        var scale = px / game->Size;
        return ((int)Rounding.Round(game->Ascent * scale), (int)Rounding.Round(game->LineHeight * scale));
    }

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
