using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Rendering.Faces.Sources;
using FontChanger.DalamudPlugin.Rendering.Faces.Sources.Nominal;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Rendering.Text;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.Rendering.Faces;

/// <summary>
/// A face of a preset made usable (xivres merged_fixed_size_font): its elements and their sources, which element draws
/// each grapheme, and its line metrics at a drawn size.
/// </summary>
/// <remarks>
/// <para>Sizes in a face are relative to its first element (<see cref="MaterializedFontElement"/>). The first element gives
/// the line metrics.</para>
/// <para>A codepoint goes to the elements in order: an element has it if it is in its ranges and its source has the
/// glyph it draws it with; AddNew takes it if no element has yet, AddAll always, Replace only from an earlier element.
/// Characters no element has are the game's, or with system fallback, a system font's: an element is made for each font
/// the system's fallback picks, at the primary element's size.</para>
/// </remarks>
internal sealed unsafe class MaterializedFont : IDisposable
{
    private readonly FontServices _services;
    private readonly Face _def;
    private readonly SystemFallbackFonts _fallbackFonts;

    // Whether characters no element has are drawn with the system's fonts (as elements made for them) instead of the
    // game's glyphs.
    private readonly bool _systemFallback;

    // The size the face's sizes are relative to: its first element's.
    private readonly float _referenceSize;

    // The first element drawn from a font: its family is what system fonts are matched against, and its measuring mode
    // (DWRITE_MEASURING_MODE) is the face's, for all its glyphs.
    private readonly MaterializedFontElement? _primary;
    private readonly int _measureMode;

    private readonly MaterializedFontElement[] _elements;
    private readonly MaterializedFontElement _gameElement;
    private readonly Dictionary<(string Family, int Weight, int Stretch, int Style, float Scale), MaterializedFontElement> _fallbackElements = [];
    private readonly Dictionary<(Pointer<AtkFontManager.Font> Game, Rune Rune), MaterializedFontElement?> _elementCache = [];
    private int? _ownGlyphCount;

    /// <summary>
    /// Sets a face up. With <paramref name="systemFallback"/>, characters no element has are drawn with the system fonts
    /// <paramref name="fallbackFonts"/> finds.
    /// </summary>
    public MaterializedFont(FontServices services, Face def, bool systemFallback, SystemFallbackFonts fallbackFonts)
    {
        this._services = services;
        this._def = def;
        this._systemFallback = systemFallback;
        this._fallbackFonts = fallbackFonts;
        this._referenceSize = def.Elements.Count != 0 && def.Elements[0].Size > 0 ? def.Elements[0].Size : 1;
        this._gameElement = new(new() { Size = this._referenceSize, Renderer = Renderer.Game }, null, this._referenceSize);

        var elements = new List<MaterializedFontElement>();
        try
        {
            foreach (var e in def.Elements)
                elements.Add(new(e, CreateSource(e, services, def.Name), this._referenceSize));
        }
        catch
        {
            foreach (var e in elements)
                e.Dispose();
            throw;
        }

        this._elements = elements.ToArray();

        // A font that is installed, with glyph merging around it or not.
        this._primary = Array.Find(
            this._elements,
            e => e.Def.Renderer is Renderer.DirectWrite or Renderer.FreeType && e.Source is not EmptyFaceSource);
        this._measureMode = this._primary?.Def.DirectWrite.MeasureMode ?? 0;

        // The face's text is measured one way, its primary element's; every font of it draws glyphs measured so.
        foreach (var e in this._elements)
            this.SetMeasureMode(e.Source);
    }

    /// <summary>Gets how many codepoints the face's elements draw themselves (not with the game's glyphs); kept once counted.</summary>
    public int OwnGlyphCount
    {
        get
        {
            if (this._ownGlyphCount is { } known)
                return known;
            var drawn = new HashSet<Rune>();
            foreach (var e in this._elements)
            {
                if (e.Source is not { } source)
                    continue;
                foreach (var c in source.Codepoints)
                {
                    if (e.Def.Contains(c))
                        drawn.Add(c);
                }
            }

            this._ownGlyphCount = drawn.Count;
            return drawn.Count;
        }
    }

    /// <summary>
    /// Makes the face of game fonts no preset gives one: the game's glyphs, and for characters the game font lacks, with
    /// <paramref name="systemFallback"/>, system fonts.
    /// </summary>
    public static MaterializedFont CreateGame(FontServices services, bool systemFallback, SystemFallbackFonts fallbackFonts)
    {
        var element = new FaceElement { Size = 1, Renderer = Renderer.Game, Ranges = RuneRange.From(0, 0x10FFFF) };
        return new(services, new() { Name = "(game)", Elements = [element] }, systemFallback, fallbackFonts);
    }

    public void Dispose()
    {
        foreach (var e in this._elements)
            e.Dispose();
        foreach (var e in this._fallbackElements.Values)
            e.Dispose();
        this._fallbackElements.Clear();
    }

    /// <summary>
    /// Gets the element that draws a grapheme (one rune for the per-character path): the first that has all of it,
    /// else the one that has its first rune, else a system font's; false if none does.
    /// </summary>
    /// <remarks>An element whose <see cref="MaterializedFontElement.DrawsGame"/> means the game's glyph is used.</remarks>
    public bool TryGetElement(ReadOnlySpan<Rune> grapheme, AtkFontManager.Font* game, [NotNullWhen(true)] out MaterializedFontElement? element)
    {
        element = null;
        if (grapheme.IsEmpty)
            return false;

        if (grapheme.Length == 1)
        {
            var key = ((Pointer<AtkFontManager.Font>)game, grapheme[0]);
            if (!this._elementCache.TryGetValue(key, out element))
            {
                element = this.FindElement(grapheme, game);
                this._elementCache[key] = element;
            }

            return element is not null;
        }

        // A grapheme goes to an element that has all of it, else to its first rune's.
        element = this.FindAssigned(grapheme, game) ?? (this.TryGetElement(grapheme[..1], game, out var first) ? first : null);
        return element is not null;
    }

    /// <summary>Gets the line metrics at a drawn size: the first element's (or the game font's), in whole pixels.</summary>
    public (int Ascent, int LineHeight) GetLineMetrics(float px, AtkFontManager.Font* game)
    {
        var (ascent, lineHeight) = this._elements.Length == 0 ? (0, 0) : GetElementMetrics(this._elements[0], px, game);
        if (lineHeight <= 0)
        {
            // Nothing to take them from: proportions of Segoe UI.
            ascent = (int)MathF.Ceiling(px * 1.08f);
            lineHeight = (int)MathF.Ceiling(px * 1.33f);
        }

        return (ascent, lineHeight);
    }

    /// <summary>Gets where the face's line is at a drawn size, which its elements are aligned to.</summary>
    public FaceAlignment GetAlignment(float px, AtkFontManager.Font* game)
    {
        var (ascent, lineHeight) = this.GetLineMetrics(px, game);
        var first = this._elements.Length == 0 ? null : this._elements[0];
        return new(
            ascent,
            lineHeight,
            first?.GetRomanBaselineY(px, ascent) ?? ascent,
            first?.GetIdeographicCenterY(px, ascent, lineHeight) ?? lineHeight / 2f,
            this._def.Alignment);
    }

    /// <summary>
    /// Rasterizes a codepoint at a drawn size, in its cell (<see cref="MaterializedFontElement.Wrap"/>); false if the game's
    /// glyph is to be used.
    /// </summary>
    public bool TryRasterize(Rune rune, float px, AtkFontManager.Font* game, out RasterGlyph glyph)
    {
        glyph = default;
        if (!this.TryGetElement([rune], game, out var element) || element.Source is not { } source)
            return false;

        var id = source.GetGlyphId(element.GetDrawnCodepoint(rune));
        if (id == 0)
            return false;

        var elementPx = element.GetPx(px);
        ReadOnlySpan<uint> ids = [id];
        ReadOnlySpan<float> advances = [source.GetAdvance(elementPx, id)];
        var run = new GlyphRun(ids, advances);
        if (source.Rasterize(elementPx, run) is not { } raw)
            return false;

        glyph = element.Wrap(raw, px, this.GetAlignment(px, game), source, run);
        return true;
    }

    /// <summary>
    /// Makes an element's source (xivres's fonts, as FontChanger's ReplacementElement::GetBaseFont makes them): by its
    /// renderer, with its gamma, and glyph merging around it; null for the game font being replaced. A font that isn't
    /// installed has no glyphs.
    /// </summary>
    private static IFaceSource? CreateSource(FaceElement def, FontServices services, string faceName)
    {
        IFaceSource source;
        switch (def.Renderer)
        {
            // FontChanger's game font elements name a family, drawn from the game's files (no glyphs if they can't be
            // read); those made here name none, and use the game font being replaced.
            case Renderer.Game when def.Lookup.Name.Length != 0:
                source = (IFaceSource?)GameFontDataFaceSource.Create(def, faceName) ?? new EmptyFaceSource(def.Size, def.EmptyAscent, def.EmptyLineHeight);
                break;

            case Renderer.Game:
                return null;

            case Renderer.DirectWrite or Renderer.FreeType:
                source = (IFaceSource?)OutlineFaceSource.Create(services, def, faceName) ?? new EmptyFaceSource(0, 0, 0);
                break;

            case Renderer.GlyphImages:
                source = ImageFaceSource.Create(def, services.Images);
                break;

            default:
                source = new EmptyFaceSource(def.Size, def.EmptyAscent, def.EmptyLineHeight);
                break;
        }

        // As in FontChanger, glyphs are merged only around fonts that draw outlines or images.
        if (def.GlyphMerging is not { } merging || def.Renderer is not (Renderer.DirectWrite or Renderer.FreeType or Renderer.GlyphImages))
            return source;
        return MergingFaceSource.Create(source, def, merging, services, faceName);
    }

    /// <summary>Gets an element's line metrics at a drawn size: its source's, or the game font's for the game's glyphs.</summary>
    private static (int Ascent, int LineHeight) GetElementMetrics(MaterializedFontElement element, float px, AtkFontManager.Font* game)
    {
        if (!element.DrawsGame)
            return element.GetLineMetrics(px);
        if (game is null || game->Size <= 0)
            return (0, 0);
        var scale = element.GetPx(px) / game->Size;
        return ((int)Rounding.Round(game->Ascent * scale), (int)Rounding.Round(game->LineHeight * scale));
    }

    /// <summary>Gets whether an element has every rune of a grapheme: with its source, or in the game font for the game's glyphs.</summary>
    private static bool Has(MaterializedFontElement element, ReadOnlySpan<Rune> grapheme, AtkFontManager.Font* game)
    {
        foreach (var rune in grapheme)
        {
            var has = element.DrawsGame
                          ? element.Def.Contains(rune) && game is not null && game->FindGlyph(rune) is not null
                          : element.Has(rune);
            if (!has)
                return false;
        }

        return true;
    }

    /// <summary>Gives a source the face's measuring mode, if it draws with fonts.</summary>
    private void SetMeasureMode(IFaceSource? source)
    {
        IEnumerable<OutlineFaceSource> outlines = source switch
        {
            OutlineFaceSource outline => [outline],
            MergingFaceSource merging => merging.OutlineSources,
            _ => [],
        };
        foreach (var outline in outlines)
            outline.MeasureMode = this._measureMode;
    }

    /// <summary>Finds the element of a grapheme: the one assigned it, else a system font's or the game's.</summary>
    private MaterializedFontElement? FindElement(ReadOnlySpan<Rune> grapheme, AtkFontManager.Font* game)
    {
        if (this.FindAssigned(grapheme, game) is { } assigned)
            return assigned;
        return this._systemFallback ? this.GetFallbackElement(grapheme) : this._gameElement;
    }

    /// <summary>Gets the element the face's elements assign a grapheme to, in order by their merge modes; null if none.</summary>
    private MaterializedFontElement? FindAssigned(ReadOnlySpan<Rune> grapheme, AtkFontManager.Font* game)
    {
        MaterializedFontElement? assigned = null;
        foreach (var e in this._elements)
        {
            var mode = e.Def.MergeMode;
            if ((mode == MergeMode.AddNew && assigned is not null) || (mode == MergeMode.Replace && assigned is null))
                continue;
            if (Has(e, grapheme, game))
                assigned = e;
        }

        return assigned;
    }

    /// <summary>
    /// Gets the element of the system font the system's fallback draws a grapheme with, made on first use at the
    /// primary element's size (scaled as the fallback says); null if no font has it.
    /// </summary>
    private MaterializedFontElement? GetFallbackElement(ReadOnlySpan<Rune> grapheme)
    {
        Span<char> text = stackalloc char[grapheme.Length * 2];
        var length = 0;
        foreach (var rune in grapheme)
            length += rune.EncodeToUtf16(text[length..]);

        var baseFamily = this._primary?.Def.Lookup.Name ?? SystemFallbackFonts.DefaultBaseFamily;
        if (this._fallbackFonts.Find(text[..length], baseFamily) is not (var lookup, var scale))
            return null;

        var key = (lookup.Name.ToLowerInvariant(), lookup.Weight, lookup.Stretch, lookup.Style, scale);
        if (!this._fallbackElements.TryGetValue(key, out var element))
        {
            var primary = this._primary?.Def;
            var def = new FaceElement
            {
                Size = (primary?.Size ?? this._referenceSize) * scale,
                Renderer = Renderer.DirectWrite,
                Lookup = lookup,
                Ranges = RuneRange.From(0, 0x10FFFF),
                DirectWrite = primary?.DirectWrite ?? DirectWriteParams.Default,
            };
            var source = OutlineFaceSource.Create(this._services, def, this._def.Name);
            this.SetMeasureMode(source);
            element = new(def, source, this._referenceSize);
            this._fallbackElements.Add(key, element);
        }

        return element.Source is not null && element.Has(grapheme[0]) ? element : null;
    }
}
