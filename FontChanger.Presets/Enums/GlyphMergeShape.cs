namespace FontChanger.Presets.Enums;

/// <summary>Mirrors xivres glyph_merge_shape; the shapes follow the Lodestone web font's.</summary>
public enum GlyphMergeShape
{
    /// <summary>The text alone, smaller, on the baseline.</summary>
    None,
    AmPm,
    Ime,
    Box,

    /// <summary>A box sized for numbers of one digit, or slightly smaller for more.</summary>
    NumberBox,

    /// <summary>A box outline the text is drawn inside of, instead of cut out of.</summary>
    HollowBox,
    Hexagon,
    Rhombus,
    Bozja,
    Time,

    /// <summary>The star that splits the box of the Bozja glyphs in two.</summary>
    Star,

    /// <summary>An SVG path, or a whole SVG document.</summary>
    Custom,

    /// <summary>The element's own glyph of the codepoint.</summary>
    Glyph,
}
