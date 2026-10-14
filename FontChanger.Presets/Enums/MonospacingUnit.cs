namespace FontChanger.Presets.Enums;

/// <summary>How monospacing's widths are measured.</summary>
public enum MonospacingUnit
{
    Pixels,
    Em,

    /// <summary>Multiples of the advance of the reference character.</summary>
    ReferenceGlyph,
}
