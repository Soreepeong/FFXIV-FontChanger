using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Faces;

namespace FontChanger.DalamudPlugin.Fonts.Records;

/// <summary><see cref="Element"/> is null for the system's fonts.</summary>
internal sealed record GlyphPick(LoadedFont Font, Pointer<AtkFontManager.Font> Game, LoadedFontElement? Element);