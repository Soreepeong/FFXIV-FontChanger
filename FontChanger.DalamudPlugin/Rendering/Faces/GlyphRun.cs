using System;

namespace FontChanger.DalamudPlugin.Rendering.Faces;

/// <summary>Glyphs of one source, with their advances as laid out and their offsets in pixels (empty: none).</summary>
internal readonly ref struct GlyphRun(ReadOnlySpan<uint> glyphIds, ReadOnlySpan<float> advances, ReadOnlySpan<GlyphOffset> offsets = default)
{
    public ReadOnlySpan<uint> GlyphIds { get; } = glyphIds;

    public ReadOnlySpan<float> Advances { get; } = advances;

    public ReadOnlySpan<GlyphOffset> Offsets { get; } = offsets;
}
