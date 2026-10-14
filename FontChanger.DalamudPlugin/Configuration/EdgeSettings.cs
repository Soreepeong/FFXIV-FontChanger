using System;

using FontChanger.DalamudPlugin.Rendering.Shaders;

namespace FontChanger.DalamudPlugin.Configuration;

public readonly record struct EdgeSettings(float Scale = 0, float Min = 1, float Max = 1, float Softness = 0)
{
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
        };
    }
}
