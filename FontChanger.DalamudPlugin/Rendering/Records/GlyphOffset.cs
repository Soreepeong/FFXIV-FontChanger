namespace FontChanger.DalamudPlugin.Rendering.Records;

/// <summary>A glyph's offset from where its advance puts it.</summary>
internal readonly record struct GlyphOffset(float Advance, float Ascender);
