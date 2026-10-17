using System;

namespace FontChanger.DalamudPlugin.Fonts.Records;

/// <summary>Offsets are in pixels, and empty if there are none.</summary>
internal readonly ref struct GlyphRun(ReadOnlySpan<uint> glyphIds, ReadOnlySpan<float> advances, ReadOnlySpan<GlyphOffset> offsets = default)
{
    public ReadOnlySpan<uint> GlyphIds { get; } = glyphIds;

    public ReadOnlySpan<float> Advances { get; } = advances;

    public ReadOnlySpan<GlyphOffset> Offsets { get; } = offsets;
}
