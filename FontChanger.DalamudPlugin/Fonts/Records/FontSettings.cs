using System.Collections.Generic;

using FontChanger.DalamudPlugin.Fonts.Enums;

namespace FontChanger.DalamudPlugin.Fonts.Records;

/// <summary>
/// Game fonts the preset has no font of (all, if it is null) keep the game's glyphs, and characters a font lacks come from
/// the game's glyphs unless <paramref name="SystemFallback"/>.
/// </summary>
internal readonly record struct FontSettings(
    Preset? Preset = null, bool SystemFallback = true, IReadOnlyDictionary<TextFace, Preset>? Faces = null);
