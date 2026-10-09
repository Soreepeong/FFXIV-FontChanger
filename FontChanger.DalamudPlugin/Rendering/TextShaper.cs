using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace CustomFonts;

/// <summary>How a run of text the game draws in italics is shaped.</summary>
internal enum ItalicMode
{
    /// <summary>Not in italics.</summary>
    Upright,

    /// <summary>
    /// In italics by an italic macro, which the game also sees when it measures: shaped in the faces' italics, which are
    /// drawn as they are; the game shears the glyphs of faces without (synthesized obliques are drawn upright for it).
    /// </summary>
    Real,

    /// <summary>
    /// In italics by the text node, which the game only sees when it draws, measuring the text upright: shaped in italics
    /// as <see cref="Real"/>, spaced out evenly to the upright run's width. Where the two shapings differ, shaped upright
    /// instead, each single-character cluster drawn with the italic of its font, where there is one.
    /// </summary>
    Images,
}

/// <summary>
/// Shapes the game's text with DirectWrite (ligatures, contextual alternates, kerning, marks, fallback per script), and
/// answers the game's per-character glyph lookups from the result.
/// </summary>
/// <remarks>
/// <para>The game lays text out one character at a time: its text walkers (FUN_140652BE0, FUN_140653030, and
/// AtkFontAnalyzerBase vf4 for text inserted by macros) call FUN_1406EEC70 for each character, which looks the glyph up
/// and advances the pen by its width. With the character's address known, the run it starts or continues is shaped
/// once: from there to the end of the line, macro payloads skipped (a colour change doesn't break a ligature), as
/// UTF-16 for DirectWrite.</para>
/// <para>Each shaped cluster (a ligature, a base and its marks, a conjunct) becomes one cell: its glyphs rasterized
/// together at the fraction of a pixel shaping put it at, advancing by the whole-pixel difference of its rounded start
/// and end, so a line of clusters ends where shaping ends it. The first character of a cluster gets the cell, the others a
/// glyph of no width. Right-to-left runs and glyphs a font lacks are left to the per-character path.</para>
/// <para>Text is laid out every frame (several times: measuring, counting, drawing), so shaped runs are kept by content
/// and size, and the last few runs by address, used while the text at the address is unchanged.</para>
/// <para>A run ends at an italic macro, and is shaped as the italics it is in says (<see cref="ItalicMode"/>). The cells
/// of real italic glyphs are told to the replacer, which keeps the game from shearing them.</para>
/// </remarks>
internal sealed unsafe class TextShaper : IDisposable, IGlyphRunSink
{
    // The game's icon font: private-use characters go to it before the system fallback, which would pick an icon font
    // of Windows' own (Segoe Fluent Icons) for them.
    internal const string IconFamily = "XIV AXIS Std ATK";

    // The macro that sets italics (<italic>): runs end before it.
    private const byte ItalicMacro = 0x1A;

    private const int MaxRunBytes = 4096;
    private const int MaxActiveRuns = 8;
    private const int MaxCachedRuns = 8192;

    // Cells are made at a quarter-pixel resolution of the cluster's position.
    private const float SubpixelSteps = 4;

    private readonly GlyphRasterizer rasterizer;
    private readonly FontReplacer replacer;
    private readonly GlyphRunCollector collector = new();
    private readonly Dictionary<(ReplacementFace? Face, int HalfPx), nint> formats = [];
    private readonly Dictionary<(ulong Hash, int Length, FontReplacer.SizedFont Sized, ItalicMode Italic), ShapedRun> runs = [];

    // Faces without their synthesized oblique, by face; and the italic face of a face's family (0 for none), by face.
    private readonly Dictionary<nint, nint> uprightFaces = [];
    private readonly Dictionary<nint, nint> italicFaces = [];
    private readonly Dictionary<string, nint> cells = [];
    private readonly HashSet<nint> keptFaces = [];
    private readonly List<ActiveRun> active = [];
    private readonly StringBuilder cellKey = new();
    private readonly List<(int Byte, int Owner)> absorbed = [];

    // The clusters of the layout being shaped, and coverage of clusters by glyphs and position.
    private readonly List<Cluster> clusters = [];
    private readonly Dictionary<string, RasterGlyph> rasters = [];
    private bool clusterGap;

    // The system's fallback, private-use characters to the game's icon font first. A face without system fallback lays
    // out each character in the font of its element only (GlyphRasterizer.NoFallback).
    private IDWriteFontFallback* fallback;
    private nint emptyGlyph;

    // While shaping a run: its text, and each code unit's byte offset, element and whether it is in italics.
    private char[] text = new char[256];
    private int[] textToByte = new int[256];
    private int[] textElement = new int[256];
    private bool[] textItalic = new bool[256];
    private readonly bool[] byteItalic = new bool[MaxRunBytes];    private byte* shapingBytes;
    private nint[]? shapingGlyphs;
    private FontReplacer.SizedFont? shapingSized;
    private ItalicMode shapingItalic;

    // How far the pen has moved from where the layout put it: by elements' letter spacing, transformations and
    // monospacing, which DirectWrite doesn't know of.
    private float penShift;

    public TextShaper(GlyphRasterizer rasterizer, FontReplacer replacer)
    {
        this.rasterizer = rasterizer;
        this.replacer = replacer;
        this.fallback = CreateSystemFallback(rasterizer);
    }

    /// <summary>Releases the text formats, which depend on the faces (before the faces change).</summary>
    public void ClearFormats()
    {
        foreach (var format in this.formats.Values)
            ((IUnknown*)format)->Release();
        this.formats.Clear();
    }

    /// <summary>
    /// Gets the glyph for the character at <paramref name="p"/> as shaped in its run, in the italics it is in, or 0 to
    /// look it up per character. <paramref name="emptyGlyph"/> is the glyph of no width given to a cluster's characters
    /// after the first.
    /// </summary>
    public nint TryGetGlyph(FontReplacer.SizedFont sized, byte* p, nint emptyGlyph, ItalicMode italic)
    {
        this.emptyGlyph = emptyGlyph;
        for (var i = this.active.Count - 1; i >= 0; i--)
        {
            var run = this.active[i];
            if (run.Sized == sized && p >= run.Start && p < run.End && run.ItalicAt(p) == italic && run.Matches(p))
                return run.Glyphs[p - run.Start];
        }

        var reused = this.active.Count == MaxActiveRuns ? this.active[0] : null;
        if (!this.ShapeFrom(sized, p, italic, ref reused))
            return 0;
        if (this.active.Count == MaxActiveRuns)
            this.active.RemoveAt(0);
        this.active.Add(reused!);
        return reused!.Glyphs[0];
    }

    /// <summary>Forgets every shaped run and cell (their glyphs are about to be freed).</summary>
    public void Clear()
    {
        this.active.Clear();
        this.runs.Clear();
        this.cells.Clear();
        this.rasters.Clear();
    }

    public void Dispose()
    {
        this.Clear();
        this.ClearFormats();
        this.uprightFaces.Clear();
        this.italicFaces.Clear();
        foreach (var face in this.keptFaces)
            ((IUnknown*)face)->Release();
        this.keptFaces.Clear();
        if (this.fallback is not null)
        {
            this.fallback->Release();
            this.fallback = null;
        }

        this.collector.Dispose();
    }

    void IGlyphRunSink.OnGlyphRun(float baselineX, DWRITE_GLYPH_RUN* run, DWRITE_GLYPH_RUN_DESCRIPTION* description)
    {
        // The game draws in logical order: right-to-left runs stay per character, and break the line's clusters.
        if (this.shapingGlyphs is null || this.shapingSized is null)
            return;
        if ((run->bidiLevel & 1) != 0)
        {
            this.clusterGap = true;
            return;
        }

        var clusterMap = description->clusterMap;
        var length = (int)description->stringLength;
        var start = (int)description->textPosition;
        var pen = baselineX + this.penShift;
        for (var k = 0; k < length;)
        {
            var glyphStart = clusterMap[k];
            var k2 = k + 1;
            while (k2 < length && clusterMap[k2] == glyphStart)
                k2++;
            var glyphEnd = k2 < length ? clusterMap[k2] : (ushort)run->glyphCount;
            var count = glyphEnd - glyphStart;

            // Characters left to the game (by the face, or as none of its elements has them), and those of elements not
            // drawn from their fonts (merged glyphs, glyph images), stay per character.
            var sized = this.shapingSized;
            var element = this.textElement[start + k];
            var missing = element == ReplacementFace.GameElement || !sized.Face.IsShaped(element);

            // An element's glyphs are drawn from its own face (simulations, axis values), and FreeType's emboldening
            // advances them further.
            var face = sized.Face.GetRunFace(element, run->fontFace, run->fontEmSize);
            if (this.keptFaces.Add((nint)face))
                face->AddRef();
            var extra = sized.Face.GetExtraAdvance(element, run->fontEmSize);
            var advances = new float[count];
            var advance = 0f;
            for (var g = glyphStart; g < glyphEnd; g++)
            {
                advances[g - glyphStart] = run->glyphAdvances[g] + extra;
                advance += advances[g - glyphStart];
                missing |= run->glyphIndices[g] == 0;
            }

            // Italics. Shaped in italic: a face's real italic stays, a synthesized oblique is drawn upright for the game to
            // shear. Shaped upright for a node's italics: a single character is drawn with its font's italic, if it has
            // one, at the upright advance; ligatures and the rest stay upright, sheared.
            var glyphs = new ReadOnlySpan<ushort>(run->glyphIndices + glyphStart, count).ToArray();
            var offsets = run->glyphOffsets is null ? null : new ReadOnlySpan<DWRITE_GLYPH_OFFSET>(run->glyphOffsets + glyphStart, count).ToArray();
            var realItalic = false;
            if (!missing && this.shapingItalic == ItalicMode.Real && this.textItalic[start + k])
            {
                realItalic = IsRealItalic(face);
                if (!realItalic)
                    face = this.GetUprightFace(face);
            }
            else if (!missing && this.shapingItalic == ItalicMode.Images && count == 1 &&
                     Rune.DecodeFromUtf16(this.text.AsSpan(start + k, k2 - k), out var rune, out var used) == System.Buffers.OperationStatus.Done &&
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

            // An element's transformation scales its advances, its monospacing puts them in cells, and its letter spacing
            // widens them; everything after moves along.
            var x0 = MathF.Round(pen);
            var width = advance * sized.Face.GetAdvanceScale(element);
            var spacing = element >= 0 ? sized.Face.ScalePixels(sized.Face.GetDef(element).LetterSpacing, sized.Px) : 0;
            var cluster = new Cluster
            {
                Face = (nint)face,
                EmSize = run->fontEmSize,
                Glyphs = glyphs,
                Advances = advances,
                Offsets = offsets,
                RealItalic = realItalic,
                Origin = MathF.Round((pen - x0) * SubpixelSteps) / SubpixelSteps,
                X0 = (int)x0,
                TextStart = start + k,
                TextEnd = start + k2,
                Element = element,
                Missing = missing,
                AfterGap = this.clusterGap,
            };

            var end = pen + width;

            // DirectWrite moves the pen by its advances; the difference moves everything after along.
            this.penShift += extra * count;
            if (!missing && element >= 0 && sized.Face.GetDef(element).Monospacing is not null)
            {
                cluster.Monospaced = sized.Face.Monospace(
                    element, sized.Px, this.RasterizeCluster(cluster, (int)MathF.Round(width), 1), s => this.RasterizeCluster(cluster, (int)MathF.Round(width), s));
                end = x0 + cluster.Monospaced.Value.Advance;
            }

            cluster.X1 = (int)MathF.Round(end) + spacing;
            this.clusters.Add(cluster);
            this.clusterGap = false;
            this.penShift += end - pen - advance + spacing;

            pen = end + spacing;
            k = k2;
        }
    }

    /// <summary>Gets whether a face is a real italic (or oblique) style of its family, not one synthesized.</summary>
    private static bool IsRealItalic(IDWriteFontFace* face)
    {
        if ((face->GetSimulations() & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE) != 0)
            return false;
        IDWriteFontFace3* face3;
        var iid = IID.IID_IDWriteFontFace3;
        if (((IUnknown*)face)->QueryInterface(&iid, (void**)&face3).FAILED)
            return false;
        var style = face3->GetStyle();
        face3->Release();
        return style != DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL;
    }

    /// <summary>Gets a face without its synthesized oblique (the same glyphs, upright), or the face if it has none.</summary>
    private IDWriteFontFace* GetUprightFace(IDWriteFontFace* face)
    {
        var simulations = face->GetSimulations();
        if ((simulations & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE) == 0)
            return face;
        if (this.uprightFaces.TryGetValue((nint)face, out var known))
            return (IDWriteFontFace*)known;

        var upright = face;
        IDWriteFontFace3* face3;
        var iid = IID.IID_IDWriteFontFace3;
        if (((IUnknown*)face)->QueryInterface(&iid, (void**)&face3).SUCCEEDED)
        {
            IDWriteFontFaceReference* reference;
            if (face3->GetFontFaceReference(&reference).SUCCEEDED)
            {
                IDWriteFontFace3* made;
                if (reference->CreateFontFaceWithSimulations(simulations & ~DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE, &made).SUCCEEDED)
                    upright = this.Keep((IDWriteFontFace*)made);
                reference->Release();
            }

            face3->Release();
        }

        this.uprightFaces.Add((nint)face, (nint)upright);
        return upright;
    }

    /// <summary>
    /// Gets the italic face of a face's family among the system's fonts (by its family name, weight and stretch, as faces
    /// may be made from their files, with simulations or axis values), or null if the family has none of its own.
    /// </summary>
    private IDWriteFontFace* GetItalicFace(IDWriteFontFace* face)
    {
        if (this.italicFaces.TryGetValue((nint)face, out var known))
            return (IDWriteFontFace*)known;

        IDWriteFontFace* italic = null;
        IDWriteFontFace3* face3;
        var iid = IID.IID_IDWriteFontFace3;
        if (((IUnknown*)face)->QueryInterface(&iid, (void**)&face3).SUCCEEDED)
        {
            IDWriteFontFamily* family = null;
            IDWriteLocalizedStrings* names;
            if (face3->GetFamilyNames(&names).SUCCEEDED)
            {
                uint length;
                if (names->GetCount() != 0 && names->GetStringLength(0, &length).SUCCEEDED)
                {
                    var name = stackalloc char[(int)length + 1];
                    uint index;
                    BOOL exists;
                    if (names->GetString(0, name, length + 1).SUCCEEDED &&
                        this.rasterizer.SystemFonts->FindFamilyName(name, &index, &exists).SUCCEEDED && exists)
                    {
                        this.rasterizer.SystemFonts->GetFontFamily(index, &family);
                    }
                }

                names->Release();
            }

            IDWriteFont* match;
            if (family is not null &&
                family->GetFirstMatchingFont(face3->GetWeight(), face3->GetStretch(), DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC, &match).SUCCEEDED)
            {
                IDWriteFontFace* made;
                if (match->GetStyle() != DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL &&
                    (match->GetSimulations() & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE) == 0 &&
                    match->CreateFontFace(&made).SUCCEEDED)
                {
                    italic = this.Keep(made);
                }

                match->Release();
            }

            if (family is not null)
                family->Release();
            face3->Release();
        }

        this.italicFaces.Add((nint)face, (nint)italic);
        return italic;
    }

    /// <summary>Keeps a face made here (its reference becomes the shaper's) for as long as the shaper.</summary>
    private IDWriteFontFace* Keep(IDWriteFontFace* face)
    {
        if (!this.keptFaces.Add((nint)face))
            face->Release();
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
        var count = this.clusters.Count;
        var starts = count <= 256 ? stackalloc int[count] : new int[count];
        for (var i = 0; i < count; i++)
        {
            var c = this.clusters[i];
            starts[i] = c.X0;
            if (c.Missing)
                continue;

            // A cluster after one left to the per-character path (or a gap) can't move into it.
            var raster = this.GetRaster(c);
            if (i > 0 && !c.AfterGap && !this.clusters[i - 1].Missing && raster.Width > 0 && raster.Left < 0)
                starts[i] = Math.Max(c.X0 + raster.Left, starts[i - 1]);
        }

        for (var i = 0; i < count; i++)
        {
            var c = this.clusters[i];

            // A ligature's characters (text elements: a letter with its marks is one) share its advance evenly, so the
            // caret and selection can stop inside it; the first draws all of it, and the others draw nothing.
            var elements = 0;
            for (var t = c.TextStart; t < c.TextEnd; t += StringInfo.GetNextTextElementLength(this.text.AsSpan(t, c.TextEnd - t)))
                elements++;

            // A glyph the font lacks (.notdef) stays the per-character path's (the game's own font, at worst).
            nint cell = 0;
            var advance = 0;
            if (!c.Missing)
            {
                var end = i + 1 < count && !this.clusters[i + 1].Missing && !this.clusters[i + 1].AfterGap ? starts[i + 1] : c.X1;
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
                    nextElement += StringInfo.GetNextTextElementLength(this.text.AsSpan(t, c.TextEnd - t));
                }

                var b = this.textToByte[t];
                if (b < 0)
                    continue;
                this.shapingGlyphs![b] = cell == 0 ? 0
                    : t == c.TextStart ? cell
                    : startsElement ? this.GetSpacer(Share(advance, elements, element))
                    : this.emptyGlyph;
            }
        }

        static int Share(int advance, int parts, int part) => (advance * (part + 1) / parts) - (advance * part / parts);
    }

    /// <summary>Gets a glyph that draws nothing and advances by <paramref name="advance"/>.</summary>
    private nint GetSpacer(int advance)
    {
        var sized = this.shapingSized!;
        var key = $"spacer|{sized.Face.Id}|{sized.Px}|{advance}";
        if (!this.cells.TryGetValue(key, out var cell))
        {
            cell = (nint)this.replacer.PlaceCell(sized, new RasterGlyph(advance, 0, 0, 0, 0, []), 0);
            this.cells.Add(key, cell);
        }

        return cell;
    }

    /// <summary>Makes the system's font fallback, with private-use characters going to the game's icon font first.</summary>
    private static IDWriteFontFallback* CreateSystemFallback(GlyphRasterizer rasterizer)
    {
        var factory = rasterizer.Factory;
        IDWriteFontFallbackBuilder* builder;
        factory->CreateFontFallbackBuilder(&builder).ThrowOnError();
        try
        {
            var icons = rasterizer.FindFont(LookupDef.Of(IconFamily));
            if (icons is not null)
            {
                icons->Release();
                var pua = new DWRITE_UNICODE_RANGE { first = 0xE000, last = 0xF8FF };
                fixed (char* name = IconFamily)
                {
                    var names = name;
                    builder->AddMapping(&pua, 1, &names, 1, null, null, null, 1).ThrowOnError();
                }
            }
            else
            {
                Plugin.Log.Warning("{family} isn't installed; private-use characters are left to the game", IconFamily);
            }

            IDWriteFontFallback* systemFallback;
            factory->GetSystemFontFallback(&systemFallback).ThrowOnError();
            builder->AddMappings(systemFallback).ThrowOnError();
            systemFallback->Release();
            IDWriteFontFallback* result;
            builder->CreateFontFallback(&result).ThrowOnError();
            return result;
        }
        finally
        {
            builder->Release();
        }
    }

    /// <summary>Decodes a SeString integer (a macro's payload length); returns the bytes it takes, 0 if malformed.</summary>
    internal static int ReadInteger(byte* p, out int value)
    {
        var marker = p[0];
        if (marker == 0)
        {
            value = 0;
            return 0;
        }

        if (marker < 0xF0)
        {
            value = marker - 1;
            return 1;
        }

        if (marker > 0xFE)
        {
            value = 0;
            return 0;
        }

        var flags = marker + 1;
        var n = 1;
        value = 0;
        for (var bit = 3; bit >= 0; bit--)
        {
            if ((flags & (1 << bit)) == 0)
                continue;
            value |= p[n++] << (8 * bit);
        }

        return n;
    }

    private static int SequenceLength(byte b) => GameUtf8.SequenceLength(b);

    /// <summary>
    /// Shapes (or finds shaped) the run starting at <paramref name="p"/> into <paramref name="run"/>, which is reused if
    /// given; false if there is nothing to shape.
    /// </summary>
    private bool ShapeFrom(FontReplacer.SizedFont sized, byte* p, ItalicMode italic, ref ActiveRun? run)
    {
        // The run: to the end of the line, as UTF-16, each code unit's byte offset kept (-1 for a low surrogate), and each
        // character's byte whether it is in italics. Italic macros change that within the run, as they do the game's state,
        // so that a line is laid out whole across them (an italic word's overhangs and spacing next to upright text); a run
        // in a node's italics, spaced to its upright width, ends at one instead.
        var count = 0;
        var i = 0;
        var hash = 0xCBF29CE484222325ul;
        var inItalic = italic != ItalicMode.Upright;
        Span<char> units = stackalloc char[2];
        while (i < MaxRunBytes)
        {
            var b = p[i];
            if (b == 0 || b == 0x0A || b == 0x0D)
                break;

            if (b == 0x02)
            {
                // A macro: skipped whole; a line break macro ends the run.
                if (p[i + 1] == 0x10 || p[i + 1] == 0 || (p[i + 1] == ItalicMacro && italic == ItalicMode.Images))
                    break;
                var n = ReadInteger(p + i + 2, out var payload);
                var total = 2 + n + payload + 1;
                if (n == 0 || payload < 0 || i + total > MaxRunBytes || p[i + total - 1] != 0x03)
                    break;

                // <italic>: its argument is a plain integer (1 on, 0 off); anything else ends the run.
                if (p[i + 1] == ItalicMacro)
                {
                    if (payload == 0 || ReadInteger(p + i + 2 + n, out var on) != payload)
                        break;
                    inItalic = on != 0;
                }

                for (var j = 0; j < total; j++)
                    hash = (hash ^ p[i + j]) * 0x100000001B3ul;
                i += total;
                continue;
            }

            this.byteItalic[i] = inItalic;

            // Stepped by the first byte alone, as the game does, valid or not; the run ends before a sequence that would
            // run into the end of the text.
            var length = SequenceLength(b);
            var truncated = false;
            for (var j = 1; j < length; j++)
                truncated |= p[i + j] == 0;
            if (truncated || i + length > MaxRunBytes)
                break;

            var codepoint = -1;
            if (b >= 0x20 &&
                Rune.DecodeFromUtf8(new ReadOnlySpan<byte>(p + i, length), out var rune, out var consumed) == System.Buffers.OperationStatus.Done &&
                consumed == length)
            {
                codepoint = rune.Value;
            }

            for (var j = 0; j < length; j++)
                hash = (hash ^ p[i + j]) * 0x100000001B3ul;

            // Control characters and broken sequences shape as a replacement character, which keeps them per character.
            var r = codepoint < 0 ? new Rune(0xFFFD) : new Rune(codepoint);
            this.EnsureCapacity(count + 2);
            var unitCount = r.EncodeToUtf16(units);
            this.text[count] = units[0];
            this.textToByte[count] = codepoint < 0 ? -1 : i;
            count++;
            if (unitCount == 2)
            {
                this.text[count] = units[1];
                this.textToByte[count] = -1;
                count++;
            }

            i += length;
        }

        if (i == 0)
            return false;

        // The byte that ended the run (read above: a terminator, a line break, a sequence that doesn't fit) is compared
        // with it too, as what follows the last character could join its cluster. A run cut at its maximum length has
        // none.
        var ended = i < MaxRunBytes;
        var key = (hash, i, sized, italic);
        if (!this.runs.TryGetValue(key, out var shaped))
        {
            shaped = new(new nint[i], italic == ItalicMode.Images ? null : this.byteItalic.AsSpan(0, i).ToArray());
            if (count != 0)
                this.Shape(sized, p, count, shaped.Glyphs, italic);
            if (this.runs.Count >= MaxCachedRuns)
                this.runs.Clear();
            this.runs.Add(key, shaped);
        }

        run ??= new ActiveRun();
        run.Set(p, i, ended ? 1 : 0, sized, italic, shaped);
        return true;
    }

    private void EnsureCapacity(int n)
    {
        if (n <= this.text.Length)
            return;
        Array.Resize(ref this.text, Math.Max(n, this.text.Length * 2));
        Array.Resize(ref this.textToByte, this.text.Length);
        Array.Resize(ref this.textElement, this.text.Length);
        Array.Resize(ref this.textItalic, this.text.Length);
    }

    private void Shape(FontReplacer.SizedFont sized, byte* p, int count, nint[] glyphs, ItalicMode italic)
    {
        this.absorbed.Clear();
        count = this.ComposeHangul(count);
        this.AssignElements(sized, count);

        // Each code unit is in italics as its character's byte is (a low surrogate or control character as the one before).
        for (var t = 0; t < count; t++)
        {
            var b = this.textToByte[t];
            this.textItalic[t] = b >= 0 ? this.byteItalic[b] : t > 0 ? this.textItalic[t - 1] : italic != ItalicMode.Upright;
        }

        this.shapingBytes = p;
        this.shapingGlyphs = glyphs;
        this.shapingSized = sized;
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
            this.shapingBytes = null;
            this.shapingGlyphs = null;
            this.shapingSized = null;
        }

        // A jamo composed into its syllable is part of the syllable's cluster.
        foreach (var (b, owner) in this.absorbed)
            glyphs[b] = glyphs[owner] == 0 ? 0 : this.emptyGlyph;
    }

    /// <summary>
    /// Lays the text being shaped out, and collects its clusters as <paramref name="italic"/> says (OnGlyphRun). With
    /// <see cref="ItalicMode.Real"/>, the code units in italics (<see cref="textItalic"/>) are laid out in the faces'
    /// italics, which the system's fallback picks too.
    /// </summary>
    private void Collect(FontReplacer.SizedFont sized, int count, ItalicMode italic)
    {
        // A face measuring as GDI does (hinted, whole-pixel advances) is laid out so; its glyphs are drawn to match.
        var measureMode = sized.Face.MeasureMode;
        var format = this.GetFormat(sized.Face, sized.Px);
        IDWriteTextLayout* layout;
        fixed (char* t = this.text)
        {
            if (measureMode is 1 or 2)
            {
                this.rasterizer.Factory->CreateGdiCompatibleTextLayout(
                    t, (uint)count, (IDWriteTextFormat*)format, 1e6f, 1e6f, 1, null, measureMode == 2 ? BOOL.TRUE : BOOL.FALSE, &layout).ThrowOnError();
            }
            else
            {
                this.rasterizer.Factory->CreateTextLayout(t, (uint)count, (IDWriteTextFormat*)format, 1e6f, 1e6f, &layout).ThrowOnError();
            }
        }

        try
        {
            this.ApplyElements(layout, sized, count);
            for (var start = 0; italic == ItalicMode.Real && start < count;)
            {
                var end = start + 1;
                while (end < count && this.textItalic[end] == this.textItalic[start])
                    end++;
                if (this.textItalic[start])
                    layout->SetFontStyle(DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_ITALIC, new() { startPosition = (uint)start, length = (uint)(end - start) }).ThrowOnError();
                start = end;
            }

            this.shapingItalic = italic;
            this.penShift = 0;
            this.clusters.Clear();
            this.clusterGap = false;
            this.collector.Collect(layout, this);
        }
        finally
        {
            layout->Release();
        }
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
        var upright = this.clusters.ToArray();
        this.textItalic.AsSpan(0, count).Fill(true);
        this.Collect(sized, count, ItalicMode.Real);
        var italic = this.clusters.ToArray();
        this.clusters.Clear();

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
            this.clusters.Add(italic[i].MovedBy(share * i, share * (i + 1)));
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
        const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7;
        const int LCount = 19, VCount = 21, TCount = 28, SCount = LCount * VCount * TCount;

        var w = 0;
        for (var i = 0; i < count;)
        {
            int c = this.text[i];
            var n = 1;
            if (c - LBase is >= 0 and < LCount && i + 1 < count && this.text[i + 1] - VBase is >= 0 and < VCount)
            {
                c = SBase + ((((c - LBase) * VCount) + (this.text[i + 1] - VBase)) * TCount);
                n = 2;
            }

            if ((n == 2 || (c - SBase is >= 0 and < SCount && (c - SBase) % TCount == 0)) &&
                i + n < count && this.text[i + n] - TBase is > 0 and < TCount)
            {
                c += this.text[i + n] - TBase;
                n++;
            }

            for (var j = 1; j < n; j++)
                this.absorbed.Add((this.textToByte[i + j], this.textToByte[i]));
            this.text[w] = (char)c;
            this.textToByte[w] = this.textToByte[i];
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
        var format = this.GetFormat(this.replacer.BuiltInFace, 16);
        IDWriteTextLayout* layout;
        fixed (char* t = text)
            this.rasterizer.Factory->CreateTextLayout(t, (uint)text.Length, (IDWriteTextFormat*)format, 1e6f, 1e6f, &layout).ThrowOnError();
        try
        {
            uint count;
            layout->GetClusterMetrics(null, 0, &count);
            var metrics = count <= 256 ? stackalloc DWRITE_CLUSTER_METRICS[(int)count] : new DWRITE_CLUSTER_METRICS[count];
            fixed (DWRITE_CLUSTER_METRICS* m = metrics)
                layout->GetClusterMetrics(m, count, &count).ThrowOnError();

            var position = 0;
            foreach (ref readonly var cluster in metrics)
            {
                position += cluster.length;
                clusterEnd[position] = true;
                wrapAfter[position] = cluster.canWrapLineAfter != 0;
            }
        }
        finally
        {
            layout->Release();
        }
    }

    /// <summary>
    /// Finds the face element of each code unit of the text (a low surrogate gets its high one's), and puts in the
    /// characters an element draws with another's glyph (its codepoint replacements), where the UTF-16 length allows.
    /// </summary>
    private void AssignElements(FontReplacer.SizedFont sized, int count)
    {
        var face = sized.Face;
        var game = (GameFont*)sized.GameFont;
        for (var i = 0; i < count;)
        {
            var n = char.IsHighSurrogate(this.text[i]) && i + 1 < count && char.IsLowSurrogate(this.text[i + 1]) ? 2 : 1;
            var codepoint = n == 2 ? char.ConvertToUtf32(this.text[i], this.text[i + 1]) : this.text[i];
            var element = face.GetElement(codepoint, game);
            if (element >= 0)
            {
                var drawn = face.GetDrawnCodepoint(element, codepoint);
                if (drawn != codepoint && new Rune(drawn).Utf16SequenceLength == n)
                    new Rune(drawn).EncodeToUtf16(this.text.AsSpan(i, n));
            }

            for (var j = 0; j < n; j++)
                this.textElement[i + j] = element;
            i += n;
        }
    }

    /// <summary>
    /// Sets each element's font, size and features on the text it draws. Text no element draws keeps the format's: the
    /// face's first font, with the system's fallback if the face has it (and dropped otherwise).
    /// </summary>
    private void ApplyElements(IDWriteTextLayout* layout, FontReplacer.SizedFont sized, int count)
    {
        var face = sized.Face;
        for (var start = 0; start < count;)
        {
            var element = this.textElement[start];
            var end = start + 1;
            while (end < count && this.textElement[end] == element)
                end++;

            // Elements not drawn from their fonts are left per character anyway.
            if (element >= 0 && face.IsShaped(element))
            {
                var range = new DWRITE_TEXT_RANGE { startPosition = (uint)start, length = (uint)(end - start) };
                face.ApplyElement(layout, element, range, face.GetElementPx(element, sized.Px));
            }

            start = end;
        }
    }

    /// <summary>
    /// Gets the text format of a face at a size: its first font (Segoe UI if none) at that font's size, with the
    /// system's fallback or none as the face says.
    /// </summary>
    private nint GetFormat(ReplacementFace face, float px)
    {
        var key = (face, (int)MathF.Round(px * 2));
        if (this.formats.TryGetValue(key, out var existing))
            return existing;

        var primary = face.PrimaryElement;
        var lookup = primary >= 0 ? face.GetDef(primary).Lookup : LookupDef.Of("Segoe UI");
        IDWriteTextFormat* format;
        fixed (char* name = lookup.Name)
        fixed (char* locale = "en-us")
        {
            this.rasterizer.Factory->CreateTextFormat(
                name,
                null,
                (DWRITE_FONT_WEIGHT)lookup.Weight,
                (DWRITE_FONT_STYLE)lookup.Style,
                (DWRITE_FONT_STRETCH)lookup.Stretch,
                primary >= 0 ? face.GetElementPx(primary, px) : px,
                locale,
                &format).ThrowOnError();
        }

        try
        {
            format->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP).ThrowOnError();
            IDWriteTextFormat1* format1;
            var iid = IID.IID_IDWriteTextFormat1;
            ((IUnknown*)format)->QueryInterface(&iid, (void**)&format1).ThrowOnError();
            format1->SetFontFallback(face.SystemFallback ? this.fallback : this.rasterizer.NoFallback).ThrowOnError();
            this.formats.Add(key, (nint)format1);
            return (nint)format1;
        }
        finally
        {
            format->Release();
        }
    }

    /// <summary>Gets the key of a cluster's coverage: its face, size and element, and its glyphs at its position.</summary>
    private string GetRasterKey(Cluster c)
    {
        var sized = this.shapingSized!;
        var key = this.cellKey.Clear()
            .Append(sized.Face.Id).Append('|').Append(sized.Px).Append('|').Append(c.Element).Append('|')
            .Append(c.Face).Append('|').Append(c.EmSize).Append('|').Append(c.Origin);
        for (var g = 0; g < c.Glyphs.Length; g++)
        {
            key.Append('|').Append(c.Glyphs[g]).Append(',').Append(c.Advances[g]);
            if (c.Offsets is not null)
                key.Append(',').Append(c.Offsets[g].advanceOffset).Append(',').Append(c.Offsets[g].ascenderOffset);
        }

        return key.ToString();
    }

    /// <summary>Gets a cluster's coverage, relative to its pen, finished as its element says (FinishGlyph).</summary>
    private RasterGlyph GetRaster(Cluster c)
    {
        var key = c.RasterKey ??= this.GetRasterKey(c);
        if (this.rasters.TryGetValue(key, out var raster))
            return raster;

        var sized = this.shapingSized!;
        raster = c.Monospaced ?? this.RasterizeCluster(c, 0, 1);

        // An edge margin makes ink left of the pen: the cell then starts earlier (PlaceClusters), keeping the spacing.
        raster = this.replacer.FinishGlyph(sized, c.Element, raster);
        this.rasters.Add(key, raster);
        return raster;
    }

    /// <summary>Rasterizes a cluster as its element draws it, squeezed horizontally by <paramref name="squeezeX"/>.</summary>
    private RasterGlyph RasterizeCluster(Cluster c, int advance, float squeezeX)
    {
        fixed (ushort* glyphs = c.Glyphs)
        fixed (float* advances = c.Advances)
        fixed (DWRITE_GLYPH_OFFSET* offsets = c.Offsets)
        {
            return this.shapingSized!.Face.RasterizeRun(
                c.Element, (IDWriteFontFace*)c.Face, c.EmSize, glyphs, advances, offsets, (uint)c.Glyphs.Length, c.Origin, advance, squeezeX);
        }
    }

    /// <summary>
    /// Gets the cell of a cluster whose box starts <paramref name="pad"/> pixels left of its pen and advances by
    /// <paramref name="advance"/>.
    /// </summary>
    private nint GetCell(Cluster c, int pad, int advance)
    {
        var sized = this.shapingSized!;
        var raster = this.GetRaster(c);
        var key = $"{pad}|{advance}|{c.RasterKey}";
        if (this.cells.TryGetValue(key, out var cell))
            return cell;

        raster = raster with { Advance = advance, Left = raster.Left + pad };
        var b = this.textToByte[c.TextStart];
        var utf8 = b < 0 ? 0u : GameUtf8.PackSequence(this.shapingBytes + b, SequenceLength(this.shapingBytes[b]));
        cell = (nint)this.replacer.PlaceCell(sized, raster, utf8);
        if (c.RealItalic)
            this.replacer.MarkRealItalic(cell);
        this.cells.Add(key, cell);
        return cell;
    }

    /// <summary>A cluster of a layout: its glyphs and where they are, and the text it covers.</summary>
    private sealed class Cluster
    {
        public nint Face { get; init; }

        public float EmSize { get; init; }

        public ushort[] Glyphs { get; init; } = [];

        public float[] Advances { get; init; } = [];

        public DWRITE_GLYPH_OFFSET[]? Offsets { get; init; }

        /// <summary>The key of the cluster's coverage, once made (GetRasterKey).</summary>
        public string? RasterKey { get; set; }

        /// <summary>The fraction of a pixel the pen is at, in steps of a quarter.</summary>
        public float Origin { get; init; }

        /// <summary>The whole-pixel pen at the cluster's start and end.</summary>
        public int X0 { get; init; }

        public int X1 { get; set; }

        /// <summary>The cluster's coverage placed in its monospaced cell, if its element is monospaced.</summary>
        public RasterGlyph? Monospaced { get; set; }

        public int TextStart { get; init; }

        public int TextEnd { get; init; }

        /// <summary>The face element of the cluster's first character (or <see cref="ReplacementFace.NoElement"/>).</summary>
        public int Element { get; init; }

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
            var x0 = MathF.Round(pen);
            return new()
            {
                Face = this.Face,
                EmSize = this.EmSize,
                Glyphs = this.Glyphs,
                Advances = this.Advances,
                Offsets = this.Offsets,
                Origin = MathF.Round((pen - x0) * SubpixelSteps) / SubpixelSteps,
                X0 = (int)x0,
                X1 = (int)MathF.Round(this.X1 + end),
                Monospaced = this.Monospaced,
                TextStart = this.TextStart,
                TextEnd = this.TextEnd,
                Element = this.Element,
                Missing = this.Missing,
                AfterGap = this.AfterGap,
                RealItalic = this.RealItalic,
            };
        }
    }

    /// <summary>
    /// A run shaped at an address. The game reuses its buffers (each line drawn from one copy buffer, each word measured
    /// in another), so an address says nothing about the text there now: the run keeps a copy of its bytes, and a
    /// lookup in it only uses it while the text from the looked-up character to the run's end is still the same.
    /// </summary>
    /// <summary>
    /// A shaped run: the glyph for each byte, and whether each character's byte is in italics (null for a run in a node's
    /// italics, all of it in them).
    /// </summary>
    private sealed record ShapedRun(nint[] Glyphs, bool[]? ItalicBytes);

    private sealed class ActiveRun
    {
        private byte[] bytes = new byte[64];
        private int compareLength;

        public byte* Start { get; private set; }

        public byte* End { get; private set; }

        public FontReplacer.SizedFont? Sized { get; private set; }

        public ItalicMode Italic { get; private set; }

        public nint[] Glyphs { get; private set; } = [];

        private bool[]? italicBytes;

        public void Set(byte* start, int length, int endBytes, FontReplacer.SizedFont sized, ItalicMode italic, ShapedRun shaped)
        {
            this.Start = start;
            this.End = start + length;
            this.Sized = sized;
            this.Italic = italic;
            this.Glyphs = shaped.Glyphs;
            this.italicBytes = shaped.ItalicBytes;
            this.compareLength = length + endBytes;
            if (this.bytes.Length < this.compareLength)
                this.bytes = new byte[Math.Max(this.compareLength, this.bytes.Length * 2)];
            new ReadOnlySpan<byte>(start, this.compareLength).CopyTo(this.bytes);
        }

        /// <summary>
        /// Gets the italics the character at <paramref name="p"/> (inside the run) was shaped in: as the run's italic macros
        /// left them, or the node's italics of a run spaced to its upright width.
        /// </summary>
        public ItalicMode ItalicAt(byte* p) =>
            this.italicBytes is null ? this.Italic : this.italicBytes[p - this.Start] ? ItalicMode.Real : ItalicMode.Upright;

        /// <summary>
        /// Gets whether the text from <paramref name="p"/> (inside the run) to its end is what was shaped. Compared byte
        /// by byte, stopping at the first difference: the copy has no terminator before its end, so a shorter text stops
        /// it before reading past its own terminator.
        /// </summary>
        public bool Matches(byte* p)
        {
            for (var i = (int)(p - this.Start); i < this.compareLength; i++)
            {
                if (this.Start[i] != this.bytes[i])
                    return false;
            }

            return true;
        }
    }
}
