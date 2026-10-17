namespace FontChanger.DalamudPlugin.Fonts.Enums;

internal enum ItalicMode
{
    Upright,

    /// <summary>By an italic macro, which measuring sees too: the sources' italics drawn as they are, the others sheared by the game.</summary>
    Real,

    /// <summary>
    /// By the text node, which measuring doesn't see: shaped as <see cref="Real"/> and spaced to the upright run's width, or
    /// where the shapings differ, upright with each single-character cluster in its font's italic.
    /// </summary>
    Images,
}
