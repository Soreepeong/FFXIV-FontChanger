using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

using FontChanger.DalamudPlugin.Fonts.Faces.Sources;
using FontChanger.DalamudPlugin.Fonts.Faces.Sources.Nominal;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Fonts.Text;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.Fonts.Faces;

/// <summary>
/// As xivres merged_fixed_size_font, elements take codepoints in order: AddNew those no earlier element has, AddAll any,
/// Replace only those an earlier element has.
/// </summary>
internal sealed unsafe class MaterializedFont : IDisposable
{
    private readonly FontServices _services;
    private readonly Font _def;
    private readonly SystemFallbackFonts _fallbackFonts;
    private readonly bool _systemFallback;

    // Shared by the fonts made together, and owns their sources.
    private readonly FaceSourceCache _sources;

    // The font's sizes are relative to its first element's.
    private readonly float _referenceSize;

    // The first installed font's element: system fallback matches its family, and its DWRITE_MEASURING_MODE is the whole font's.
    private readonly MaterializedFontElement? _primary;
    private readonly int _measureMode;

    // Without the elements that couldn't be set up.
    private readonly MaterializedFontElement[] _elements;
    private readonly MaterializedFontElement _gameElement;
    private readonly Dictionary<(string Family, int Weight, int Stretch, int Style, float Scale), MaterializedFontElement> _fallbackElements = [];

    // Fallback fonts fitted as chain items would be, on the first character each draws, and what fitting them opens.
    private readonly Dictionary<(string Family, int Weight, int Stretch, int Style), MaterializedFontElement[]> _fittedFallback = [];
    private readonly Dictionary<(string Family, int Weight, int Stretch, int Style), FontProbe?> _probes = [];
    private IFontProbe? _drawnProbe;
    private (IFontProbe? Probe, bool Opened) _gameProbe;
    private readonly Dictionary<(Pointer<AtkFontManager.Font> Game, Rune Rune), MaterializedFontElement?> _elementCache = [];
    private readonly Dictionary<(float Px, Pointer<AtkFontManager.Font> Game), FontAlignment> _alignments = [];
    private int? _ownCodepointCount;

    /// <summary>An element that can't be set up is left out with a warning; throws if none of them can.</summary>
    public MaterializedFont(FontServices services, Font def, bool systemFallback, SystemFallbackFonts fallbackFonts, FaceSourceCache sources)
    {
        this._services = services;
        this._def = def;
        this._systemFallback = systemFallback;
        this._fallbackFonts = fallbackFonts;
        this._sources = sources;
        this._referenceSize = def.Elements.Count != 0 && def.Elements[0].Size > 0 ? def.Elements[0].Size : 1;
        this._gameElement = new(new() { Size = this._referenceSize, Source = ElementSource.Game }, null, this._referenceSize);

        // The first installed font's measuring mode is known before the sources are made with it, as shared sources can't change it afterwards.
        var installed = def.Elements.Select(e => e.Source is ElementSource.DirectWrite or ElementSource.FreeType && IsInstalled(services, e.Lookup)).ToArray();
        var first = Array.IndexOf(installed, true);
        this._measureMode = first < 0 ? 0 : def.Elements[first].DirectWrite.MeasureMode;

        var elements = new List<MaterializedFontElement>();
        for (var i = 0; i < def.Elements.Count; i++)
        {
            var e = def.Elements[i];
            try
            {
                var element = new MaterializedFontElement(e, this.CreateSource(e), this._referenceSize);
                elements.Add(element);
                if (this._primary is null && installed[i])
                    this._primary = element;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "{font}: element {index} ({family}) can't be set up and is left out", def.Name, i + 1, e.Lookup.FamilyName);
            }
        }

        if (elements.Count == 0 && def.Elements.Count != 0)
            throw new InvalidOperationException("None of its elements can be set up.");
        this._elements = elements.ToArray();
    }

    /// <summary>Excludes codepoints drawn with the game's glyphs.</summary>
    public int OwnCodepointCount => this._ownCodepointCount ??= this.CountOwnCodepoints();

    public static MaterializedFont CreateGame(FontServices services, bool systemFallback, SystemFallbackFonts fallbackFonts, FaceSourceCache sources)
    {
        var element = new FontElement { Size = 1, Source = ElementSource.Game, Ranges = RuneRange.From(0, 0x10FFFF) };
        return new(services, new() { Name = "(game)", Elements = [element] }, systemFallback, fallbackFonts, sources);
    }

    /// <summary>The sources are left to the <see cref="FaceSourceCache"/>.</summary>
    public void Dispose()
    {
        foreach (var p in this._probes.Values)
            p?.Dispose();
        this._probes.Clear();
    }

    /// <summary>A game font was rebuilt, possibly from another file, so which of its glyphs exist is looked up again.</summary>
    public void ForgetGameFonts()
    {
        this._elementCache.Clear();
        this._alignments.Clear();
    }

    /// <summary>Does the slow parts of the first lookups ahead, on the thread the font is made on.</summary>
    public void Prepare()
    {
        _ = this.OwnCodepointCount;
        if (!this._systemFallback || this.GetGameSize() <= 0)
            return;
        try
        {
            _ = this.OpenFittingProbes();
        }
        catch (Exception)
        {
            // GetFittedFallback tries again, and logs why.
        }
    }

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

        element = this.FindAssigned(grapheme, game) ?? (this.TryGetElement(grapheme[..1], game, out var first) ? first : null);
        return element is not null;
    }

    public (int Ascent, int LineHeight) GetLineMetrics(float px, AtkFontManager.Font* game)
    {
        var (ascent, lineHeight) = this._elements.Length == 0 ? (0, 0) : GetElementMetrics(this._elements[0], px, game);
        if (lineHeight <= 0)
        {
            // Segoe UI's proportions.
            ascent = (int)MathF.Ceiling(px * 1.08f);
            lineHeight = (int)MathF.Ceiling(px * 1.33f);
        }

        return (ascent, lineHeight);
    }

    public FontAlignment GetAlignment(float px, AtkFontManager.Font* game)
    {
        var key = (px, (Pointer<AtkFontManager.Font>)game);
        if (this._alignments.TryGetValue(key, out var known))
            return known;

        var (ascent, lineHeight) = this.GetLineMetrics(px, game);
        var first = this._elements.Length == 0 ? null : this._elements[0];
        return this._alignments[key] = new(
            ascent,
            lineHeight,
            first?.GetRomanBaselineY(px, ascent) ?? ascent,
            first?.GetIdeographicCenterY(px, ascent, lineHeight) ?? lineHeight / 2f,
            this._def.Alignment);
    }

    /// <summary>False means the game's glyph is used.</summary>
    public bool TryRasterize(Rune rune, float px, AtkFontManager.Font* game, bool bold, out RasterGlyph glyph)
    {
        glyph = default;
        if (!this.TryGetElement([rune], game, out var element) || element.Source is not { } source)
            return false;
        if (bold)
            source = source.GetBold();

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

    private static bool IsInstalled(FontServices services, LookupStruct lookup)
    {
        if (!services.TryFindFont(lookup, out var match))
            return false;
        match.Dispose();
        return true;
    }

    /// <summary>As FontChanger's FontElement::GetBaseFont; null for the replaced game font.</summary>
    private IFaceSource? CreateSource(FontElement def)
    {
        // FontChanger's game font elements name a family read from the game's files; those made here name none and mean
        // the replaced font.
        if (def is { Source: ElementSource.Game, Lookup.FamilyName.Length: 0 })
            return null;

        var fontName = this._def.Name;
        var source = this._sources.GetOrCreate(
            def,
            this._measureMode,
            () => {
                IFaceSource made = def.Source switch
                {
                    ElementSource.Game => (IFaceSource?)GameFontDataFaceSource.Create(def, fontName) ?? new EmptyFaceSource(def.Size, def.EmptyAscent, def.EmptyLineHeight),
                    ElementSource.DirectWrite or ElementSource.FreeType => (IFaceSource?)OutlineFaceSource.Create(this._services, def, fontName) ?? new EmptyFaceSource(0, 0, 0),
                    ElementSource.GlyphImages => ImageFaceSource.Create(def, this._services.Images),
                    _ => new EmptyFaceSource(def.Size, def.EmptyAscent, def.EmptyLineHeight),
                };
                made.MeasureMode = this._measureMode;
                return made;
            })!;

        // As in FontChanger, glyphs are merged only around fonts that draw outlines or images.
        if (def.GlyphMerging is not { } merging || def.Source is not (ElementSource.DirectWrite or ElementSource.FreeType or ElementSource.GlyphImages))
            return source;
        var merged = this._sources.Own(MergingFaceSource.Create(source, def, merging, this._services, fontName));
        merged.MeasureMode = this._measureMode;
        return merged;
    }

    // The size of the game font of the font's name; 0 if there is none.
    private float GetGameSize() =>
        Math.Max(
            0,
            FontNames.FontNamesOf(Preset.FamilyOf(this._def.Name))
                     .FirstOrDefault(f => string.Equals(f.Name, this._def.Name, StringComparison.OrdinalIgnoreCase))
                     .Size);

    /// <summary>Returns the drawn probe.</summary>
    private IFontProbe OpenFittingProbes()
    {
        if (!this._gameProbe.Opened)
            this._gameProbe = (GameFontProbe.Open(this._def.Name), true);
        return this._drawnProbe ??= FontsFromSystemFont.ProbeFont(this._def, this.TryOpenProbe);
    }

    private static (int Ascent, int LineHeight) GetElementMetrics(MaterializedFontElement element, float px, AtkFontManager.Font* game)
    {
        if (!element.DrawsGame)
            return element.GetLineMetrics(px);
        if (game is null || game->Size <= 0)
            return (0, 0);
        var scale = element.GetPx(px) / game->Size;
        return ((int)Misc.StdRound(game->Ascent * scale), (int)Misc.StdRound(game->LineHeight * scale));
    }

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

    private int CountOwnCodepoints()
    {
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

        return drawn.Count;
    }

    private MaterializedFontElement? FindElement(ReadOnlySpan<Rune> grapheme, AtkFontManager.Font* game)
    {
        if (this.FindAssigned(grapheme, game) is { } assigned)
            return assigned;
        return this._systemFallback ? this.GetFallbackElement(grapheme) : this._gameElement;
    }

    private MaterializedFontElement? FindAssigned(ReadOnlySpan<Rune> grapheme, AtkFontManager.Font* game)
    {
        MaterializedFontElement? assigned = null;
        foreach (var e in this._elements)
        {
            var mode = e.Def.MergeMode;
            if ((mode == CodepointMergeMode.AddNew && assigned is not null) || (mode == CodepointMergeMode.Replace && assigned is null))
                continue;
            if (Has(e, grapheme, game))
                assigned = e;
        }

        return assigned;
    }

    private MaterializedFontElement? GetFallbackElement(ReadOnlySpan<Rune> grapheme)
    {
        Span<char> text = stackalloc char[grapheme.Length * 2];
        var length = 0;
        foreach (var rune in grapheme)
            length += rune.EncodeToUtf16(text[length..]);

        var baseFamily = this._primary?.Def.Lookup.FamilyName ?? SystemFallbackFonts.DefaultBaseFamily;
        if (this._fallbackFonts.Find(text[..length], baseFamily) is not (var lookup, var scale))
            return null;

        var fitted = this.GetFittedFallback(lookup);
        for (var i = fitted.Length - 1; i >= 0; i--)
        {
            if (fitted[i].Has(grapheme[0]))
                return fitted[i];
        }

        // Private-use characters, and fonts that aren't a game font's, keep DirectWrite's scale.
        var key = (lookup.FamilyName.ToLowerInvariant(), lookup.Weight, lookup.Stretch, lookup.Style, scale);
        if (!this._fallbackElements.TryGetValue(key, out var element))
        {
            var primary = this._primary?.Def;
            var def = new FontElement
            {
                Size = (primary?.Size ?? this._referenceSize) * scale,
                Source = ElementSource.DirectWrite,
                Lookup = lookup,
                Ranges = RuneRange.From(0, 0x10FFFF),
                DirectWrite = primary?.DirectWrite ?? DirectWriteParams.Default,
            };
            var source = this.CreateSource(def);

            // Rendered as the fallback font's own bitmaps and gasp table want at its size, not as the primary element's.
            if (source is OutlineFaceSource outline)
            {
                using var probe = outline.CreateProbe();
                var renderMode = FontsFromSystemFont.GetRenderMode(probe, def.Size);
                if (renderMode != def.DirectWrite.RenderMode)
                {
                    def = def with { DirectWrite = def.DirectWrite with { RenderMode = renderMode } };
                    source = this.CreateSource(def);
                }
            }

            element = new(def, source, this._referenceSize);
            this._fallbackElements.Add(key, element);
        }

        return element.Source is not null && element.Has(grapheme[0]) ? element : null;
    }

    // Fitted to the size the font's chain items were made at, the game font's.
    private MaterializedFontElement[] GetFittedFallback(LookupStruct lookup)
    {
        var key = (lookup.FamilyName.ToLowerInvariant(), lookup.Weight, lookup.Stretch, lookup.Style);
        if (this._fittedFallback.TryGetValue(key, out var fitted))
            return fitted;

        fitted = [];
        try
        {
            var size = this.GetGameSize();
            if (size > 0)
            {
                var drawnProbe = this.OpenFittingProbes();
                fitted = FontsFromSystemFont.MakeFallbackElements(this._def, size, lookup, this.TryOpenProbe, drawnProbe, this._gameProbe.Probe)
                                            .Select(e => new MaterializedFontElement(e, this.CreateSource(e), this._referenceSize))
                                            .ToArray();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Fitting {family} for {font} failed; it is drawn at DirectWrite's scale", lookup.FamilyName, this._def.Name);
        }

        this._fittedFallback.Add(key, fitted);
        return fitted;
    }

    private bool TryOpenProbe(LookupStruct lookup, [NotNullWhen(true)] out IFontProbe? probe)
    {
        var key = (lookup.FamilyName.ToLowerInvariant(), lookup.Weight, lookup.Stretch, lookup.Style);
        if (!this._probes.TryGetValue(key, out var cached))
        {
            this._services.TryOpenProbe(lookup, out cached);
            this._probes.Add(key, cached);
        }

        probe = cached;
        return probe is not null;
    }
}
