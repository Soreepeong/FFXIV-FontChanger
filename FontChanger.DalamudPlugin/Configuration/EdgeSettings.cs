using System;

using FontChanger.DalamudPlugin.Rendering.Shaders;

namespace FontChanger.DalamudPlugin.Configuration;

public readonly record struct EdgeSettings(float Scale = 0, float Min = 1, float Max = 1, float Softness = 0, bool Enabled = false)
{
    // Without it, new() gives zeros rather than the defaults above.
    public EdgeSettings() : this(0, 1, 1, 0) { }

    public float GetWidth(float px) => Math.Clamp(this.Scale * px, this.Min, this.Max);
    public float GetReach(float px) => Math.Min(this.GetWidth(px) * Math.Max(this.Softness, 1), 8);

    public EdgeSettings Clamped()
    {
        var min = Math.Clamp(this.Min, 0.25f, 8);
        return new()
        {
            Scale = Math.Clamp(this.Scale, 0, 1),
            Min = min,
            Max = Math.Clamp(this.Max, min, 8),
            Softness = Math.Clamp(this.Softness, 0, EdgeShaderCode.MaxSoftness),
            Enabled = this.Enabled,
        };
    }

    /// <summary>Gets the edge as drawn: clamped if enabled, else the game's own (the defaults).</summary>
    public EdgeSettings Drawn() => this.Enabled ? this.Clamped() : new();
}
