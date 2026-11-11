namespace FontChanger.DalamudPlugin.Configuration;

// DirectWrite has no vertical ClearType, so vertical layouts use FreeType's hinted LCD_V or unhinted ClearType of glyphs turned a quarter.
public enum VerticalSubpixelRenderer
{
    FreeType,
    DirectWrite,
}