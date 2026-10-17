namespace FontChanger.Presets.Records;

/// <summary>A game font: its font type, FDT name (without the extension), family, size, and its font type's texture paths
/// (a format of the texture number, from 1).</summary>
public sealed record GameFontDataDefinition(string FontType, string Name, string Family, float Size, string Textures);
