using System;

namespace FontChanger.DalamudPlugin.Configuration;

public readonly record struct SubpixelRenderingParameters(float Level = 1, float Contrast = 0.5f, float Gamma = 1.8f)
{
    public SubpixelRenderingParameters() : this(1) { }

    public SubpixelRenderingParameters Clamped() => new(Math.Clamp(this.Level, 0, 1), Math.Clamp(this.Contrast, 0, 4), Math.Clamp(this.Gamma, 1, 2.2f));
}
