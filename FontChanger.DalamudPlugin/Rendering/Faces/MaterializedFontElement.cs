using System;
using System.Collections.Generic;
using System.Text;

using FontChanger.DalamudPlugin.Rendering.Faces.Sources;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.Rendering.Faces;

/// <summary>
/// An element of a face: its definition and source, at sizes relative to the face's reference size (its first
/// element's): at a drawn size <c>px</c>, it draws at <c>px * Size / reference</c>, and its pixel values (letter spacing,
/// offsets) scale by <c>px / reference</c>.
/// </summary>
internal sealed class MaterializedFontElement(FaceElement def, IFaceSource? source, float referenceSize) : IDisposable
{
    private readonly Dictionary<float, float> _referenceAdvances = [];

    public FaceElement Def { get; } = def;

    /// <summary>Gets the glyphs it draws; null for the game font being replaced (copied from the game's textures).</summary>
    public IFaceSource? Source { get; } = source;

    /// <summary>Gets whether its glyphs are the game font's (it has no source of its own).</summary>
    public bool DrawsGame => this.Source is null;

    /// <summary>Gets whether it draws a codepoint with its source: in its ranges, and its source has the glyph it draws it with.</summary>
    public bool Has(Rune rune) =>
        this.Def.Contains(rune) && this.Source is { } s && s.GetGlyphId(this.GetDrawnCodepoint(rune)) != 0;

    /// <summary>Gets the codepoint it draws a codepoint with (its replacements).</summary>
    public Rune GetDrawnCodepoint(Rune rune) => this.Def.Replacements.TryGetValue(rune, out var to) ? to : rune;

    /// <summary>Gets its size at a drawn size of the face (scaled by its size relative to the face's reference).</summary>
    public float GetPx(float facePx) => facePx * this.Def.Size / referenceSize;

    /// <summary>Gets the letter spacing added to its advances at a drawn size of the face.</summary>
    public int GetLetterSpacing(float facePx) => this.ScalePixels(this.Def.LetterSpacing, facePx);

    /// <summary>Gets its line metrics at a drawn size of the face; none without a source.</summary>
    public (int Ascent, int LineHeight) GetLineMetrics(float facePx) => this.Source?.GetLineMetrics(this.GetPx(facePx)) ?? (0, 0);

    /// <summary>Gets a baseline of its source at a drawn size of the face, or <paramref name="fallback"/>.</summary>
    public float GetBaselineOrDefault(float facePx, Baseline baseline, float fallback) =>
        this.Source is { } s && s.TryGetBaseline(this.GetPx(facePx), baseline, out var y) ? y : fallback;

    /// <summary>Gets how far below the line's top its roman baseline is, with its line's ascent.</summary>
    public float GetRomanBaselineY(float facePx, int ascent) => ascent - this.GetBaselineOrDefault(facePx, Baseline.Roman, 0);

    /// <summary>
    /// Gets how far below the line's top the middle of its ideographic face (or em box) is, with its line's metrics; the
    /// middle of the line if it has neither.
    /// </summary>
    public float GetIdeographicCenterY(float facePx, int ascent, int lineHeight)
    {
        if (this.Source is { } s)
        {
            var px = this.GetPx(facePx);
            if (s.TryGetBaseline(px, Baseline.IdeographicFaceBottom, out var bottom) && s.TryGetBaseline(px, Baseline.IdeographicFaceTop, out var top))
                return ascent - ((bottom + top) / 2);
            if (s.TryGetBaseline(px, Baseline.IdeographicBottom, out var emBottom) && s.TryGetBaseline(px, Baseline.IdeographicTop, out var emTop))
                return ascent - ((emBottom + emTop) / 2);
        }

        return lineHeight / 2f;
    }

    /// <summary>
    /// Places a glyph in its cell (xivres wrapping_fixed_size_font): monospaced (<paramref name="squeeze"/> draws it
    /// narrower), its letter spacing added to the advance, its horizontal offset to the ink, and moved down by its
    /// alignment in the face (<paramref name="face"/>) and its baseline shift. <paramref name="glyph"/>'s advance is the
    /// whole pixels it takes, unadjusted.
    /// </summary>
    public RasterGlyph Wrap<TSqueeze>(RasterGlyph glyph, float facePx, in FaceAlignment face, TSqueeze squeeze)
        where TSqueeze : IGlyphSqueeze, allows ref struct
    {
        var g = this.Monospace(facePx, glyph, squeeze);
        return g with
        {
            Advance = g.Advance + this.GetLetterSpacing(facePx),
            Left = g.Left + this.ScalePixels(this.Def.HorizontalOffset, facePx),
            Top = g.Top + this.GetVerticalShift(facePx, face),
        };
    }

    public void Dispose() => this.Source?.Dispose();

    /// <summary>Gets a pixel value of the face (at its reference size) at a drawn size, rounded.</summary>
    private int ScalePixels(float value, float facePx) => (int)Rounding.Round(value * facePx / referenceSize);

    /// <summary>
    /// Gets how far its glyphs are moved down from the face's baseline at a drawn size: by the face's vertical alignment
    /// of its line box in the face's (xivres merged_fixed_size_font get_vertical_adjustment), and its baseline shift.
    /// </summary>
    private int GetVerticalShift(float facePx, in FaceAlignment face)
    {
        // The element's line box is placed in the face's: its top moved down by the adjustment.
        var (ascent, lineHeight) = this.GetLineMetrics(facePx);
        var adjustment = face.Mode switch
        {
            VerticalAlignment.Top => 0,
            VerticalAlignment.Middle => (face.LineHeight - lineHeight) / 2,
            VerticalAlignment.Bottom => face.LineHeight - lineHeight,
            VerticalAlignment.RomanBaseline => (int)Rounding.Round(face.RomanBaselineY - this.GetRomanBaselineY(facePx, ascent)),
            VerticalAlignment.IdeographicCenter => (int)Rounding.Round(face.IdeographicCenterY - this.GetIdeographicCenterY(facePx, ascent, lineHeight)),
            _ => face.Ascent - ascent,
        };
        return ascent + adjustment - face.Ascent + this.ScalePixels(this.Def.BaselineShift, facePx);
    }

    /// <summary>
    /// Places a glyph (its advance on screen, unadjusted) in a cell as its monospacing says: the advance clamped into its
    /// limits, the ink aligned in the cell, and ink wider than a limited cell drawn narrower with
    /// <paramref name="squeeze"/>. Glyphs that don't advance stay as they are.
    /// </summary>
    private RasterGlyph Monospace<TSqueeze>(float facePx, RasterGlyph glyph, TSqueeze squeeze)
        where TSqueeze : IGlyphSqueeze, allows ref struct
    {
        if (this.Def.Monospacing is not { } mono || glyph.Advance <= 0)
            return glyph;

        var unit = mono.Unit switch
        {
            MonospacingUnit.Pixels => facePx / referenceSize,
            MonospacingUnit.Em => this.GetPx(facePx),
            _ => this.GetReferenceAdvance(facePx, mono.ReferenceCharacter),
        };
        if (unit is not > 0)
            return glyph;

        var min = mono.Min is { } a ? Math.Max(0, (int)Rounding.Round(a * unit)) : 0;
        var max = mono.Max is { } b ? Math.Max(0, (int)Rounding.Round(b * unit)) : int.MaxValue;
        min = Math.Min(min, max);
        var cell = Math.Clamp(glyph.Advance, min, max);
        if (glyph.Width == 0)
            return glyph with { Advance = cell };

        int left;
        if (mono.Max is not null && cell > 0 && glyph.Width > cell)
        {
            // Hinting and rounding make the ink only roughly as narrow as the scale says: narrower scales are tried until
            // it fits.
            var scale = (float)cell / glyph.Width;
            var fitted = glyph;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                fitted = squeeze.Squeeze(scale);
                if (fitted.Width <= cell)
                    break;
                scale *= (float)cell / fitted.Width * 0.98f;
            }

            left = (cell - fitted.Width) >> 1;
            glyph = fitted;
        }
        else
        {
            left = mono.Alignment switch
            {
                MonospacingAlignment.Left => glyph.Left,
                MonospacingAlignment.CenterInk => (cell - glyph.Width) >> 1,
                MonospacingAlignment.Right => glyph.Left + cell - glyph.Advance,
                _ => glyph.Left + ((cell - glyph.Advance) >> 1),
            };
        }

        // The horizontal offset (added after) nudges the ink in its cell, but not left of the pen.
        var offset = this.ScalePixels(this.Def.HorizontalOffset, facePx);
        return glyph with { Advance = cell, Left = Math.Max(0, left + offset) - offset };
    }

    /// <summary>Gets the advance on screen of its monospacing reference character at a drawn size (kept by size).</summary>
    private float GetReferenceAdvance(float facePx, Rune rune)
    {
        if (this._referenceAdvances.TryGetValue(facePx, out var advance))
            return advance;

        advance = 0;
        if (this.Source is { } s && s.GetGlyphId(rune) is var id and not 0)
        {
            var px = this.GetPx(facePx);
            ReadOnlySpan<uint> ids = [id];
            ReadOnlySpan<float> advances = [s.GetAdvance(px, id)];
            advance = s.GetScreenAdvance(px, new(ids, advances));
        }

        this._referenceAdvances.Add(facePx, advance);
        return advance;
    }
}
