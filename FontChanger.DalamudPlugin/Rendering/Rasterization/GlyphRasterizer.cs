using System;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Rendering.Fonts;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DirectX;

namespace FontChanger.DalamudPlugin.Rendering.Rasterization;

/// <summary>Finds system fonts and rasterizes glyph runs with DirectWrite, grayscale antialiased.</summary>
internal sealed unsafe class GlyphRasterizer : IDisposable
{
    private ComPtr<IDWriteFactory2> _factory;
    private ComPtr<IDWriteFactory3> _factory3;
    private ComPtr<IDWriteFontCollection> _systemFonts;
    private ComPtr<IDWriteFontFallback> _noFallback;

    public GlyphRasterizer()
    {
        try
        {
            var iid = IID.IID_IDWriteFactory2;
            DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &iid, (IUnknown**)this._factory.GetAddressOf()).ThrowOnError();

            this._factory.CopyTo(this._factory3.GetAddressOf()).ThrowOnError();
            this._factory.Get()->GetSystemFontCollection(this._systemFonts.GetAddressOf(), BOOL.FALSE).ThrowOnError();

            using var builder = default(ComPtr<IDWriteFontFallbackBuilder>);
            this._factory.Get()->CreateFontFallbackBuilder(builder.GetAddressOf()).ThrowOnError();
            builder.Get()->CreateFontFallback(this._noFallback.GetAddressOf()).ThrowOnError();

            this.FreeType = FreeTypeFonts.Create();
            try
            {
                this.Images = new();
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Direct2D can't be used; glyph images and SVG shapes aren't drawn");
            }
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    /// <summary>Gets the DirectWrite factory (shared; owned by this object).</summary>
    public IDWriteFactory2* Factory => this._factory.Get();

    /// <summary>Gets FreeType, or null if its native library can't be loaded.</summary>
    public FreeTypeFonts? FreeType { get; }

    /// <summary>Gets Direct2D's drawing of SVG documents and bitmaps, or null if it can't be used.</summary>
    public ImageRenderer? Images { get; }

    /// <summary>Gets a font fallback that falls back to nothing: text is laid out in its own font only.</summary>
    public IDWriteFontFallback* NoFallback => this._noFallback.Get();

    /// <summary>
    /// Gets or sets whether <see cref="RasterizeRun"/> gives ClearType coverage too, in which order. Framework thread, before
    /// glyphs are made again.
    /// </summary>
    public SubpixelOrder SubpixelOrder { get; set; }

    /// <summary>
    /// Finds the font of a family closest to a weight, stretch and style (simulated if the family lacks it); false if the
    /// family isn't installed. The caller releases it.
    /// </summary>
    public bool TryFindFont(LookupStruct lookup, out ComPtr<IDWriteFont> match)
    {
        match = default;
        uint index;
        BOOL exists;
        fixed (char* name = lookup.Name)
            this._systemFonts.Get()->FindFamilyName(name, &index, &exists).ThrowOnError();
        if (!exists)
            return false;

        using var fontFamily = default(ComPtr<IDWriteFontFamily>);
        this._systemFonts.Get()->GetFontFamily(index, fontFamily.GetAddressOf()).ThrowOnError();
        var font = default(ComPtr<IDWriteFont>);
        fontFamily.Get()->GetFirstMatchingFont(
            (DWRITE_FONT_WEIGHT)lookup.Weight,
            (DWRITE_FONT_STRETCH)lookup.Stretch,
            (DWRITE_FONT_STYLE)lookup.Style,
            font.GetAddressOf()).ThrowOnError();
        match = font;
        return true;
    }

    /// <summary>
    /// Makes a text format that doesn't wrap, of a family at a weight, style and stretch (DWRITE_* values) and a size,
    /// with a font fallback. The caller releases it.
    /// </summary>
    public ComPtr<IDWriteTextFormat1> CreateFormat(string family, int weight, int style, int stretch, float size, IDWriteFontFallback* fallback)
    {
        using var format = default(ComPtr<IDWriteTextFormat>);
        fixed (char* name = family)
        fixed (char* locale = "en-us")
        {
            this._factory.Get()->CreateTextFormat(
                name,
                null,
                (DWRITE_FONT_WEIGHT)weight,
                (DWRITE_FONT_STYLE)style,
                (DWRITE_FONT_STRETCH)stretch,
                size,
                locale,
                format.GetAddressOf()).ThrowOnError();
        }

        format.Get()->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP).ThrowOnError();
        var format1 = default(ComPtr<IDWriteTextFormat1>);
        try
        {
            format.CopyTo(format1.GetAddressOf()).ThrowOnError();
            format1.Get()->SetFontFallback(fallback).ThrowOnError();
            return format1;
        }
        catch
        {
            format1.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Rasterizes a run of glyphs of one face (a glyph, or a shaped cluster: a ligature, a base with its marks) with the
    /// pen at <paramref name="originX"/>, a fraction of a pixel, so the cluster lands where shaping put it.
    /// </summary>
    public RasterGlyph RasterizeRun(
        IDWriteFontFace* face,
        float px,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        DirectWriteParams parameters,
        Matrix2x2 transform)
    {
        // Grayscale is the mean of the three subpixels; aliased text has no ClearType.
        var subpixel = this.SubpixelOrder != SubpixelOrder.None &&
                       (DWRITE_RENDERING_MODE1)parameters.RenderMode != DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_ALIASED;
        using var analysis = this.CreateAnalysis(
            face,
            px,
            glyphs,
            advances,
            offsets,
            count,
            originX,
            parameters,
            transform,
            subpixel ? DWRITE_TEXT_ANTIALIAS_MODE.DWRITE_TEXT_ANTIALIAS_MODE_CLEARTYPE : DWRITE_TEXT_ANTIALIAS_MODE.DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        RECT bounds;
        if (subpixel)
        {
            analysis.Get()->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds).ThrowOnError();
            var cw = bounds.right - bounds.left;
            var ch = bounds.bottom - bounds.top;
            if (cw > 0 && ch > 0)
            {
                var lcd = new byte[cw * ch * 3];
                fixed (byte* p = lcd)
                {
                    analysis.Get()->CreateAlphaTexture(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds, p, (uint)lcd.Length)
                        .ThrowOnError();
                }

                var mean = new byte[cw * ch];
                for (var i = 0; i < mean.Length; i++)
                {
                    mean[i] = (byte)((lcd[i * 3] + lcd[(i * 3) + 1] + lcd[(i * 3) + 2] + 1) / 3);
                    if (this.SubpixelOrder == SubpixelOrder.Bgr)
                        (lcd[i * 3], lcd[(i * 3) + 2]) = (lcd[(i * 3) + 2], lcd[i * 3]);
                }

                return new(advance, bounds.left, bounds.top, cw, ch, mean, lcd);
            }

            // A font may still render aliased (its recommended mode): the 1x1 texture has it then.
        }

        // With grayscale antialiasing, the 1x1 texture type gives 8-bit coverage.
        analysis.Get()->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_ALIASED_1x1, &bounds).ThrowOnError();
        var w = bounds.right - bounds.left;
        var h = bounds.bottom - bounds.top;
        var alpha = w > 0 && h > 0 ? new byte[w * h] : [];
        if (alpha.Length != 0)
        {
            fixed (byte* p = alpha)
            {
                analysis.Get()->CreateAlphaTexture(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_ALIASED_1x1, &bounds, p, (uint)alpha.Length)
                    .ThrowOnError();
            }
        }
        else
        {
            w = h = 0;
        }

        return new(advance, bounds.left, bounds.top, w, h, alpha);
    }

    /// <summary>Gets the pixels a run of glyphs covers with the pen at the origin, as <see cref="RasterizeRun"/> draws it.</summary>
    public RECT GetRunBounds(
        IDWriteFontFace* face,
        float px,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        DirectWriteParams parameters,
        Matrix2x2 transform)
    {
        using var analysis = this.CreateAnalysis(face, px, glyphs, advances, offsets, count, 0, parameters, transform);
        RECT bounds;
        analysis.Get()->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_ALIASED_1x1, &bounds).ThrowOnError();
        return bounds;
    }

    /// <summary>Makes DirectWrite's analysis of a glyph run, which the caller releases.</summary>
    private ComPtr<IDWriteGlyphRunAnalysis> CreateAnalysis(
        IDWriteFontFace* face,
        float px,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        DirectWriteParams parameters,
        Matrix2x2 transform,
        DWRITE_TEXT_ANTIALIAS_MODE antialiasMode = DWRITE_TEXT_ANTIALIAS_MODE.DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE)
    {
        // DirectWrite transforms row vectors; the origin is on screen, after the transformation.
        var matrix = new DWRITE_MATRIX
        {
            m11 = transform.M11,
            m12 = transform.M21,
            m21 = transform.M12,
            m22 = transform.M22,
            dx = originX,
            dy = 0,
        };

        var run = new DWRITE_GLYPH_RUN
        {
            fontFace = face,
            fontEmSize = px,
            glyphCount = count,
            glyphIndices = glyphs,
            glyphAdvances = advances,
            glyphOffsets = offsets,
            isSideways = BOOL.FALSE,
            bidiLevel = 0,
        };

        var measuringMode = (DWRITE_MEASURING_MODE)parameters.MeasureMode;
        var renderingMode = (DWRITE_RENDERING_MODE1)parameters.RenderMode;
        if (renderingMode == DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_DEFAULT)
        {
            DWRITE_RENDERING_MODE recommended;
            face->GetRecommendedRenderingMode(px, 1, measuringMode, null, &recommended);
            renderingMode = (DWRITE_RENDERING_MODE1)recommended;
        }

        // Outlines are not rasterized by glyph run analysis.
        if (renderingMode is DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_OUTLINE or DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_DEFAULT)
            renderingMode = DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_NATURAL_SYMMETRIC;

        var analysis = default(ComPtr<IDWriteGlyphRunAnalysis>);
        var gridFitMode = (DWRITE_GRID_FIT_MODE)parameters.GridFitMode;
        for (var attempt = 0;; attempt++)
        {
            var hr = this._factory3.Get()->CreateGlyphRunAnalysis(
                &run,
                &matrix,
                renderingMode,
                measuringMode,
                gridFitMode,
                antialiasMode,
                0,
                0,
                analysis.GetAddressOf());
            if (hr.SUCCEEDED)
                return analysis;
            if (attempt != 0)
                hr.ThrowOnError();

            // Parameters DirectWrite refuses are tried again as the defaults.
            (renderingMode, measuringMode, gridFitMode) = (
                                                              DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_NATURAL_SYMMETRIC,
                                                              DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL,
                                                              DWRITE_GRID_FIT_MODE.DWRITE_GRID_FIT_MODE_DEFAULT);
        }
    }

    public void Dispose()
    {
        this.Images?.Dispose();
        this.FreeType?.Dispose();
        this._noFallback.Dispose();
        this._systemFonts.Dispose();
        this._factory3.Dispose();
        this._factory.Dispose();
    }
}
