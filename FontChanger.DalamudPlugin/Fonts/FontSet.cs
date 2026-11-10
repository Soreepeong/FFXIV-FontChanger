using System;
using System.Collections.Generic;
using System.Linq;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Faces;
using FontChanger.DalamudPlugin.Fonts.Text;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>Fonts made together, sharing their face sources.</summary>
internal sealed class FontSet : IDisposable
{
    private bool? _hasNamePlateFaces;

    private FontSet()
    {
    }

    public FaceSourceCache Sources { get; } = new();

    /// <summary>Gets the font of the game fonts the preset has no font of.</summary>
    public MaterializedFont Game { get; private set; } = null!;

    public Dictionary<string, MaterializedFont> Preset { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<TextFace, Dictionary<string, MaterializedFont>> Faces { get; } = [];

    private IEnumerable<MaterializedFont> All => this.Preset.Values.Concat(this.Faces.Values.SelectMany(f => f.Values)).Append(this.Game);

    /// <summary>Only the game font's, to which the others are added.</summary>
    public static FontSet CreateGame(FontServices services, bool systemFallback, SystemFallbackFonts fallbackFonts)
    {
        var set = new FontSet();
        set.Game = MaterializedFont.CreateGame(services, systemFallback, fallbackFonts, set.Sources);
        return set;
    }

    public bool HasOwnFonts(TextFace face) => this.Faces.ContainsKey(face);

    // A plate whose own face has no fonts still picks a copy in the nameplates' atlas, which its title's glyphs must be in. The faces
    // are complete once the set is handed to the main thread.
    public bool HasFonts(TextFace face) =>
        face == TextFace.Default ||
        this.Faces.ContainsKey(face) ||
        (face == TextFace.NamePlate && (this._hasNamePlateFaces ??= this.Faces.Keys.Any(TextFaceNames.IsNamePlates)));

    public TextFace ResolveFace(TextFace face)
    {
        while (!this.HasFonts(face))
            face = TextFaceNames.FallbackOf(face);
        return face;
    }

    public MaterializedFont FontOf(string fontName, TextFace face)
    {
        for (; face != TextFace.Default; face = TextFaceNames.FallbackOf(face))
        {
            if (this.Faces.TryGetValue(face, out var fonts) && fonts.GetValueOrDefault(fontName) is { } font)
                return font;
        }

        return this.Preset.GetValueOrDefault(fontName) ?? this.Game;
    }

    /// <summary>A game font was rebuilt, possibly from another file.</summary>
    public void ForgetGameFonts()
    {
        foreach (var font in this.All)
            font.ForgetGameFonts();
    }

    public void Dispose()
    {
        foreach (var font in this.All)
            font.Dispose();
        this.Sources.Dispose();
    }
}
