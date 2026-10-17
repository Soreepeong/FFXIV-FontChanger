namespace FontChanger.Presets.Records;

/// <summary>Name is the FDT name without its extension; Textures is a path format of the texture number, from 1.</summary>
public sealed record GameFontDataDefinition(string FontType, string Name, string Family, float Size, string Textures);
