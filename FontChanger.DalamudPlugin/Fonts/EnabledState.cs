using System;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>Whether fonts are replaced, read by every hook.</summary>
internal sealed class EnabledState(FrameLog log)
{
    public bool IsOn { get; set; } = true;

    /// <summary>Stops replacing at once, and has the rest turned off at the next framework update.</summary>
    public void Fail(Exception ex, string message)
    {
        this.IsOn = false;
        CopyFonts.ForgetPicked();
        log.Fail(Part.Replacer, ex, message);
    }
}
