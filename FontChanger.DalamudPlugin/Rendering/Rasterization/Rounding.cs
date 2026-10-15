using System;

namespace FontChanger.DalamudPlugin.Rendering.Rasterization;

/// <summary>Rounding as the C++ code (xivres, FontChanger) does it: halves away from zero, as std::round and std::lround.</summary>
internal static class Rounding
{
    public static float Round(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);
}