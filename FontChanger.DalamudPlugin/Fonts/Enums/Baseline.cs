namespace FontChanger.DalamudPlugin.Fonts.Enums;

// The BASE table's baseline tags: https://learn.microsoft.com/en-us/typography/opentype/otspec181/baselinetags
internal enum Baseline
{
    Roman, // romn
    IdeographicBottom, // ideo, of the em box
    IdeographicTop, // idtp, of the em box
    IdeographicFaceBottom, // icfb
    IdeographicFaceTop, // icft
}
