using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CustomFonts.Utilities;

public static unsafe class CustomClientStructExtensions {
    /// <summary> Gets a glyph by its <see cref="AtkFontManager.Glyph.Utf8Value"/>, without adding a map entry as the game's lookup does. </summary>
    /// <returns> <c>null</c> if the font has none, or isn't built. </returns>
    public static AtkFontManager.Glyph* FindGlyph(this ref AtkFontManager.Font font, uint utf8Value) {
        if (font.GlyphMap is null || !font.Flags.HasFlag(AtkFontManager.FontFlags.Ready))
            return null;
        return font.GlyphMap->TryGetValue(utf8Value, out var glyph) ? glyph : null;
    }

    /// <summary> Gets a loaded resource's file name, from the game's std::string; a length out of all reason reads as none. </summary>
    public static string GetFileName(ResourceHandle* handle) {
        var length = handle->FileName.Length;
        return length is > 0 and <= 4096
            ? Encoding.UTF8.GetString(handle->FileName.AsSpan(0, (int)length))
            : string.Empty;
    }
}
