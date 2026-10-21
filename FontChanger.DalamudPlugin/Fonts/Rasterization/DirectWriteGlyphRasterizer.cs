using System;
using System.Collections.Generic;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Faces.Sources.Outline;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using FreeTypeSharp;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Fonts.Rasterization;

internal sealed unsafe class DirectWriteGlyphRasterizer(IDWriteFactory7* factory, FreeTypeGlyphRasterizer freeType)
{
    /// <summary>Set before glyphs are made again.</summary>
    public SubpixelOrder SubpixelOrder { get; set => field = Misc.DebugAssertMainThread(value); }

    /// <summary>Set before glyphs are made again.</summary>
    /// <remarks>DirectWrite has no ClearType for vertical stripes, so without FreeType it draws them turned a quarter.</remarks>
    public bool VerticalWithFreeType { get; set => field = Misc.DebugAssertMainThread(value); }

    /// <summary>The pen is at <paramref name="originX"/>, a fraction of a pixel, so the cluster lands where shaping put it.</summary>
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
        var subpixel = this.UsesSubpixel(parameters);
        var turned = subpixel && this.SubpixelOrder.IsVertical;
        if (turned && this.VerticalWithFreeType &&
            this.RasterizeWithFreeType(face, px, glyphs, advances, offsets, count, originX, advance, parameters, transform) is { } drawn)
            return drawn;

        using var analysis = this.CreateAnalysis(face, px, glyphs, advances, offsets, count, originX, parameters, transform, subpixel, turned);
        var (type, bounds) = GetTexture(analysis.Value, subpixel);
        var w = bounds.right - bounds.left;
        var h = bounds.bottom - bounds.top;
        if (w <= 0 || h <= 0)
            return new(advance, bounds.left, bounds.top, 0, 0, []);

        var channels = type == DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1 ? 3 : 1;
        var pixels = new byte[w * h * channels];
        fixed (byte* p = pixels)
            analysis.Value->CreateAlphaTexture(type, &bounds, p, (uint)pixels.Length).ThrowOnError();
        if (turned)
            (pixels, bounds, w, h) = (TurnBack(pixels, w, h, channels), TurnBack(bounds), h, w);

        return channels == 1
                   ? new(advance, bounds.left, bounds.top, w, h, pixels)
                   : new(advance, bounds.left, bounds.top, w, h, FreeTypeGlyphRasterizer.TakeSubpixelMean(pixels, this.SubpixelOrder.IsBlueFirst), pixels);
    }

    /// <summary>Must match what <see cref="RasterizeRun"/> draws with the pen at 0.</summary>
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
        var turned = subpixel && this.SubpixelOrder.IsVertical;
        if (turned && this.VerticalWithFreeType &&
            this.RasterizeWithFreeType(face, px, glyphs, advances, offsets, count, 0, 0, parameters, transform) is { } g)
            return new() { left = g.Left, top = g.Top, right = g.Left + g.Width, bottom = g.Top + g.Height };

        using var analysis = this.CreateAnalysis(face, px, glyphs, advances, offsets, count, 0, parameters, transform, subpixel, turned);
        var (_, bounds) = GetTexture(analysis.Value, subpixel);
        return turned ? TurnBack(bounds) : bounds;
    }

    /// <summary>
    /// A font may still render aliased (its recommended mode), leaving the ClearType texture empty; the 1x1 texture holds
    /// 8-bit coverage when antialiased.
    /// </summary>
    private static (DWRITE_TEXTURE_TYPE Type, RECT Bounds) GetTexture(IDWriteGlyphRunAnalysis* analysis, bool subpixel)
    {
        RECT bounds = default;
        if (subpixel)
        {
            analysis->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, &bounds).ThrowOnError();
            if (bounds.right > bounds.left && bounds.bottom > bounds.top)
                return (DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_CLEARTYPE_3x1, bounds);
        }

        analysis->GetAlphaTextureBounds(DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_ALIASED_1x1, &bounds).ThrowOnError();
        return (DWRITE_TEXTURE_TYPE.DWRITE_TEXTURE_ALIASED_1x1, bounds);
    }

    /// <summary>Returns null if FreeType can't open the face, or it has only bitmaps.</summary>
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

        // DirectWrite's oblique simulation slants before the transformation; its bold simulation adds 1/50 em both ways.
        var simulations = face->GetSimulations();
        if ((simulations & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE) != 0)
            transform *= new Matrix2x2(1, -LookupFont.ObliqueSlope, 0, 1);
        var embolden = (simulations & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_BOLD) != 0 ? 1 / 50f : 0;

        // GDI's modes hint both ways and the natural ones vertically only, always with the font's own hinting as
        // DirectWrite does, since FreeType's light mode would auto-hint TrueType fonts and round heights differently.
        var renderMode = (DWRITE_RENDERING_MODE1)parameters.RenderMode is DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_GDI_CLASSIC
                         or DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_GDI_NATURAL
                             ? (int)FT_Render_Mode_.FT_RENDER_MODE_NORMAL
                             : (int)FT_Render_Mode_.FT_RENDER_MODE_LIGHT;
        var loadFlags = (DWRITE_GRID_FIT_MODE)parameters.GridFitMode == DWRITE_GRID_FIT_MODE.DWRITE_GRID_FIT_MODE_DISABLED
                            ? (int)FT_LOAD.FT_LOAD_NO_HINTING
                            : (int)FT_LOAD.FT_LOAD_NO_AUTOHINT;

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

    private bool UsesSubpixel(DirectWriteParams parameters) =>
        this.SubpixelOrder != SubpixelOrder.None &&
        (DWRITE_RENDERING_MODE1)parameters.RenderMode != DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_ALIASED;

    /// <summary>Returns null if the face isn't variable.</summary>
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

    // (u, v) drawn turned is (y, -x) on screen.
    private static RECT TurnBack(RECT turned) =>
        new() { left = -turned.bottom, top = turned.left, right = -turned.top, bottom = turned.right };

    /// <summary><paramref name="turned"/> draws the run turned a quarter, so that ClearType's horizontal stripes are vertical once turned back.</summary>
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
        bool subpixel,
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

        // Glyph run analysis doesn't rasterize outlines.
        if (renderingMode is DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_OUTLINE or DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_DEFAULT)
            renderingMode = DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_NATURAL_SYMMETRIC;

        var analysis = default(MyComPtr<IDWriteGlyphRunAnalysis>);
        var gridFitMode = (DWRITE_GRID_FIT_MODE)parameters.GridFitMode;

        // Turned, the other modes would leave the text's horizontal unantialiased, and hint across it.
        if (turned)
            (renderingMode, gridFitMode) = (DWRITE_RENDERING_MODE1.DWRITE_RENDERING_MODE1_NATURAL_SYMMETRIC,
                                               DWRITE_GRID_FIT_MODE.DWRITE_GRID_FIT_MODE_DISABLED);

        var antialiasMode = subpixel ? DWRITE_TEXT_ANTIALIAS_MODE.DWRITE_TEXT_ANTIALIAS_MODE_CLEARTYPE : DWRITE_TEXT_ANTIALIAS_MODE.DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE;
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
