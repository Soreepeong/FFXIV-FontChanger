namespace FontChanger.DalamudPlugin.Fonts.Records;

/// <summary>A DWRITE_SCRIPT_ANALYSIS, opaque but to the shaping that pairs with it.</summary>
internal readonly record struct ScriptToken(ushort Script, int Shapes);
