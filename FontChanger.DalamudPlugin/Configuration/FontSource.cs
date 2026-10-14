using System;

namespace FontChanger.DalamudPlugin.Configuration;

[Serializable]
public sealed record FontSource
{
    public bool Enabled { get; init; } = true;

    public FontSourceType Type { get; init; }

    public string Path { get; init; } = string.Empty;

    public FamilyFont? Font { get; init; }

    public bool MonospacedDigits { get; init; } = true;
}
