namespace FontChanger.Presets.Enums;

/// <summary>How text wider than its shape's area is fitted.</summary>
public enum MergeFitMode
{
    /// <summary>Narrowed to 70%, then made smaller.</summary>
    CondenseThenShrink,
    Shrink,
    Overflow,
}
