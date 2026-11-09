using System;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>How much is made again. Each includes what it is made from, but a font swap keeps the atlases and new atlases keep the fonts.</summary>
[Flags]
internal enum ResetScope
{
    None = 0,

    /// <summary>Only what keeps text laid out (nameplates, the chat log) lays it out again.</summary>
    Text = 1,

    Glyphs = Text | 2,

    /// <summary>Also everything made from the game's fonts.</summary>
    SizedFonts = Glyphs | 4,

    Atlases = SizedFonts | 8,

    Fonts = SizedFonts | 16,
}
