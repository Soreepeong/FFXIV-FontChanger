namespace FontChanger.DalamudPlugin.Configuration;

/// <summary>What draws fonts set to DirectWrite for vertical subpixel layouts, for which DirectWrite has no ClearType of its own.</summary>
public enum VerticalSubpixelRenderer
{
    /// <summary>FreeType's LCD_V rendering, hinted as the fonts' grid fitting says.</summary>
    FreeType,

    /// <summary>DirectWrite's ClearType of the glyphs turned a quarter, turned back; without hinting.</summary>
    DirectWrite,
}
