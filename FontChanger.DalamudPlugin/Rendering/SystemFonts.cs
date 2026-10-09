using System;
using System.Collections.Generic;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DirectX;

namespace CustomFonts;

/// <summary>
/// The system's fonts, measured for <see cref="FaceFromFont"/>. Its own DirectWrite factory, made when first used, is
/// shared, and so usable from any thread.
/// </summary>
internal sealed unsafe class SystemFonts : IDisposable
{
    private readonly object sync = new();
    private IDWriteFactory* factory;
    private IDWriteFontCollection* collection;

    /// <summary>Gets measurements of the font of a family closest to a weight, stretch and style; null if it isn't installed.</summary>
    public Probe? Open(LookupDef lookup)
    {
        lock (this.sync)
        {
            if (this.collection is null)
            {
                IDWriteFactory* f;
                var iid = IID.IID_IDWriteFactory;
                DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &iid, (IUnknown**)&f).ThrowOnError();
                this.factory = f;
                IDWriteFontCollection* c;
                f->GetSystemFontCollection(&c, BOOL.TRUE).ThrowOnError();
                this.collection = c;
            }

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
                if (family->GetFirstMatchingFont((DWRITE_FONT_WEIGHT)lookup.Weight, (DWRITE_FONT_STRETCH)lookup.Stretch, (DWRITE_FONT_STYLE)lookup.Style, &font).FAILED)
                    return null;
                try
                {
                    IDWriteFontFace* face;
                    return font->CreateFontFace(&face).SUCCEEDED ? new Probe(face) : null;
                }
                finally
                {
                    font->Release();
                }
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

    /// <summary>Measurements of a font face for <see cref="FaceFromFont"/>.</summary>
    public sealed class Probe : IFontProbe, IDisposable
    {
        private readonly HashSet<int> bitmapSizes = [];
        private readonly ushort unitsPerEm;
        private IDWriteFontFace* face;

        public Probe(IDWriteFontFace* face)
        {
            this.face = face;
            DWRITE_FONT_METRICS metrics;
            face->GetMetrics(&metrics);
            this.unitsPerEm = metrics.designUnitsPerEm;

            // Sizes of bitmaps: ppemY of the BitmapSize records of 48 bytes after the header of EBLC or CBLC.
            foreach (var tag in new[] { Preset.Tag("EBLC"), Preset.Tag("CBLC") })
            {
                void* data;
                uint size;
                void* context;
                BOOL exists;
                if (face->TryGetFontTable(tag, &data, &size, &context, &exists).FAILED || !exists)
                    continue;
                var p = (byte*)data;
                if (size >= 8)
                {
                    var count = ((uint)p[4] << 24) | ((uint)p[5] << 16) | ((uint)p[6] << 8) | p[7];
                    for (var i = 0u; i < count && 8 + (48 * (i + 1)) <= size; i++)
                        this.bitmapSizes.Add(p[8 + (48 * i) + 45]);
                }

                face->ReleaseFontTable(context);
            }
        }

        public GlyphBox? GetGlyph(int codepoint)
        {
            var c = (uint)codepoint;
            ushort glyph;
            DWRITE_GLYPH_METRICS gm;
            if (this.face->GetGlyphIndices(&c, 1, &glyph).FAILED || glyph == 0 || this.face->GetDesignGlyphMetrics(&glyph, 1, &gm, BOOL.FALSE).FAILED)
                return null;
            var top = gm.verticalOriginY - gm.topSideBearing;
            var bottom = top - ((int)gm.advanceHeight - gm.topSideBearing - gm.bottomSideBearing);
            float em = this.unitsPerEm;
            return new(gm.leftSideBearing / em, gm.rightSideBearing / em, gm.advanceWidth / em, top / em, bottom / em);
        }

        public bool HasBitmapOf(int ppem) => this.bitmapSizes.Contains(ppem);

        public bool HasFeature(string tag) => this.HasFeature(Preset.Tag("GSUB"), tag) || this.HasFeature(Preset.Tag("GPOS"), tag);

        public void Dispose()
        {
            if (this.face is not null)
                this.face->Release();
            this.face = null;
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
    }
}
