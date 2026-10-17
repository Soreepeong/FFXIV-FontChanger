namespace FontChanger.DalamudPlugin.Fonts.Records;

internal readonly record struct TextRun(int Start, int End, ScriptToken Script, bool RightToLeft);
