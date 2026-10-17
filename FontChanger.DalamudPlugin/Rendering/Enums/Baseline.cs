namespace FontChanger.DalamudPlugin.Rendering.Enums;

/// <summary>The baselines of the BASE table a face aligns its elements by.</summary>
internal enum Baseline
{
    /// <summary><c>romn</c>: The baseline used by simple alphabetic scripts such as Latin, Cyrillic, and Greek.</summary>
    Roman,

    /// <summary><c>ideo</c>: The bottom of the ideographic em box.</summary>
    IdeographicBottom,

    /// <summary><c>idtp</c>: The top of the ideographic em box.</summary>
    IdeographicTop,

    /// <summary><c>icfb</c>: The bottom of the ideographic character face.</summary>
    IdeographicFaceBottom,

    /// <summary><c>icft</c>: The top of the ideographic character face.</summary>
    IdeographicFaceTop,
}
