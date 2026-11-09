using System;
using System.Collections.Generic;
using System.Linq;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Faces;
using FontChanger.DalamudPlugin.Fonts.Glyphs;
using FontChanger.DalamudPlugin.Game;

namespace FontChanger.DalamudPlugin.Fonts;

/// <summary>The sized fonts made so far, and the glyphs they cache.</summary>
internal sealed unsafe class SizedFontRegistry(FontSet fonts, AtlasSet atlases, GlyphStore store)
{
    private readonly Dictionary<(MaterializedFont Font, int HalfPx, string FontName, TextFace Face, bool Bold), SizedFont> _sizedFonts = [];

    // Get's results by its arguments, as finding the family's fonts for a request is slow and wrapping asks per character. Which
    // of the family's fonts were ready is in them.
    private readonly Dictionary<(Pointer<AtkFontManager.Font> Original, int HalfPx, TextFace Face, bool Bold), SizedFont> _byRequest = [];

    public FontSet Fonts { get; private set; } = fonts;

    public IEnumerable<SizedFont> All => this._sizedFonts.Values;

    /// <summary>
    /// At the size of one of the family's fonts, that font's glyphs with its game font's metrics. At another, each glyph from the
    /// largest font that draws it itself, with the metrics of the font that draws the most glyphs itself.
    /// </summary>
    public SizedFont Get(Pointer<AtkFontManager.Font> original, int halfPx, TextFace face, bool bold)
    {
        face = this.Fonts.ResolveFace(face);
        var request = (original, halfPx, face, bold);
        if (this._byRequest.TryGetValue(request, out var known))
            return known;

        var px = halfPx / 2f;
        var fonts = new List<(string Name, float Size, MaterializedFont Font, Pointer<AtkFontManager.Font> Game)>();
        if (FontNames.TryGetFontName(original, out var originalName))
        {
            foreach (var (name, size) in FontNames.FontNamesOf(Preset.FamilyOf(originalName)).OrderByDescending(f => f.Size))
            {
                var loaded = original;
                if (!string.Equals(name, originalName, StringComparison.OrdinalIgnoreCase) && !FontNames.TryFindFont(name, out loaded))
                    continue;
                if (loaded.IsNull || !loaded.Value->Flags.HasFlag(AtkFontManager.FontFlags.Ready) || loaded.Value->Size <= 0)
                    continue;
                fonts.Add((name, size, this.Fonts.FontOf(name, face), loaded));
            }
        }

        if (fonts.Count == 0)
        {
            var name = originalName ?? $"0x{(nint)original.Value:X}";
            fonts = [(name, 0, originalName is not null ? this.Fonts.FontOf(originalName, face) : this.Fonts.Game, original)];
        }
        else if (fonts.FindIndex(f => MathF.Abs(f.Size - px) < 0.01f) is var exact and >= 0)
        {
            fonts = [fonts[exact]];
        }

        var best = 0;
        for (var i = 1; i < fonts.Count; i++)
        {
            if (fonts[i].Font.OwnCodepointCount > fonts[best].Font.OwnCodepointCount)
                best = i;
        }

        var (fontName, _, font, game) = fonts[best];
        var key = (font, halfPx, fontName, face, bold);
        if (!this._sizedFonts.TryGetValue(key, out var sized))
        {
            var (ascent, lineHeight) = font.GetLineMetrics(px, game);
            var atlas = atlases.Get(
                face == TextFace.Default ? Preset.FamilyOf(fontName)
                : TextFaceNames.IsNamePlates(face) ? TextFaceNames.NamePlates
                : TextFaceNames.NameOf(face));
            sized = new(font, px, ascent, lineHeight, game, atlas, fonts.Select(f => (f.Font, f.Game)).ToList(), bold);
            this._sizedFonts.Add(key, sized);
        }

        this._byRequest[request] = sized;
        return sized;
    }

    public void Sweep(long frame)
    {
        foreach (var sized in this._sizedFonts.Values)
            sized.SweepGlyphs(frame, store.ReleaseAction);
    }

    public void ReleaseAllGlyphs()
    {
        foreach (var sized in this._sizedFonts.Values)
            sized.ReleaseGlyphs(store.ReleaseAction);
    }

    /// <summary>Lets go of their glyphs first.</summary>
    public void Clear()
    {
        this.ReleaseAllGlyphs();
        this._byRequest.Clear();
        this._sizedFonts.Clear();
    }

    /// <summary>After <see cref="Clear"/>. The old fonts are disposed.</summary>
    public void SwapFonts(FontSet fonts)
    {
        var old = this.Fonts;
        this.Fonts = fonts;
        old.Dispose();
    }
}
