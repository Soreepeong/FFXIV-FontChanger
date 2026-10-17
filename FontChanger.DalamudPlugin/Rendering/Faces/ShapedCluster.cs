namespace FontChanger.DalamudPlugin.Rendering.Faces;

/// <summary>A cluster: glyphs of the source that shaped it, and the code units it covers (relative to the text shaped).</summary>
internal sealed record ShapedCluster(uint[] GlyphIds, float[] Advances, GlyphOffset[]? Offsets, int TextStart, int TextEnd);
