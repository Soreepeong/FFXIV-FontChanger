using FontChanger.DalamudPlugin.Rendering.Faces;

namespace FontChanger.DalamudPlugin.Rendering.Text;

/// <summary>A run of a line: its code units, its script, and whether it is written right to left.</summary>
internal readonly record struct TextRun(int Start, int End, ScriptToken Script, bool RightToLeft);
