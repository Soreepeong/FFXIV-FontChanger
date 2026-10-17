namespace FontChanger.DalamudPlugin.Rendering.Faces;

/// <summary>A glyph's offset from where its advance puts it: along the line, and up (DWRITE_GLYPH_OFFSET).</summary>
internal readonly record struct GlyphOffset(float Advance, float Ascender);
