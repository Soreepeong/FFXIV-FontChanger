using System;

using FontChanger.DalamudPlugin.Rendering.Rasterization;

namespace FontChanger.DalamudPlugin.Rendering.Faces.ElementFonts.MergingHelpers;

/// <summary>
/// A laid out line: its advance; its ink, measured by its glyphs' metrics (horizontally from the outlines of shaped
/// glyphs, which partly covered pixels may go past; vertically in pixels from the line's top; all 0 if it has none); and
/// how to draw it with the pen at an x on the baseline.
/// </summary>
internal sealed record MergeTextLine(int Width, bool Shaped, float InkX1, float InkX2, int InkY1, int InkY2, Func<float, RasterGlyph> Draw);
