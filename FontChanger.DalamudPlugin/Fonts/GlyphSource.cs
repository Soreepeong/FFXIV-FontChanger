using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Faces;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary><see cref="Element"/> is null for the system's fonts.</summary>
internal sealed record GlyphSource(MaterializedFont Font, Pointer<AtkFontManager.Font> Game, MaterializedFontElement? Element);