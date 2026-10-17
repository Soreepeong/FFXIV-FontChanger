using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;

using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DirectX;

namespace FontChanger.DalamudPlugin.PluginInterface;

/// <summary>
/// The system's fonts, measured for <see cref="FaceFromFont"/>. Its own DirectWrite factory, made when first used, is
/// shared, and so usable from any thread.
/// </summary>
internal sealed unsafe class SystemFonts : IDisposable
{
    private readonly Lock _sync = new();
    private MyComPtr<IDWriteFactory7> _factory;
    private MyComPtr<IDWriteFontCollection3> _collection;

    /// <summary>Gets measurements of the font of a family closest to a weight, stretch and style; false if it isn't installed.</summary>
    public bool TryOpen(LookupStruct lookup, [NotNullWhen(true)] out Probe? probe)
    {
        probe = null;
        lock (this._sync)
        {
            if (!this._collection)
            {
                var iid = IID.IID_IDWriteFactory7;
                this._factory.Reset();
                fixed (IDWriteFactory7** factory = this._factory)
                    DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &iid, (IUnknown**)factory).ThrowOnError();

                // Weight, stretch and style families, as FontServices's.
                using var collection = default(MyComPtr<IDWriteFontCollection>);
                this._factory.Value->GetSystemFontCollection(collection.GetAddressOf(), BOOL.TRUE).ThrowOnError();
                collection.CopyTo(ref this._collection).ThrowOnError();
            }

            uint index;
            fixed (char* name = lookup.Name)
            {
                BOOL exists;
                if (this._collection.Value->FindFamilyName(name, &index, &exists).FAILED || !exists)
                    return false;
            }

            using var family = default(MyComPtr<IDWriteFontFamily2>);
            if (this._collection.Value->GetFontFamily(index, family.GetAddressOf()).FAILED)
                return false;
            using var font = default(MyComPtr<IDWriteFont3>);
            if (family.Value->GetFirstMatchingFont(
                    (DWRITE_FONT_WEIGHT)lookup.Weight,
                    (DWRITE_FONT_STRETCH)lookup.Stretch,
                    (DWRITE_FONT_STYLE)lookup.Style,
                    font.GetAddressOf()).FAILED)
                return false;
            using var face = default(MyComPtr<IDWriteFontFace5>);
            if (font.Value->CreateFontFace(face.GetAddressOf()).FAILED)
                return false;
            probe = new(face.Value);
            return true;
        }
    }

    public void Dispose()
    {
        lock (this._sync)
        {
            this._collection.Dispose();
            this._factory.Dispose();
        }
    }

    /// <summary>Measurements of a font face for <see cref="FaceFromFont"/>.</summary>
    public sealed class Probe : IFontProbe, IDisposable
    {
        private readonly HashSet<int> _bitmapSizes = [];
        private readonly ushort _unitsPerEm;
        private MyComPtr<IDWriteFontFace5> _face;

        /// <summary>Takes a reference of its own to the face.</summary>
        public Probe(IDWriteFontFace5* face)
        {
            this._face = new(face, true);
            DWRITE_FONT_METRICS metrics;
            face->GetMetrics(&metrics);
            this._unitsPerEm = metrics.designUnitsPerEm;

            // Sizes of bitmaps: ppemY of the BitmapSize records of 48 bytes after the header of EBLC or CBLC.
            foreach (var tag in new[] { Preset.Tag("EBLC"u8), Preset.Tag("CBLC"u8) })
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
                        this._bitmapSizes.Add(p[8 + (48 * i) + 45]);
                }

                face->ReleaseFontTable(context);
            }
        }

        public bool TryGetGlyph(Rune rune, out GlyphBox box)
        {
            ushort glyph;
            DWRITE_GLYPH_METRICS gm;
            if (this._face.Value->GetGlyphIndices((uint*)&rune, 1, &glyph).FAILED || glyph == 0 ||
                this._face.Value->GetDesignGlyphMetrics(&glyph, 1, &gm, BOOL.FALSE).FAILED)
            {
                box = default;
                return false;
            }

            var top = gm.verticalOriginY - gm.topSideBearing;
            var bottom = top - ((int)gm.advanceHeight - gm.topSideBearing - gm.bottomSideBearing);
            float em = this._unitsPerEm;
            box = new(gm.leftSideBearing / em, gm.rightSideBearing / em, gm.advanceWidth / em, top / em, bottom / em);
            return true;
        }

        public bool HasBitmapOf(int ppem) => this._bitmapSizes.Contains(ppem);

        public bool HasGposFeature(string tag) => this.HasFeature(Preset.Tag("GPOS"u8), tag);
        public bool HasGsubFeature(string tag) => this.HasFeature(Preset.Tag("GSUB"u8), tag);

        public void Dispose() => this._face.Dispose();

        /// <summary>Gets whether an OpenType layout table (GSUB or GPOS) has a feature of a tag in its feature list.</summary>
        private bool HasFeature(uint table, string feature)
        {
            void* data;
            uint size;
            void* context;
            BOOL exists;
            if (this._face.Value->TryGetFontTable(table, &data, &size, &context, &exists).FAILED || !exists)
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
                this._face.Value->ReleaseFontTable(context);
            }
        }
    }
}
