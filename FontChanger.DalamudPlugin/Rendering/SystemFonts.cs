using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DirectX;

namespace CustomFonts;

/// <summary>A font family of the system: the name it is looked up by (English), the name shown, and its faces.</summary>
internal sealed record SystemFontFamily(string Name, string DisplayName, IReadOnlyList<SystemFontFace> Faces);

/// <summary>A face of a family by its weight, stretch and style (DWRITE_* values), and its name (Regular, Bold Italic, ...).</summary>
internal sealed record SystemFontFace(int Weight, int Stretch, int Style, string Name);

/// <summary>
/// The system's fonts, for choosing one per game font family and measuring it for <see cref="FaceFromFont"/>. Its own
/// DirectWrite factory is shared, and so usable from any thread.
/// </summary>
internal sealed unsafe class SystemFonts : IDisposable
{
    private readonly object sync = new();
    private IDWriteFactory* factory;
    private IDWriteFontCollection* collection;
    private IReadOnlyList<SystemFontFamily>? families;

    public SystemFonts()
    {
        IDWriteFactory* f;
        var iid = IID.IID_IDWriteFactory;
        DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &iid, (IUnknown**)&f).ThrowOnError();
        this.factory = f;
        this.Refresh();
    }

    /// <summary>Gets the families, sorted by the names shown; read once, until <see cref="Refresh"/>.</summary>
    public IReadOnlyList<SystemFontFamily> Families
    {
        get
        {
            lock (this.sync)
                return this.families ??= this.ReadFamilies();
        }
    }

    /// <summary>Reads the system's fonts again, as installed now.</summary>
    public void Refresh()
    {
        lock (this.sync)
        {
            if (this.collection is not null)
                this.collection->Release();
            IDWriteFontCollection* c;
            this.factory->GetSystemFontCollection(&c, BOOL.TRUE).ThrowOnError();
            this.collection = c;
            this.families = null;
        }
    }

    /// <summary>Gets measurements of the font of a family closest to a weight, stretch and style; null if it isn't installed.</summary>
    public Probe? Open(LookupDef lookup)
    {
        lock (this.sync)
        {
            uint index;
            BOOL exists;
            fixed (char* name = lookup.Name)
            {
                if (this.collection->FindFamilyName(name, &index, &exists).FAILED || !exists)
                    return null;
            }

            IDWriteFontFamily* family;
            if (this.collection->GetFontFamily(index, &family).FAILED)
                return null;
            try
            {
                IDWriteFont* font;
                return family->GetFirstMatchingFont((DWRITE_FONT_WEIGHT)lookup.Weight, (DWRITE_FONT_STRETCH)lookup.Stretch, (DWRITE_FONT_STYLE)lookup.Style, &font).SUCCEEDED
                           ? new Probe(font)
                           : null;
            }
            finally
            {
                family->Release();
            }
        }
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            if (this.collection is not null)
                this.collection->Release();
            this.collection = null;
            if (this.factory is not null)
                this.factory->Release();
            this.factory = null;
        }
    }

    /// <summary>Gets a localized string in the user's language, else English, else the first.</summary>
    private static string GetString(IDWriteLocalizedStrings* strings, string locale)
    {
        uint index;
        BOOL exists;
        fixed (char* l = locale)
        {
            if (strings->FindLocaleName(l, &index, &exists).FAILED || !exists)
            {
                fixed (char* en = "en-us")
                {
                    if (strings->FindLocaleName(en, &index, &exists).FAILED || !exists)
                        index = 0;
                }
            }
        }

        uint length;
        if (strings->GetCount() == 0 || strings->GetStringLength(index, &length).FAILED)
            return string.Empty;
        var buffer = new char[length + 1];
        fixed (char* p = buffer)
            return strings->GetString(index, p, length + 1).SUCCEEDED ? new(p, 0, (int)length) : string.Empty;
    }

    private IReadOnlyList<SystemFontFamily> ReadFamilies()
    {
        var locale = CultureInfo.CurrentUICulture.Name;
        var res = new List<SystemFontFamily>();
        for (var i = 0u; i < this.collection->GetFontFamilyCount(); i++)
        {
            IDWriteFontFamily* family;
            if (this.collection->GetFontFamily(i, &family).FAILED)
                continue;
            try
            {
                IDWriteLocalizedStrings* names;
                if (family->GetFamilyNames(&names).FAILED)
                    continue;
                string name, displayName;
                try
                {
                    name = GetString(names, "en-us");
                    displayName = GetString(names, locale);
                }
                finally
                {
                    names->Release();
                }

                // The faces it has, not those DirectWrite would simulate.
                var faces = new List<SystemFontFace>();
                for (var j = 0u; j < family->GetFontCount(); j++)
                {
                    IDWriteFont* font;
                    if (family->GetFont(j, &font).FAILED)
                        continue;
                    try
                    {
                        if (font->GetSimulations() != DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE)
                            continue;
                        IDWriteLocalizedStrings* faceNames;
                        var faceName = string.Empty;
                        if (font->GetFaceNames(&faceNames).SUCCEEDED)
                        {
                            faceName = GetString(faceNames, locale);
                            faceNames->Release();
                        }

                        faces.Add(new((int)font->GetWeight(), (int)font->GetStretch(), (int)font->GetStyle(), faceName));
                    }
                    finally
                    {
                        font->Release();
                    }
                }

                if (name.Length != 0 && faces.Count != 0)
                {
                    res.Add(new(
                        name,
                        displayName.Length != 0 ? displayName : name,
                        faces.DistinctBy(f => (f.Weight, f.Stretch, f.Style)).OrderBy(f => f.Stretch != 5).ThenBy(f => f.Style).ThenBy(f => f.Weight).ToList()));
                }
            }
            finally
            {
                family->Release();
            }
        }

        return res.OrderBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Measurements of a font for <see cref="FaceFromFont"/>.</summary>
    public sealed class Probe : IFontProbe, IDisposable
    {
        private readonly HashSet<int> bitmapSizes = [];
        private readonly ushort unitsPerEm;
        private IDWriteFont* font;
        private IDWriteFontFace* face;

        public Probe(IDWriteFont* font)
        {
            this.font = font;
            IDWriteFontFace* f;
            font->CreateFontFace(&f).ThrowOnError();
            this.face = f;
            DWRITE_FONT_METRICS metrics;
            f->GetMetrics(&metrics);
            this.unitsPerEm = metrics.designUnitsPerEm;

            // Sizes of bitmaps: ppemY of the BitmapSize records of 48 bytes after the header of EBLC or CBLC.
            foreach (var tag in new[] { FaceFromFont.Tag("EBLC"), FaceFromFont.Tag("CBLC") })
            {
                void* data;
                uint size;
                void* context;
                BOOL exists;
                if (f->TryGetFontTable(tag, &data, &size, &context, &exists).FAILED || !exists)
                    continue;
                var p = (byte*)data;
                if (size >= 8)
                {
                    var count = ((uint)p[4] << 24) | ((uint)p[5] << 16) | ((uint)p[6] << 8) | p[7];
                    for (var i = 0u; i < count && 8 + (48 * (i + 1)) <= size; i++)
                        this.bitmapSizes.Add(p[8 + (48 * i) + 45]);
                }

                f->ReleaseFontTable(context);
            }
        }

        public bool Has(string text)
        {
            if (text.StartsWith('_'))
                text = text[1..];
            foreach (var rune in text.EnumerateRunes())
            {
                BOOL exists;
                if (rune.Value != '\n' && (this.font->HasCharacter((uint)rune.Value, &exists).FAILED || !exists))
                    return false;
            }

            return true;
        }

        public float GetInkHeight(int codepoint)
        {
            var c = (uint)codepoint;
            ushort glyph;
            if (this.face->GetGlyphIndices(&c, 1, &glyph).FAILED || glyph == 0)
                return 0;
            DWRITE_GLYPH_METRICS gm;
            if (this.face->GetDesignGlyphMetrics(&glyph, 1, &gm, BOOL.FALSE).FAILED)
                return 0;
            var top = gm.verticalOriginY - gm.topSideBearing;
            var bottom = top - ((int)gm.advanceHeight - gm.topSideBearing - gm.bottomSideBearing);
            return (float)(top - Math.Max(0, bottom)) / this.unitsPerEm;
        }

        public bool HasBitmapOf(int ppem) => this.bitmapSizes.Contains(ppem);

        public (float Left, float Right, float Advance) GetHorizontalMetrics(int codepoint)
        {
            var c = (uint)codepoint;
            ushort glyph;
            DWRITE_GLYPH_METRICS gm;
            if (this.face->GetGlyphIndices(&c, 1, &glyph).FAILED || glyph == 0 || this.face->GetDesignGlyphMetrics(&glyph, 1, &gm, BOOL.FALSE).FAILED)
                return default;
            return ((float)gm.leftSideBearing / this.unitsPerEm, (float)gm.rightSideBearing / this.unitsPerEm, (float)gm.advanceWidth / this.unitsPerEm);
        }

        public bool HasTabularDigits()
        {
            if (this.HasFeature(FaceFromFont.Tag("GSUB"), "tnum") || this.HasFeature(FaceFromFont.Tag("GPOS"), "tnum"))
                return true;

            // Digits as wide as each other by default (as most fonts' are).
            var codepoints = stackalloc uint[10];
            for (var i = 0; i < 10; i++)
                codepoints[i] = (uint)('0' + i);
            var glyphs = stackalloc ushort[10];
            var metrics = stackalloc DWRITE_GLYPH_METRICS[10];
            if (this.face->GetGlyphIndices(codepoints, 10, glyphs).FAILED || this.face->GetDesignGlyphMetrics(glyphs, 10, metrics, BOOL.FALSE).FAILED)
                return false;
            for (var i = 0; i < 10; i++)
            {
                if (glyphs[i] == 0 || metrics[i].advanceWidth != metrics[0].advanceWidth)
                    return false;
            }

            return true;
        }

        /// <summary>Gets whether an OpenType layout table (GSUB or GPOS) has a feature of a tag in its feature list.</summary>
        private bool HasFeature(uint table, string feature)
        {
            void* data;
            uint size;
            void* context;
            BOOL exists;
            if (this.face->TryGetFontTable(table, &data, &size, &context, &exists).FAILED || !exists)
                return false;
            try
            {
                // The header: version (4 bytes), script list offset, feature list offset; the feature list: a count, then
                // records of a tag (4 bytes) and an offset.
                var p = (byte*)data;
                if (size < 10)
                    return false;
                var list = (p[6] << 8) | p[7];
                if (list + 2 > size)
                    return false;
                var count = (p[list] << 8) | p[list + 1];
                for (var i = 0; i < count && list + 2 + (6 * (i + 1)) <= size; i++)
                {
                    var tag = p + list + 2 + (6 * i);
                    if (tag[0] == feature[0] && tag[1] == feature[1] && tag[2] == feature[2] && tag[3] == feature[3])
                        return true;
                }

                return false;
            }
            finally
            {
                this.face->ReleaseFontTable(context);
            }
        }

        public void Dispose()
        {
            if (this.face is not null)
                this.face->Release();
            this.face = null;
            if (this.font is not null)
                this.font->Release();
            this.font = null;
        }
    }
}
