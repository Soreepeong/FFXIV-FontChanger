namespace FontChanger.DalamudPlugin.Rendering.Faces;

/// <summary>The baselines of the BASE table a face aligns its elements by.</summary>
internal enum Baseline
{
    /// <summary><c>romn</c>.</summary>
    Roman,

    /// <summary><c>ideo</c>: the bottom of the ideographic em box.</summary>
    IdeographicBottom,

    /// <summary><c>idtp</c>: the top of the ideographic em box.</summary>
    IdeographicTop,

    /// <summary><c>icfb</c>: the bottom of the ideographic character face.</summary>
    IdeographicFaceBottom,

    /// <summary><c>icft</c>: the top of the ideographic character face.</summary>
    IdeographicFaceTop,
}
