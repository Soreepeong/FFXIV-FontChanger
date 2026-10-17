using System;
using System.Collections.Generic;

using FontChanger.DalamudPlugin.Rendering.Enums;
using FontChanger.DalamudPlugin.Rendering.Faces.Sources.Outline;
using FontChanger.DalamudPlugin.Rendering.Records;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using FreeTypeSharp;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Rasterization;

/// <summary>
/// Rasterizes glyph runs with DirectWrite, grayscale antialiased, with ClearType coverage too for a subpixel order
/// (vertical ones drawn turned a quarter, or with FreeType's LCD_V rendering).
/// </summary>
internal sealed unsafe class DirectWriteGlyphRasterizer(IDWriteFactory7* factory, FreeTypeGlyphRasterizer freeType)
{
    /// <summary>Gets or sets whether <see cref="RasterizeRun"/> gives ClearType coverage too, in which order. Framework thread, before glyphs are made again.</summary>
    public SubpixelOrder SubpixelOrder { get; set; }

    /// <summary>
    /// Gets or sets whether glyph runs are drawn with FreeType's LCD_V rendering for vertical subpixel orders, which
    /// DirectWrite has no ClearType for; else (or if FreeType can't open the font) DirectWrite draws them turned a quarter,
    /// unhinted. Framework thread, before glyphs are made again.
    /// </summary>
    public bool VerticalWithFreeType { get; set; }

    /// <summary>
    /// Rasterizes a run of glyphs of one face (a glyph, or a shaped cluster: a ligature, a base with its marks) with the
    /// pen at <paramref name="originX"/>, a fraction of a pixel, so the cluster lands where shaping put it.
    /// </summary>
    public RasterGlyph RasterizeRun(
        IDWriteFontFace5* face,
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
        var subpixel = this.UsesSubpixel(parameters);
        if (subpixel && this.SubpixelOrder.IsVertical && this.VerticalWithFreeType &&
            this.RasterizeWithFreeType(face, px, glyphs, advances, offsets, count, originX, advance, parameters, transform) is { } drawn)
            return drawn;

        // ClearType's stripes are horizontal: for vertical ones, the glyphs are drawn turned a quarter and turned back.
        var turned = subpixel && this.SubpixelOrder.IsVertical;
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
            subpixel ? DWRITE_TEXT_ANTIALIAS_MODE.DWRITE_TEXT_ANTIALIAS_MODE_CLEARTYPE : DWRITE_TEXT_ANTIALIAS_MODE.DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE,
            turned);
        RECT bounds;
        if (subpixel)
        {
            analysis.Value->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds).ThrowOnError();
            var cw = bounds.right - bounds.left;
            var ch = bounds.bottom - bounds.top;
            if (cw > 0 && ch > 0)
            {
                var lcd = new byte[cw * ch * 3];
                fixed (byte* p = lcd)
                {
                    analysis.Value->CreateAlphaTexture(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds, p, (uint)lcd.Length)
                        .ThrowOnError();
                }

                if (turned)
                    (lcd, bounds, cw, ch) = (TurnBack(lcd, cw, ch, 3), TurnBack(bounds), ch, cw);

                var mean = new byte[cw * ch];
                var blueFirst = this.SubpixelOrder.IsBlueFirst;
                for (var i = 0; i < mean.Length; i++)
                {
                    mean[i] = (byte)((lcd[i * 3] + lcd[(i * 3) + 1] + lcd[(i * 3) + 2] + 1) / 3);
                    if (blueFirst)
                        (lcd[i * 3], lcd[(i * 3) + 2]) = (lcd[(i * 3) + 2], lcd[i * 3]);
                }

                return new(advance, bounds.left, bounds.top, cw, ch, mean, lcd);
            }

            // A font may still render aliased (its recommended mode): the 1x1 texture has it then.
        }

        // With grayscale antialiasing, the 1x1 texture type gives 8-bit coverage.
        analysis.Value->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_ALIASED_1x1, &bounds).ThrowOnError();
        var w = bounds.right - bounds.left;
        var h = bounds.bottom - bounds.top;
        var alpha = w > 0 && h > 0 ? new byte[w * h] : [];
        if (alpha.Length != 0)
        {
            fixed (byte* p = alpha)
            {
                analysis.Value->CreateAlphaTexture(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_ALIASED_1x1, &bounds, p, (uint)alpha.Length)
                    .ThrowOnError();
            }

            if (turned)
                (alpha, bounds, w, h) = (TurnBack(alpha, w, h, 1), TurnBack(bounds), h, w);
        }
        else
        {
            w = h = 0;
        }

        return new(advance, bounds.left, bounds.top, w, h, alpha);
    }

    /// <summary>Gets the pixels a run of glyphs covers with the pen at the origin, as <see cref="RasterizeRun"/> draws it.</summary>
    public RECT GetRunBounds(
        IDWriteFontFace5* face,
        float px,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        DirectWriteParams parameters,
        Matrix2x2 transform)
    {
        var subpixel = this.UsesSubpixel(parameters);
        var vertical = subpixel && this.SubpixelOrder.IsVertical;
        if (vertical && this.VerticalWithFreeType &&
            this.RasterizeWithFreeType(face, px, glyphs, advances, offsets, count, 0, 0, parameters, transform) is { } g)
            return new() { left = g.Left, top = g.Top, right = g.Left + g.Width, bottom = g.Top + g.Height };

        // The texture RasterizeRun draws: ClearType's (antialiased horizontally), unless the font renders aliased.
        using var analysis = this.CreateAnalysis(
            face,
            px,
            glyphs,
            advances,
            offsets,
            count,
            0,
            parameters,
            transform,
            subpixel ? DWRITE_TEXT_ANTIALIAS_MODE.DWRITE_TEXT_ANTIALIAS_MODE_CLEARTYPE : DWRITE_TEXT_ANTIALIAS_MODE.DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE,
            vertical);
        RECT bounds = default;
        if (subpixel)
            analysis.Value->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds).ThrowOnError();
        if (bounds.right <= bounds.left || bounds.bottom <= bounds.top)
            analysis.Value->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_ALIASED_1x1, &bounds).ThrowOnError();
        return vertical ? TurnBack(bounds) : bounds;
    }

    /// <summary>Gets whether glyph runs drawn with the parameters get subpixel coverage: aliased ones have none.</summary>
    private bool UsesSubpixel(DirectWriteParams parameters) =>
        this.SubpixelOrder != SubpixelOrder.None &&
        (DWRITE_RENDERING_MODE1)parameters.RenderMode != DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_ALIASED;

    /// <summary>
    /// Rasterizes a glyph run as <see cref="RasterizeRun"/> with FreeType: the face's file, its simulations made as
    /// FreeType makes them, its axis values, and hinted as the parameters' rendering and grid fitting modes are; null if
    /// FreeType can't open it, or it has only bitmaps.
    /// </summary>
    private RasterGlyph? RasterizeWithFreeType(
        IDWriteFontFace5* face,
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
        var ftFace = freeType.Open(face);
        if (ftFace is null || !FreeTypeGlyphRasterizer.IsScalable(ftFace))
            return null;

        // DirectWrite's oblique simulation slants as synthesis does, before the transformation; its bold simulation makes
        // glyphs 1/50 em wider and taller.
        var simulations = face->GetSimulations();
        if ((simulations & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE) != 0)
            transform *= new Matrix2x2(1, -LookupFont.SynthesizedFace.ObliqueSlope, 0, 1);
        var embolden = (simulations & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_BOLD) != 0 ? 1 / 50f : 0;

        // GDI's modes hint both ways, the natural ones vertically only (FT_LOAD_TARGET_NORMAL, _LIGHT); with the font's
        // own hinting, as DirectWrite does: FreeType's light mode auto-hints TrueType fonts otherwise, which rounds heights
        // differently (Comic Sans gains a row above and below).
        const int noHinting = 1 << 1;
        const int noAutohint = 1 << 15;
        var renderMode = (DWRITE_RENDERING_MODE1)parameters.RenderMode is DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_GDI_CLASSIC
                         or DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_GDI_NATURAL
                             ? (int)FT_Render_Mode_.FT_RENDER_MODE_NORMAL
                             : (int)FT_Render_Mode_.FT_RENDER_MODE_LIGHT;
        var loadFlags = (DWRITE_GRID_FIT_MODE)parameters.GridFitMode == DWRITE_GRID_FIT_MODE.DWRITE_GRID_FIT_MODE_DISABLED ? noHinting : noAutohint;

        return freeType.RasterizeRun(
            ftFace,
            px,
            glyphs,
            advances,
            offsets,
            count,
            originX,
            advance,
            new(loadFlags, renderMode),
            transform,
            GetAxisValues(face),
            embolden);
    }

    /// <summary>Gets a variable font face's axis values (as DWRITE_FONT_AXIS_TAG); null if it isn't variable.</summary>
    private static Dictionary<uint, float>? GetAxisValues(IDWriteFontFace5* face)
    {
        if (!face->HasVariations())
            return null;

        var values = new DWRITE_FONT_AXIS_VALUE[face->GetFontAxisValueCount()];
        fixed (DWRITE_FONT_AXIS_VALUE* p = values)
        {
            if (face->GetFontAxisValues(p, (uint)values.Length).FAILED)
                return null;
        }

        var res = new Dictionary<uint, float>(values.Length);
        foreach (var v in values)
            res[(uint)v.axisTag] = v.value;
        return res;
    }

    /// <summary>
    /// Turns the pixels of a run drawn turned a quarter (<see cref="CreateAnalysis"/>'s <c>turned</c>), of
    /// <paramref name="width"/> by <paramref name="height"/> with <paramref name="channels"/> bytes each, back: its rows
    /// become columns, the last on the left. A pixel's channels stay in order, now top to bottom.
    /// </summary>
    private static byte[] TurnBack(byte[] pixels, int width, int height, int channels)
    {
        var res = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var from = ((y * width) + x) * channels;
                var to = ((x * height) + (height - 1 - y)) * channels;
                pixels.AsSpan(from, channels).CopyTo(res.AsSpan(to));
            }
        }

        return res;
    }

    /// <summary>Gets the box of a run drawn turned a quarter, turned back: (u, v) drawn is (y, -x) on screen.</summary>
    private static RECT TurnBack(RECT turned) =>
        new() { left = -turned.bottom, top = turned.left, right = -turned.top, bottom = turned.right };

    /// <summary>
    /// Makes DirectWrite's analysis of a glyph run, which the caller releases. <paramref name="turned"/> draws it turned a
    /// quarter, its screen's (x, y) at (y, -x), so ClearType's horizontal stripes are vertical when turned back
    /// (<see cref="TurnBack(byte[], int, int, int)"/>): antialiased both ways and unhinted, as both are then needed across
    /// the text.
    /// </summary>
    private MyComPtr<IDWriteGlyphRunAnalysis> CreateAnalysis(
        IDWriteFontFace5* face,
        float px,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        DirectWriteParams parameters,
        Matrix2x2 transform,
        DWRITE_TEXT_ANTIALIAS_MODE antialiasMode,
        bool turned)
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
        if (turned)
            matrix = new() { m11 = matrix.m12, m12 = -matrix.m11, m21 = matrix.m22, m22 = -matrix.m21, dx = matrix.dy, dy = -matrix.dx };

        var run = new DWRITE_GLYPH_RUN
        {
            fontFace = (IDWriteFontFace*)face,
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

        var analysis = default(MyComPtr<IDWriteGlyphRunAnalysis>);
        var gridFitMode = (DWRITE_GRID_FIT_MODE)parameters.GridFitMode;

        // Turned, the other modes leave the text's horizontal unantialiased, and hint across it.
        if (turned)
            (renderingMode, gridFitMode) = (DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_NATURAL_SYMMETRIC,
                                               DWRITE_GRID_FIT_MODE.DWRITE_GRID_FIT_MODE_DISABLED);

        for (var attempt = 0;; attempt++)
        {
            var hr = factory->CreateGlyphRunAnalysis(
                &run,
                &matrix,
                renderingMode,
                measuringMode,
                gridFitMode,
                antialiasMode,
                0,
                0,
                analysis.ReleaseAndGetAddressOf());
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
}
