using System;

namespace FontChanger.DalamudPlugin.Configuration;

/// <summary>Subpixel rendering values used with a set stripe order, instead of Windows'.</summary>
public readonly record struct ClearTypeSettings(float Level = 1, float Contrast = 0.5f, float Gamma = 1.8f)
{
    // Without it, new() gives zeros rather than the defaults above.
    public ClearTypeSettings() : this(1) { }

    public ClearTypeSettings Clamped() => new(Math.Clamp(this.Level, 0, 1), Math.Clamp(this.Contrast, 0, 4), Math.Clamp(this.Gamma, 1, 3));
}
