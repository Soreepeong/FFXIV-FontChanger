using System;

namespace FontChanger.Presets.Enums;

[Flags]
public enum FontScripts
{
    None = 0,
    NonCjk = 1,
    Han = 2,
    Kana = 4,
    Hangul = 8,

    /// <summary>The private-use icons and merged glyphs of AXIS fonts.</summary>
    GameSymbols = 16,

    All = NonCjk | Han | Kana | Hangul | GameSymbols,
}
