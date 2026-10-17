namespace FontChanger.DalamudPlugin.Fonts.Records;

/// <summary>TextStart and TextEnd are code units relative to the text shaped.</summary>
internal sealed record ShapedCluster(uint[] GlyphIds, float[] Advances, GlyphOffset[]? Offsets, int TextStart, int TextEnd);
