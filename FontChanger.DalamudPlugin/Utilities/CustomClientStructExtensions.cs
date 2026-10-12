
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CustomFonts;

public static unsafe class CustomClientStructExtensions {
    /// <summary> Gets a glyph by its <see cref="Glyph.Utf8Value"/>, without adding a map entry as the game's lookup does. </summary>
    /// <returns> <c>null</c> if the font has none, or isn't built. </returns>
    public static Glyph* FindGlyph(this AtkFontManager.Font font, uint utf8Value) {
        if (font.GlyphMap is null || !font.Flags.HasFlag(FontFlags.Ready))
            return null;
        return font.GlyphMap->TryGetValue(utf8Value, out var glyph) ? glyph : null;
    }
}
