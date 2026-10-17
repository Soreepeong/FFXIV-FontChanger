using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using FFXIVClientStructs.FFXIV.Component.GUI;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Faces;
using FontChanger.DalamudPlugin.Rendering.Fonts;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering;

// Inside the namespace, so that Pointer<T> is XIVCS's over TerraFX's.
using FFXIVClientStructs.Interop;

/// <summary>
/// Shapes the game's text with DirectWrite (ligatures, contextual alternates, kerning, marks, fallback per script), and
/// answers the game's per-character glyph lookups from the result.
/// </summary>
/// <remarks>
/// The game lays text out one character at a time (LayOutCharacter); the run a character starts is shaped once, to the end
/// of the line. Each shaped cluster becomes one cell, advancing by the whole-pixel difference of its rounded start and end;
/// right-to-left runs and glyphs a font lacks are left to the per-character path. A run ends at an italic macro (<see cref="ItalicMode"/>).
/// </remarks>
internal sealed unsafe class TextShaper : IDisposable, IGlyphRunSink
{
    // The game's icon font: private-use characters go to it before the system fallback, which would pick an icon font
    // of Windows' own (Segoe Fluent Icons) for them.
    internal const string IconFamily = "XIV AXIS Std ATK";

    private const int MaxRunBytes = 4096;
    private const int MaxActiveRuns = 8;
    private const int MaxCachedRuns = 8192;

    // Cells are made at a quarter-pixel resolution of the cluster's position.
    private const float SubpixelSteps = 4;

    private readonly GlyphRasterizer _rasterizer;
    private readonly FontReplacer _replacer;
    private readonly Dictionary<(ReplacementFace? Face, int HalfPx), ComPtr<IDWriteTextFormat3>> _formats = [];
    private readonly Dictionary<(ulong Hash, int Length, FontReplacer.SizedFont Sized, ItalicMode Italic), ShapedRun> _runs = [];

    // Faces without their synthesized oblique, by face; and the italic face of a face's family (null for none), by face.
    private readonly Dictionary<Pointer<IDWriteFontFace5>, Pointer<IDWriteFontFace5>> _uprightFaces = [];
    private readonly Dictionary<Pointer<IDWriteFontFace5>, Pointer<IDWriteFontFace5>> _italicFaces = [];
    // Cells by their sized font too: a cell is placed in its family's atlas, with its metrics, and families may share a face.
    private readonly Dictionary<(RasterKey Raster, FontReplacer.SizedFont Sized, int Pad, int Advance), Pointer<AtkFontManager.Glyph>> _cells = [];
    private readonly Dictionary<(FontReplacer.SizedFont Sized, int Advance), Pointer<AtkFontManager.Glyph>> _spacers = [];

    // The faces clusters are drawn from, each with a reference of the shaper's.
    private readonly Dictionary<Pointer<IDWriteFontFace5>, ComPtr<IDWriteFontFace5>> _keptFaces = [];
    private readonly List<ActiveRun> _active = [];
    private readonly List<(int Byte, int Owner)> _absorbed = [];

    // The clusters of the layout being shaped, and coverage of clusters by glyphs and position.
    private readonly List<Cluster> _clusters = [];
    private readonly Dictionary<RasterKey, RasterGlyph> _rasters = [];
    private bool _clusterGap;

    // The system's fallback, private-use characters to the game's icon font first. A face without system fallback lays
    // out each character in the font of its element only (GlyphRasterizer.NoFallback).
    private ComPtr<IDWriteFontFallback1> _fallback;

    // The glyph of no width given to a cluster's characters after the first.
    private readonly Pointer<AtkFontManager.Glyph> _emptyGlyph;

    // While shaping a run: its text, and each code unit's byte offset, glyph source and whether it is in italics.
    private char[] _text = new char[256];
    private int[] _textToByte = new int[256];
    private FontReplacer.GlyphSource[] _textSource = new FontReplacer.GlyphSource[256];
    private bool[] _textItalic = new bool[256];
    private readonly bool[] _byteItalic = new bool[MaxRunBytes];
    private byte* _shapingBytes;
    private Pointer<AtkFontManager.Glyph>[]? _shapingGlyphs;
    private FontReplacer.SizedFont? _shapingSized;
    private ItalicMode _shapingItalic;

    // How far the pen has moved from where the layout put it: by the difference of each cell's advance and the layout's,
    // as elements' emboldening, transformations, monospacing and letter spacing make it, which DirectWrite doesn't know of.
    private float _penShift;

    public TextShaper(GlyphRasterizer rasterizer, FontReplacer replacer, Pointer<AtkFontManager.Glyph> emptyGlyph)
    {
        this._rasterizer = rasterizer;
        this._replacer = replacer;
        this._emptyGlyph = emptyGlyph;
        this._fallback = CreateSystemFallback(rasterizer);
    }

    /// <summary>Releases the text formats, which depend on the faces (before the faces change).</summary>
    public void ClearFormats()
    {
        foreach (var format in this._formats.Values)
            format.Get()->Release();
        this._formats.Clear();
    }

    /// <summary>
    /// Gets the glyph for the character at <paramref name="p"/> as shaped in its run, in the italics it is in, or 0 to
    /// look it up per character.
    /// </summary>
    public Pointer<AtkFontManager.Glyph> TryGetGlyph(FontReplacer.SizedFont sized, byte* p, ItalicMode italic)
    {
        for (var i = this._active.Count - 1; i >= 0; i--)
        {
            var run = this._active[i];
            if (run.Sized == sized && p >= run.Start && p < run.End && run.ItalicAt(p) == italic && run.Matches(p))
                return run.Glyphs[p - run.Start];
        }

        var reused = this._active.Count == MaxActiveRuns ? this._active[0] : null;
        if (!this.ShapeFrom(sized, p, italic, ref reused))
            return default;
        if (this._active.Count == MaxActiveRuns)
            this._active.RemoveAt(0);
        this._active.Add(reused!);
        return reused!.Glyphs[0];
    }

    /// <summary>Forgets every shaped run and cell (their glyphs are about to be freed).</summary>
    public void Clear()
    {
        this._active.Clear();
        this._runs.Clear();
        this._cells.Clear();
        this._spacers.Clear();
        this._rasters.Clear();
    }

    public void Dispose()
    {
        this.Clear();
        this.ClearFormats();
        this._uprightFaces.Clear();
        this._italicFaces.Clear();
        foreach (var face in this._keptFaces.Values)
            face.Get()->Release();
        this._keptFaces.Clear();
        this._fallback.Dispose();
    }

    void IGlyphRunSink.OnGlyphRun(float baselineX, IDWriteFontFace5* runFace, in DWRITE_GLYPH_RUN run, in DWRITE_GLYPH_RUN_DESCRIPTION description)
    {
        // The game draws in logical order: right-to-left runs stay per character, and break the line's clusters.
        if (this._shapingGlyphs is null || this._shapingSized is null)
            return;
        if ((run.bidiLevel & 1) != 0)
        {
            this._clusterGap = true;
            return;
        }

        var clusterMap = description.clusterMap;
        var length = (int)description.stringLength;
        var start = (int)description.textPosition;
        var pen = baselineX + this._penShift;
        for (var k = 0; k < length;)
        {
            var glyphStart = clusterMap[k];
            var k2 = k + 1;
            while (k2 < length && clusterMap[k2] == glyphStart)
                k2++;
            var glyphEnd = k2 < length ? clusterMap[k2] : (ushort)run.glyphCount;
            var count = glyphEnd - glyphStart;

            // Characters the game draws (by the face, or as none of its elements has them), and those of elements not
            // drawn from their fonts (merged glyphs, glyph images), stay per character. Others are an element's whose
            // glyphs are shaped, or the system fonts' (no element).
            var sized = this._shapingSized;
            var source = this._textSource[start + k];
            var element = source.Element;
            var font = element?.Shaped;
            var missing = element is not null && font is null;

            // An element's glyphs are drawn from its own face (simulations, axis values), and FreeType's emboldening
            // advances them further.
            var face = font is not null ? font.GetRunFace(runFace, run.fontEmSize) : runFace;
            if (!this._keptFaces.ContainsKey(face))
                this._keptFaces.Add(face, new(face));
            var extra = font?.GetExtraAdvance(run.fontEmSize) ?? 0;
            var advances = new float[count];
            var advance = 0f;
            var layoutAdvance = 0f;
            for (var g = glyphStart; g < glyphEnd; g++)
            {
                advances[g - glyphStart] = run.glyphAdvances[g] + extra;
                advance += advances[g - glyphStart];
                layoutAdvance += run.glyphAdvances[g];
                missing |= run.glyphIndices[g] == 0;
            }

            // Italics: a real italic stays, a synthesized oblique is drawn upright for the game to shear.
            var glyphs = new ReadOnlySpan<ushort>(run.glyphIndices + glyphStart, count).ToArray();
            var offsets = run.glyphOffsets is null ? null : new ReadOnlySpan<DWRITE_GLYPH_OFFSET>(run.glyphOffsets + glyphStart, count).ToArray();
            var realItalic = false;
            if (!missing && this._shapingItalic == ItalicMode.Real && this._textItalic[start + k])
            {
                realItalic = IsRealItalic(face);
                if (!realItalic)
                    face = this.GetUprightFace(face);
            }
            else if (!missing && this._shapingItalic == ItalicMode.Images && count == 1 &&
                     Rune.DecodeFromUtf16(this._text.AsSpan(start + k, k2 - k), out var rune, out var used) == System.Buffers.OperationStatus.Done &&
                     used == k2 - k)
            {
                var italic = this.GetItalicFace(face);
                var codepoint = (uint)rune.Value;
                ushort index = 0;
                if (italic is not null)
                    italic->GetGlyphIndices(&codepoint, 1, &index).ThrowOnError();
                if (index != 0)
                {
                    face = italic;
                    glyphs = [index];
                    offsets = null;
                    realItalic = true;
                }
            }

            var x0 = Rounding.Round(pen);
            var width = advance * (font?.Transform.M11 ?? 1);
            var spacing = source.Face.GetLetterSpacing(element is { DrawsGame: false } ? element : null, sized.Px);
            var cluster = new Cluster
            {
                Face = face,
                EmSize = run.fontEmSize,
                Glyphs = glyphs,
                Advances = advances,
                Offsets = offsets,
                RealItalic = realItalic,
                Origin = Rounding.Round((pen - x0) * SubpixelSteps) / SubpixelSteps,
                X0 = (int)x0,
                TextStart = start + k,
                TextEnd = start + k2,
                Source = source,
                Element = element,
                Missing = missing,
                AfterGap = this._clusterGap,
            };

            float next;
            if (!missing && font is not null && element!.Def.Monospacing is not null)
            {
                var rawAdvance = (int)Rounding.Round(width);
                cluster.Cell = source.Face.Wrap(
                    element,
                    this.RasterizeCluster(cluster, rawAdvance, 1),
                    sized.Px,
                    source.Game,
                    s => this.RasterizeCluster(cluster, rawAdvance, s));
                cluster.X1 = cluster.X0 + cluster.Cell.Value.Advance;
                next = cluster.X1;
            }
            else
            {
                cluster.X1 = (int)Rounding.Round(pen + width) + spacing;
                next = pen + width + spacing;
            }

            this._clusters.Add(cluster);
            this._clusterGap = false;

            this._penShift += next - pen - layoutAdvance;
            pen = next;
            k = k2;
        }
    }

    /// <summary>Gets whether a face is a real italic (or oblique) style of its family, not one synthesized.</summary>
    private static bool IsRealItalic(IDWriteFontFace5* face) =>
        (face->GetSimulations() & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE) == 0
        && face->GetStyle() != DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL;

    /// <summary>Gets a face without its synthesized oblique (the same glyphs, upright), or the face if it has none.</summary>
    private IDWriteFontFace5* GetUprightFace(IDWriteFontFace5* face)
    {
        var simulations = face->GetSimulations();
        if ((simulations & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE) == 0)
            return face;
        if (this._uprightFaces.TryGetValue(face, out var known))
            return known;

        var upright = face;
        var made = default(ComPtr<IDWriteFontFace5>);
        if (LookupFont.WithSimulations(face, simulations & ~DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE, made.GetAddressOf()).SUCCEEDED)
            upright = this.Keep(made.Detach());

        this._uprightFaces.Add(face, upright);
        return upright;
    }

    /// <summary>
    /// Gets the italic face of a face's family among the system's fonts (by its family name, weight and stretch, as faces
    /// may be made from their files, with simulations or axis values), or null if the family has none of its own.
    /// </summary>
    private IDWriteFontFace5* GetItalicFace(IDWriteFontFace5* face)
    {
        if (this._italicFaces.TryGetValue(face, out var known))
            return known;

        IDWriteFontFace5* italic = null;
        string? name = null;
        using var names = default(ComPtr<IDWriteLocalizedStrings>);
        if (face->GetFamilyNames(names.GetAddressOf()).SUCCEEDED)
        {
            uint length;
            if (names.Get()->GetCount() != 0 && names.Get()->GetStringLength(0, &length).SUCCEEDED)
            {
                var buffer = stackalloc char[(int)length + 1];
                if (names.Get()->GetString(0, buffer, length + 1).SUCCEEDED)
                    name = new(buffer, 0, (int)length);
            }
        }

        var lookup = name is null
                         ? null
                         : LookupStruct.Of(name) with { Weight = (int)face->GetWeight(), Stretch = (int)face->GetStretch(), Style = 2 };
        if (lookup is not null && this._rasterizer.TryFindFont(lookup, out var match))
        {
            using (match)
            {
                var made = default(ComPtr<IDWriteFontFace5>);
                if (match.Get()->GetStyle() != DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL &&
                    (match.Get()->GetSimulations() & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE) == 0 &&
                    match.Get()->CreateFontFace(made.GetAddressOf()).SUCCEEDED)
                {
                    italic = this.Keep(made.Detach());
                }
            }
        }

        this._italicFaces.Add(face, italic);
        return italic;
    }

    /// <summary>Keeps a face made here (its reference becomes the shaper's) for as long as the shaper.</summary>
    private IDWriteFontFace5* Keep(IDWriteFontFace5* face)
    {
        if (this._keptFaces.ContainsKey(face))
        {
            face->Release();
        }
        else
        {
            var kept = default(ComPtr<IDWriteFontFace5>);
            kept.Attach(face);
            this._keptFaces.Add(face, kept);
        }

        return face;
    }

    /// <summary>
    /// Makes the cells of the clusters a layout drew, in order. The game has no left bearing: a cell's box starts at its
    /// pen, so ink left of a cluster's pen (a j's hook, a kerned pair's overhang) would be moved right. Instead the cell
    /// starts that much earlier, up to the previous cell's start, and the previous cell advances that much less; the
    /// line's width stays the same, and the ink lands where shaping put it.
    /// </summary>
    private void PlaceClusters()
    {
        var count = this._clusters.Count;
        var starts = count <= 256 ? stackalloc int[count] : new int[count];
        for (var i = 0; i < count; i++)
        {
            var c = this._clusters[i];
            starts[i] = c.X0;
            if (c.Missing)
                continue;

            // A cluster after one left to the per-character path (or a gap) can't move into it.
            var raster = this.GetRaster(c);
            if (i > 0 && !c.AfterGap && !this._clusters[i - 1].Missing && raster.Width > 0 && raster.Left < 0)
                starts[i] = Math.Max(c.X0 + raster.Left, starts[i - 1]);
        }

        for (var i = 0; i < count; i++)
        {
            var c = this._clusters[i];

            // A ligature's characters (text elements: a letter with its marks is one) share its advance evenly, so the
            // caret and selection can stop inside it; the first draws all of it, and the others draw nothing.
            var elements = 0;
            for (var t = c.TextStart; t < c.TextEnd; t += StringInfo.GetNextTextElementLength(this._text.AsSpan(t, c.TextEnd - t)))
                elements++;

            // A glyph the font lacks (.notdef) stays the per-character path's (the game's own font, at worst).
            var cell = default(Pointer<AtkFontManager.Glyph>);
            var advance = 0;
            if (!c.Missing)
            {
                var end = i + 1 < count && !this._clusters[i + 1].Missing && !this._clusters[i + 1].AfterGap ? starts[i + 1] : c.X1;
                advance = end - starts[i];
                cell = this.GetCell(c, c.X0 - starts[i], Share(advance, elements, 0));
            }

            var element = -1;
            var nextElement = c.TextStart;
            for (var t = c.TextStart; t < c.TextEnd; t++)
            {
                var startsElement = t == nextElement;
                if (startsElement)
                {
                    element++;
                    nextElement += StringInfo.GetNextTextElementLength(this._text.AsSpan(t, c.TextEnd - t));
                }

                var b = this._textToByte[t];
                if (b < 0)
                    continue;
                this._shapingGlyphs![b] = cell.IsNull ? default
                                         : t == c.TextStart ? cell
                                         : startsElement ? this.GetSpacer(Share(advance, elements, element))
                                         : this._emptyGlyph;
            }
        }

        static int Share(int advance, int parts, int part) => (advance * (part + 1) / parts) - (advance * part / parts);
    }

    /// <summary>Gets a glyph that draws nothing and advances by <paramref name="advance"/>.</summary>
    private Pointer<AtkFontManager.Glyph> GetSpacer(int advance)
    {
        var sized = this._shapingSized!;
        var key = (sized, advance);
        if (!this._spacers.TryGetValue(key, out var cell))
        {
            cell = this._replacer.PlaceCell(sized, new(advance, 0, 0, 0, 0, []), 0);
            this._spacers.Add(key, cell);
        }

        return cell;
    }

    /// <summary>Makes the system's font fallback, with private-use characters going to the game's icon font first.</summary>
    private static ComPtr<IDWriteFontFallback1> CreateSystemFallback(GlyphRasterizer rasterizer)
    {
        var factory = rasterizer.Factory;
        using var builder = default(ComPtr<IDWriteFontFallbackBuilder>);
        factory->CreateFontFallbackBuilder(builder.GetAddressOf()).ThrowOnError();
        if (rasterizer.TryFindFont(LookupStruct.Of(IconFamily), out var icons))
        {
            icons.Dispose();
            var pua = new DWRITE_UNICODE_RANGE { first = 0xE000, last = 0xF8FF };
            fixed (char* name = IconFamily)
                builder.Get()->AddMapping(&pua, 1, &name, 1, null, null, null).ThrowOnError();
        }
        else
        {
            Plugin.Log.Warning("{family} isn't installed; private-use characters are left to the game", IconFamily);
        }

        using var systemFallback = default(ComPtr<IDWriteFontFallback>);
        factory->GetSystemFontFallback(systemFallback.GetAddressOf()).ThrowOnError();
        builder.Get()->AddMappings(systemFallback.Get()).ThrowOnError();
        using var created = default(ComPtr<IDWriteFontFallback>);
        builder.Get()->CreateFontFallback(created.GetAddressOf()).ThrowOnError();
        var result = default(ComPtr<IDWriteFontFallback1>);
        created.CopyTo(result.GetAddressOf()).ThrowOnError();
        return result;
    }

    /// <summary>
    /// Shapes (or finds shaped) the run starting at <paramref name="p"/> into <paramref name="run"/>, which is reused if
    /// given; false if there is nothing to shape.
    /// </summary>
    private bool ShapeFrom(FontReplacer.SizedFont sized, byte* p, ItalicMode italic, ref ActiveRun? run)
    {
        // The run: to the end of the line, as UTF-16, each code unit's byte offset kept (-1 for a low surrogate), and each
        // character's byte whether it is in italics; a run in a node's italics ends at an italic macro instead.
        var count = 0;
        var i = 0;
        var inItalic = italic != ItalicMode.Upright;
        Span<char> units = stackalloc char[2];
        while (i < MaxRunBytes)
        {
            var b = p[i];
            if (b == 0 || b == 0x0A || b == 0x0D)
                break;

            if (b == ReadOnlySeString.Stx)
            {
                // A macro: skipped whole; a line break macro ends the run, and so does an italic one in a node's italics.
                var macro = (MacroCode)p[i + 1];
                if (macro == MacroCode.NewLine || (macro == MacroCode.Italic && italic == ItalicMode.Images))
                    break;
                var total = GameUtf8.MacroLength(p + i);
                if (total == 0 || i + total > MaxRunBytes)
                    break;

                // <italic>: its argument is a plain integer (1 on, 0 off); anything else ends the run.
                if (macro == MacroCode.Italic)
                {
                    if (!GameUtf8.TryReadInteger(p + i + 2, out var payload, out var n)
                        || !GameUtf8.TryReadInteger(p + i + 2 + n, out var on, out var onLength) || onLength != payload)
                        break;
                    inItalic = on != 0;
                }

                i += total;
                continue;
            }

            this._byteItalic[i] = inItalic;

            // Stepped as the game does; the run ends before a sequence that would run into the end of the text.
            var length = GameUtf8.CharacterLength(p + i);
            if (length == 0 || i + length > MaxRunBytes)
                break;

            var codepoint = -1;
            if (b >= 0x20 &&
                Rune.DecodeFromUtf8(new(p + i, length), out var rune, out var consumed) == System.Buffers.OperationStatus.Done &&
                consumed == length)
            {
                codepoint = rune.Value;
            }

            // Control characters and broken sequences shape as a replacement character, which keeps them per character.
            var r = codepoint < 0 ? new Rune(0xFFFD) : new Rune(codepoint);
            this.EnsureCapacity(count + 2);
            var unitCount = r.EncodeToUtf16(units);
            this._text[count] = units[0];
            this._textToByte[count] = codepoint < 0 ? -1 : i;
            count++;
            if (unitCount == 2)
            {
                this._text[count] = units[1];
                this._textToByte[count] = -1;
                count++;
            }

            i += length;
        }

        if (i == 0)
            return false;

        var hash = 0xCBF29CE484222325ul;
        for (var j = 0; j < i; j++)
            hash = (hash ^ p[j]) * 0x100000001B3ul;

        // The byte that ended the run is compared too, as what follows the last character could join its cluster.
        var ended = i < MaxRunBytes;
        var key = (hash, i, sized, italic);
        if (!this._runs.TryGetValue(key, out var shaped))
        {
            shaped = new(new Pointer<AtkFontManager.Glyph>[i],italic == ItalicMode.Images ? null : this._byteItalic.AsSpan(0, i).ToArray());
            if (count != 0)
                this.Shape(sized, p, count, shaped.Glyphs, italic);
            if (this._runs.Count >= MaxCachedRuns)
                this._runs.Clear();
            this._runs.Add(key, shaped);
        }

        run ??= new();
        run.Set(p, i, ended ? 1 : 0, sized, italic, shaped);
        return true;
    }

    private void EnsureCapacity(int n)
    {
        if (n <= this._text.Length)
            return;
        Array.Resize(ref this._text, Math.Max(n, this._text.Length * 2));
        Array.Resize(ref this._textToByte, this._text.Length);
        Array.Resize(ref this._textSource, this._text.Length);
        Array.Resize(ref this._textItalic, this._text.Length);
    }

    private void Shape(FontReplacer.SizedFont sized, byte* p, int count, Pointer<AtkFontManager.Glyph>[] glyphs, ItalicMode italic)
    {
        this._absorbed.Clear();
        count = this.ComposeHangul(count);
        this.AssignElements(sized, count);

        // Each code unit is in italics as its character's byte is (a low surrogate or control character as the one before).
        for (var t = 0; t < count; t++)
        {
            var b = this._textToByte[t];
            this._textItalic[t] = b >= 0 ? this._byteItalic[b] : t > 0 ? this._textItalic[t - 1] : italic != ItalicMode.Upright;
        }

        this._shapingBytes = p;
        this._shapingGlyphs = glyphs;
        this._shapingSized = sized;
        try
        {
            // A run in a node's italics is spaced to its upright width (or drawn per character at its upright places);
            // the others are laid out whole, in italics where their italic macros say.
            if (italic != ItalicMode.Images)
                this.Collect(sized, count, ItalicMode.Real);
            else if (!this.CollectSpreadItalics(sized, count))
                this.Collect(sized, count, ItalicMode.Images);
            this.PlaceClusters();
        }
        finally
        {
            this._shapingBytes = null;
            this._shapingGlyphs = null;
            this._shapingSized = null;
        }

        // A jamo composed into its syllable is part of the syllable's cluster.
        foreach (var (b, owner) in this._absorbed)
            glyphs[b] = glyphs[owner].IsNull ? default : this._emptyGlyph;
    }

    /// <summary>
    /// Lays the text being shaped out, and collects its clusters as <paramref name="italic"/> says (OnGlyphRun). With
    /// <see cref="ItalicMode.Real"/>, the code units in italics (<see cref="_textItalic"/>) are laid out in the faces'
    /// italics, which the system's fallback picks too.
    /// </summary>
    private void Collect(FontReplacer.SizedFont sized, int count, ItalicMode italic)
    {
        // A face measuring as GDI does (hinted, whole-pixel advances) is laid out so; its glyphs are drawn to match.
        var measureMode = sized.Face.MeasureMode;
        var format = this.GetFormat(sized.Face, sized.Px);
        using var layout = this._rasterizer.CreateLayout(this._text.AsSpan(0, count), format, measureMode);

        this.ApplyElements(layout.Get(), sized, count);
        for (var start = 0; italic == ItalicMode.Real && start < count;)
        {
            var end = start + 1;
            while (end < count && this._textItalic[end] == this._textItalic[start])
                end++;
            if (this._textItalic[start])
                layout.Get()->SetFontStyle(DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC, new() { startPosition = (uint)start, length = (uint)(end - start) })
                    .ThrowOnError();
            start = end;
        }

        this._shapingItalic = italic;
        this._penShift = 0;
        this._clusters.Clear();
        this._clusterGap = false;
        layout.Get()->CollectTo(this);
    }

    /// <summary>
    /// Collects a run in a text node's italics: shaped in italics, at the italics' own positions, but spaced out evenly to
    /// end where the run shaped upright does (the game measures it upright, as it doesn't see the node's italics then).
    /// False, with nothing collected, if the two shapings don't make the same clusters (a ligature only one has), or a
    /// cluster is left to the game: then each character is drawn in italics at its upright place instead.
    /// </summary>
    private bool CollectSpreadItalics(FontReplacer.SizedFont sized, int count)
    {
        this.Collect(sized, count, ItalicMode.Upright);
        var upright = this._clusters.ToArray();
        this._textItalic.AsSpan(0, count).Fill(true);
        this.Collect(sized, count, ItalicMode.Real);
        var italic = this._clusters.ToArray();
        this._clusters.Clear();

        var n = italic.Length;
        if (n == 0 || n != upright.Length)
            return false;
        for (var i = 0; i < n; i++)
        {
            var (u, it) = (upright[i], italic[i]);
            if (u.TextStart != it.TextStart || u.TextEnd != it.TextEnd || u.Missing || it.Missing || u.AfterGap != it.AfterGap)
                return false;
        }

        // The difference is shared out after each cluster, the last ending where the upright run ends.
        var share = (float)(upright[n - 1].X1 - italic[n - 1].X1) / n;
        for (var i = 0; i < n; i++)
            this._clusters.Add(italic[i].MovedBy(share * i, share * (i + 1)));
        return true;
    }

    /// <summary>
    /// Composes conjoining jamo (L V, L V T, and a precomposed LV syllable followed by T) into precomposed syllables, as
    /// NFC does: DirectWrite has no Hangul shaping (it applies neither composition nor the fonts' ljmo/vjmo/tjmo), so it
    /// would draw each jamo as a separate full-width letter. The absorbed jamo's bytes are recorded with their syllable's.
    /// Old Hangul sequences, which have no precomposed syllable, are left as they are.
    /// </summary>
    private int ComposeHangul(int count)
    {
        const int SyllableBase = 0xAC00, LeadBase = 0x1100, VowelBase = 0x1161, TrailBase = 0x11A7;
        const int LeadCount = 19, VowelCount = 21, TrailCount = 28, SCount = LeadCount * VowelCount * TrailCount;

        var w = 0;
        for (var i = 0; i < count;)
        {
            int c = this._text[i];
            var n = 1;
            if (c - LeadBase is >= 0 and < LeadCount && i + 1 < count && this._text[i + 1] - VowelBase is >= 0 and < VowelCount)
            {
                c = SyllableBase + ((((c - LeadBase) * VowelCount) + (this._text[i + 1] - VowelBase)) * TrailCount);
                n = 2;
            }

            if ((n == 2 || (c - SyllableBase is >= 0 and < SCount && (c - SyllableBase) % TrailCount == 0)) &&
                i + n < count && this._text[i + n] - TrailBase is > 0 and < TrailCount)
            {
                c += this._text[i + n] - TrailBase;
                n++;
            }

            for (var j = 1; j < n; j++)
                this._absorbed.Add((this._textToByte[i + j], this._textToByte[i]));
            this._text[w] = (char)c;
            this._textToByte[w] = this._textToByte[i];
            w++;
            i += n;
        }

        return w;
    }

    /// <summary>
    /// Gets where a text may be split: <paramref name="clusterEnd"/>[i] is set when a cluster ends after the first i
    /// code units, and <paramref name="wrapAfter"/>[i] when a line may also break there (UAX #14, dictionary breaks for
    /// Thai and the like). Both spans are one longer than <paramref name="text"/>.
    /// </summary>
    public void GetBreaks(ReadOnlySpan<char> text, Span<bool> clusterEnd, Span<bool> wrapAfter)
    {
        clusterEnd.Clear();
        wrapAfter.Clear();
        if (text.IsEmpty)
            return;

        // Clusters and break opportunities don't depend on the size (fallback fonts are picked the same way at any).
        var format = this.GetFormat(this._replacer.BuiltInFace, 16);
        using var layout = this._rasterizer.CreateLayout(text, format);
        uint count;
        layout.Get()->GetClusterMetrics(null, 0, &count);
        var metrics = count <= 256 ? stackalloc DWRITE_CLUSTER_METRICS[(int)count] : new DWRITE_CLUSTER_METRICS[count];
        fixed (DWRITE_CLUSTER_METRICS* m = metrics)
            layout.Get()->GetClusterMetrics(m, count, &count).ThrowOnError();

        var position = 0;
        foreach (ref readonly var cluster in metrics)
        {
            position += cluster.length;
            clusterEnd[position] = true;
            wrapAfter[position] = cluster.canWrapLineAfter != 0;
        }
    }

    /// <summary>
    /// Finds the glyph source of each code unit of the text (a low surrogate gets its high one's), and puts in the
    /// characters an element draws with another's glyph (its codepoint replacements), where the UTF-16 length allows.
    /// </summary>
    private void AssignElements(FontReplacer.SizedFont sized, int count)
    {
        for (var i = 0; i < count;)
        {
            var n = char.IsHighSurrogate(this._text[i]) && i + 1 < count && char.IsLowSurrogate(this._text[i + 1]) ? 2 : 1;
            var rune = n == 2 ? new(this._text[i], this._text[i + 1]) : Rune.TryCreate(this._text[i], out var single) ? single : Rune.ReplacementChar;
            var source = sized.Pick(rune);
            var element = source.Element;
            if (element is { DrawsGame: false })
            {
                var drawn = ReplacementFace.GetDrawnCodepoint(element, rune);
                if (drawn != rune && drawn.Utf16SequenceLength == n)
                    drawn.EncodeToUtf16(this._text.AsSpan(i, n));
            }

            for (var j = 0; j < n; j++)
                this._textSource[i + j] = source;
            i += n;
        }
    }

    /// <summary>
    /// Sets each element's font, size and features on the text it draws. Text no element draws keeps the format's: the
    /// face's first font, with the system's fallback if the face has it (and dropped otherwise).
    /// </summary>
    private void ApplyElements(IDWriteTextLayout4* layout, FontReplacer.SizedFont sized, int count)
    {
        for (var start = 0; start < count;)
        {
            var source = this._textSource[start];
            var element = source.Element;
            var end = start + 1;
            while (end < count && this._textSource[end].Element == element)
                end++;

            // Elements not drawn from their fonts are left per character anyway.
            if (element?.Shaped is { } font)
            {
                var range = new DWRITE_TEXT_RANGE { startPosition = (uint)start, length = (uint)(end - start) };
                font.ApplyTo(layout, range, source.Face.GetElementPx(element, sized.Px));
            }

            start = end;
        }
    }

    /// <summary>
    /// Gets the text format of a face at a size: its first font (Segoe UI if none) at that font's size, with the
    /// system's fallback or none as the face says.
    /// </summary>
    private IDWriteTextFormat3* GetFormat(ReplacementFace face, float px)
    {
        var key = (face, (int)Rounding.Round(px * 2));
        if (this._formats.TryGetValue(key, out var existing))
            return existing.Get();

        var primary = face.Primary;
        var lookup = primary?.Def.Lookup ?? LookupStruct.Of("Segoe UI");
        var format = this._rasterizer.CreateFormat(
            lookup.Name,
            lookup.Weight,
            lookup.Style,
            lookup.Stretch,
            primary is not null ? face.GetElementPx(primary, px) : px,
            face.SystemFallback ? this._fallback.Get() : this._rasterizer.NoFallback);
        this._formats.Add(key, format);
        return format.Get();
    }

    /// <summary>
    /// Gets a cluster's coverage, relative to its pen, adjusted as its element says (ReplacementFace.Wrap) and finished
    /// (FinishGlyph). Its advance is set when its cell is made.
    /// </summary>
    private RasterGlyph GetRaster(Cluster c)
    {
        var key = c.RasterKey ??= new(this._shapingSized!, c);
        if (this._rasters.TryGetValue(key, out var raster))
            return raster;

        var sized = this._shapingSized!;
        raster = c.Cell ?? this.RasterizeCluster(c, 0, 1);
        if (c.Cell is null && c.Element is { } element)
            raster = c.Source!.Face.Wrap(element, raster, sized.Px, c.Source.Game, s => this.RasterizeCluster(c, 0, s));

        // An edge margin makes ink left of the pen: the cell then starts earlier (PlaceClusters), keeping the spacing.
        raster = this._replacer.FinishGlyph(sized, raster);
        this._rasters.Add(key, raster);
        return raster;
    }

    /// <summary>Rasterizes a cluster as its element draws it, squeezed horizontally by <paramref name="squeezeX"/>.</summary>
    private RasterGlyph RasterizeCluster(Cluster c, int advance, float squeezeX)
    {
        fixed (ushort* glyphs = c.Glyphs)
        fixed (float* advances = c.Advances)
        fixed (DWRITE_GLYPH_OFFSET* offsets = c.Offsets)
        {
            return this._shapingSized!.Face.RasterizeRun(
                c.Element,
                c.Face,
                c.EmSize,
                glyphs,
                advances,
                offsets,
                (uint)c.Glyphs.Length,
                c.Origin,
                advance,
                squeezeX);
        }
    }

    /// <summary>
    /// Gets the cell of a cluster whose box starts <paramref name="pad"/> pixels left of its pen and advances by
    /// <paramref name="advance"/>.
    /// </summary>
    private Pointer<AtkFontManager.Glyph> GetCell(Cluster c, int pad, int advance)
    {
        var sized = this._shapingSized!;
        var raster = this.GetRaster(c);
        var key = (c.RasterKey!, sized, pad, advance);
        if (this._cells.TryGetValue(key, out var cell))
            return cell;

        raster = raster with { Advance = advance, Left = raster.Left + pad };

        // A cell that couldn't move left (PlaceClusters) is drawn right of where shaping put its margin: what follows moves along, keeping its distance to the ink.
        if (raster.Width != 0 && raster.Left < 0)
            raster = raster with { Advance = raster.Advance + Math.Min(-raster.Left, this._replacer.GetEdgeMargin(sized)) };
        var b = this._textToByte[c.TextStart];
        var utf8 = b < 0 ? 0u : GameUtf8.Pack(this._shapingBytes + b);
        cell = this._replacer.PlaceCell(sized, raster, utf8);
        if (c.RealItalic)
            this._replacer.MarkRealItalic(cell);
        this._cells.Add(key, cell);
        return cell;
    }

    /// <summary>How a run of text the game draws in italics is shaped.</summary>
    internal enum ItalicMode
    {
        Upright,

        /// <summary>
        /// In italics by an italic macro, which the game also sees when it measures: shaped in the faces' italics, which
        /// are drawn as they are; the game shears the glyphs of faces without (synthesized obliques are drawn upright for
        /// it).
        /// </summary>
        Real,

        /// <summary>
        /// In italics by the text node, which the game only sees when it draws, measuring the text upright: shaped in
        /// italics as <see cref="Real"/>, spaced out evenly to the upright run's width. Where the two shapings differ,
        /// shaped upright instead, each single-character cluster drawn with the italic of its font, where there is one.
        /// </summary>
        Images,
    }

    /// <summary>A cluster of a layout: its glyphs and where they are, and the text it covers.</summary>
    private sealed record Cluster
    {
        /// <summary>The face its glyphs are drawn from, kept by the shaper.</summary>
        public Pointer<IDWriteFontFace5> Face { get; init; }

        public float EmSize { get; init; }

        public ushort[] Glyphs { get; init; } = [];

        public float[] Advances { get; init; } = [];

        public DWRITE_GLYPH_OFFSET[]? Offsets { get; init; }

        /// <summary>The key of the cluster's coverage, once made (GetRaster).</summary>
        public RasterKey? RasterKey { get; set; }

        /// <summary>The fraction of a pixel the pen is at, in steps of a quarter.</summary>
        public float Origin { get; init; }

        /// <summary>The whole-pixel pen at the cluster's start and end.</summary>
        public int X0 { get; init; }

        public int X1 { get; set; }

        /// <summary>The cluster's coverage in its cell (ReplacementFace.Wrap), made as it is laid out if its element is monospaced.</summary>
        public RasterGlyph? Cell { get; set; }

        public int TextStart { get; init; }

        public int TextEnd { get; init; }

        /// <summary>Where the cluster's first character's glyph comes from.</summary>
        public FontReplacer.GlyphSource? Source { get; init; }

        /// <summary>The face element of the cluster's first character, or null for the system's fonts.</summary>
        public ReplacementElement? Element { get; init; }

        /// <summary>Whether a glyph is missing from the font, or the game draws it (the cluster is left per character).</summary>
        public bool Missing { get; init; }

        /// <summary>Whether something not shaped here (a right-to-left run) comes right before it.</summary>
        public bool AfterGap { get; init; }

        /// <summary>Whether its glyphs are a face's real italics, which the game must not shear.</summary>
        public bool RealItalic { get; init; }

        /// <summary>Gets the cluster moved right: its start by <paramref name="start"/> pixels, its end by <paramref name="end"/>.</summary>
        public Cluster MovedBy(float start, float end)
        {
            var pen = this.X0 + this.Origin + start;
            var x0 = Rounding.Round(pen);
            return this with
            {
                RasterKey = null,
                Origin = Rounding.Round((pen - x0) * SubpixelSteps) / SubpixelSteps,
                X0 = (int)x0,
                X1 = (int)Rounding.Round(this.X1 + end),
            };
        }
    }

    /// <summary>
    /// The key of a cluster's coverage: its face, size and element, and its glyphs at its position. Glyphs are compared
    /// by value.
    /// </summary>
    private sealed class RasterKey(FontReplacer.SizedFont sized, Cluster c) : IEquatable<RasterKey>
    {
        private readonly ReplacementFace _face = sized.Face;
        private readonly float _px = sized.Px;
        private readonly ReplacementElement? _element = c.Element;
        private readonly Pointer<IDWriteFontFace5> _fontFace = c.Face;
        private readonly float _emSize = c.EmSize;
        private readonly float _origin = c.Origin;
        private readonly ushort[] _glyphs = c.Glyphs;
        private readonly float[] _advances = c.Advances;
        private readonly DWRITE_GLYPH_OFFSET[]? _offsets = c.Offsets;

        public bool Equals(RasterKey? other) =>
            ReferenceEquals(this, other) ||
            (other is not null &&
             this._face == other._face &&
             this._element == other._element &&
             this._fontFace == other._fontFace &&
             this._px.Equals(other._px) &&
             this._emSize.Equals(other._emSize) &&
             this._origin.Equals(other._origin) &&
             this._glyphs.AsSpan().SequenceEqual(other._glyphs) &&
             this._advances.AsSpan().SequenceEqual(other._advances) &&
             MemoryMarshal.AsBytes(this._offsets.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(other._offsets.AsSpan())));

        public override bool Equals(object? obj) => this.Equals(obj as RasterKey);

        public override int GetHashCode()
        {
            var hash = default(HashCode);
            hash.Add(this._face);
            hash.Add(this._px);
            hash.Add(this._element);
            hash.Add(this._fontFace);
            hash.Add(this._emSize);
            hash.Add(this._origin);
            hash.AddBytes(MemoryMarshal.AsBytes(this._glyphs.AsSpan()));
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// A shaped run: the glyph for each byte, and whether each character's byte is in italics (null for a run in a node's
    /// italics, all of it in them).
    /// </summary>
    private sealed record ShapedRun(Pointer<AtkFontManager.Glyph>[] Glyphs, bool[]? ItalicBytes);

    /// <summary>
    /// A run shaped at an address. The game reuses its buffers (each line drawn from one copy buffer, each word measured
    /// in another), so an address says nothing about the text there now: the run keeps a copy of its bytes, and a
    /// lookup in it only uses it while the text from the looked-up character to the run's end is still the same.
    /// </summary>
    private sealed class ActiveRun
    {
        private byte[] _bytes = new byte[64];
        private int _compareLength;

        public byte* Start { get; private set; }

        public byte* End { get; private set; }

        public FontReplacer.SizedFont? Sized { get; private set; }

        public ItalicMode Italic { get; private set; }

        public Pointer<AtkFontManager.Glyph>[] Glyphs { get; private set; } = [];

        private bool[]? _italicBytes;

        public void Set(byte* start, int length, int endBytes, FontReplacer.SizedFont sized, ItalicMode italic, ShapedRun shaped)
        {
            this.Start = start;
            this.End = start + length;
            this.Sized = sized;
            this.Italic = italic;
            this.Glyphs = shaped.Glyphs;
            this._italicBytes = shaped.ItalicBytes;
            this._compareLength = length + endBytes;
            if (this._bytes.Length < this._compareLength)
                this._bytes = new byte[Math.Max(this._compareLength, this._bytes.Length * 2)];
            new ReadOnlySpan<byte>(start, this._compareLength).CopyTo(this._bytes);
        }

        /// <summary>
        /// Gets the italics the character at <paramref name="p"/> (inside the run) was shaped in: as the run's italic macros
        /// left them, or the node's italics of a run spaced to its upright width.
        /// </summary>
        public ItalicMode ItalicAt(byte* p) =>
            this._italicBytes is null ? this.Italic : this._italicBytes[p - this.Start] ? ItalicMode.Real : ItalicMode.Upright;

        /// <summary>
        /// Gets whether the text from <paramref name="p"/> (inside the run) to its end is what was shaped. Compared byte
        /// by byte, stopping at the first difference: the copy has no terminator before its end, so a shorter text stops
        /// it before reading past its own terminator.
        /// </summary>
        public bool Matches(byte* p)
        {
            for (var i = (int)(p - this.Start); i < this._compareLength; i++)
            {
                if (this.Start[i] != this._bytes[i])
                    return false;
            }

            return true;
        }
    }
}
