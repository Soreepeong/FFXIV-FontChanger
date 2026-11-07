using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

namespace FontChanger.DalamudPlugin.Fonts.Faces;

/// <summary>
/// Sources shared by the elements of one set of fonts that draw the same face the same way, disposed with the set.
/// A set's fonts and their sources are used on one thread at a time.
/// </summary>
internal sealed class FaceSourceCache : IDisposable
{
    private readonly Dictionary<Key, IFaceSource> _shared = [];
    private readonly List<IFaceSource> _owned = [];

    /// <summary>Returns <paramref name="make"/>'s source, shared with later calls of the same key unless it is null.</summary>
    public IFaceSource? GetOrCreate(FontElement def, int measureMode, Func<IFaceSource?> make)
    {
        var key = KeyOf(def, measureMode);
        if (this._shared.TryGetValue(key, out var known))
            return known;

        var source = make();
        if (source is not null)
            this._shared.Add(key, this.Own(source));
        return source;
    }

    /// <summary>For a source of an element's own, disposed with the shared ones.</summary>
    public T Own<T>(T source)
        where T : IFaceSource
    {
        this._owned.Add(source);
        return source;
    }

    public void Dispose()
    {
        foreach (var source in this._owned)
            source.Dispose();
        this._owned.Clear();
        this._shared.Clear();
    }

    // Element-level settings (ranges, offsets, merge mode, glyph merging) are left out.
    private static Key KeyOf(FontElement def, int measureMode)
    {
        // Outlines and images are drawn at the element's size, which the game's and empty fonts are made at.
        var sized = def.Source is not (ElementSource.DirectWrite or ElementSource.FreeType or ElementSource.GlyphImages);
        var lookup = def.Lookup;
        var images = def.GlyphImages is { } g
                         ? new ImagesKey(
                             g.Folder,
                             g.UnitsPerEm,
                             g.BaselineY,
                             g.Ascent,
                             g.LineHeight,
                             g.BitmapCoverage,
                             g.EmbeddedMetadata?.GetRawText(),
                             g.Embedded.Count == 0 ? null : g.Embedded)
                         : (ImagesKey?)null;
        return new(
            def.Source,
            lookup.FamilyName,
            lookup.Weight,
            lookup.Stretch,
            lookup.Style,
            lookup.LanguageCode,
            lookup.AllowSynthesis,
            string.Join(',', lookup.Features.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}")),
            string.Join(',', lookup.Variations.OrderBy(v => v.Key).Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.Key}={v.Value}"))),
            sized ? def.Size : 0,
            def.Gamma,
            def.Transform,
            def.EmptyAscent,
            def.EmptyLineHeight,
            def.DirectWrite,
            def.FreeType,
            images,
            measureMode);
    }

    private readonly record struct Key(
        ElementSource Source,
        string Family,
        int Weight,
        int Stretch,
        int Style,
        string LanguageCode,
        bool? AllowSynthesis,
        string Features,
        string Variations,
        float Size,
        float Gamma,
        Matrix2x2 Transform,
        float EmptyAscent,
        float EmptyLineHeight,
        DirectWriteParams DirectWrite,
        FreeTypeParams FreeType,
        ImagesKey? Images,
        int MeasureMode);

    // Embedded glyphs compare by reference.
    private readonly record struct ImagesKey(
        string Folder,
        float? UnitsPerEm,
        float? BaselineY,
        float? Ascent,
        float? LineHeight,
        ImageCoverageMode Coverage,
        string? Metadata,
        object? Embedded);
}
