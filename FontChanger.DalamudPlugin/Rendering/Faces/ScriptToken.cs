namespace FontChanger.DalamudPlugin.Rendering.Faces;

/// <summary>A script as the analyzer found it, opaque but to the shaping that pairs with it (DWRITE_SCRIPT_ANALYSIS).</summary>
internal readonly record struct ScriptToken(ushort Script, int Shapes);
