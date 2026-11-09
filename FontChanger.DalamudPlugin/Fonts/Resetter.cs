using FontChanger.DalamudPlugin.Fonts.Glyphs;
using FontChanger.DalamudPlugin.Fonts.Text;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>The one way to make glyphs, sized fonts, atlases or fonts again, each step once and in order.</summary>
/// <remarks>
/// <ul>
/// <li>Called between frames, or in a hook outside any layout.</li>
/// <li>Nameplates already baked keep their pixels until baked again.</li>
/// </ul>
/// </remarks>
internal sealed class Resetter(
    GameTextLayout layout,
    SizedFontRegistry sized,
    CellPlacer placer,
    GameGlyphReader reader,
    AtlasSet atlases,
    CopyFonts copies)
{
    /// <summary><paramref name="fonts"/>, if given, replace the fonts, and the old ones are disposed.</summary>
    public void Reset(ResetScope scope, FontSet? fonts = null)
    {
        if (fonts is not null)
            scope |= ResetScope.Fonts;
        if (!scope.HasFlag(ResetScope.Glyphs))
            return;

        // Nothing of ours refers to the glyphs anymore, and they are freed once the game is done with them.
        layout.Clear();
        placer.Clear();
        reader.Clear();
        if (scope.HasFlag(ResetScope.SizedFonts))
        {
            sized.Clear();
            sized.Fonts.ForgetGameFonts();
        }
        else
        {
            sized.ReleaseAllGlyphs();
        }

        // Copies refer to the old atlases' pages until they are synced below.
        if (scope.HasFlag(ResetScope.Atlases))
            atlases.Dispose();
        else
            atlases.ClearAll();

        if (fonts is not null)
            sized.SwapFonts(fonts);
        if (scope.HasFlag(ResetScope.SizedFonts))
            copies.ResyncAll();
        CopyFonts.ForgetPicked();
    }
}
