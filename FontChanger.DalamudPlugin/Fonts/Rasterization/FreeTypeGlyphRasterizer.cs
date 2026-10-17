using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Records;
using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using FreeTypeSharp;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using FacePointer = FFXIVClientStructs.Interop.Pointer<FreeTypeSharp.FT_FaceRec_>;

namespace FontChanger.DalamudPlugin.Fonts.Rasterization;

/// <summary>
/// Also draws fonts with only bitmaps, which DirectWrite doesn't; faces are opened from DirectWrite's files so that its
/// shaping's glyph indices are FreeType's.
/// </summary>
internal sealed unsafe class FreeTypeGlyphRasterizer : IDisposable
{
    // FT_LOAD_TARGET_(mode): the hinting the render mode is for.
    private const int LoadTargetShift = 16;

    // FT_LCD_FILTER_DEFAULT.
    private const int LcdFilterDefault = 1;

    private readonly Dictionary<(string Path, uint Index), FacePointer> _faces = [];
    private FT_LibraryRec_* _library;

    private readonly Dictionary<FacePointer, float> _faceSizes = [];

    // 16.16 fixed point.
    private readonly Dictionary<FacePointer, (uint Tag, nint Min, nint Default, nint Max)[]> _faceAxes = [];
    private readonly Dictionary<FacePointer, nint[]> _faceCoordinates = [];

    public FreeTypeGlyphRasterizer()
    {
        // FreeTypeSharp looks for freetype.dll in the game's directory: loading the plugin's copy first makes the system
        // give it that one by name.
        var directory = Plugin.PluginInterface.AssemblyLocation.DirectoryName!;
        NativeLibrary.Load(Path.Combine(directory, "freetype.dll"));

        FT_LibraryRec_* library;
        Check(FT.FT_Init_FreeType(&library));
        this._library = library;

        // Only a library built for ClearType-style rendering has a filter against color fringes; the bundled Harmony one
        // ignores this.
        FT_Library_SetLcdFilter(library, LcdFilterDefault);
    }

    public void Dispose()
    {
        foreach (var face in this._faces.Values)
        {
            if (!face.IsNull)
                FT.FT_Done_Face(face);
        }

        this._faces.Clear();
        if (this._library is not null)
        {
            FT.FT_Done_FreeType(this._library);
            this._library = null;
        }
    }

    public SubpixelOrder SubpixelOrder { get; set; }

    /// <summary>Also reorders <paramref name="lcd"/> in place from R, G, B to the stripes' order.</summary>
    internal static byte[] TakeSubpixelMean(byte[] lcd, bool blueFirst)
    {
        var mean = new byte[lcd.Length / 3];
        for (var i = 0; i < mean.Length; i++)
        {
            mean[i] = (byte)((lcd[i * 3] + lcd[(i * 3) + 1] + lcd[(i * 3) + 2] + 1) / 3);
            if (blueFirst)
                (lcd[i * 3], lcd[(i * 3) + 2]) = (lcd[(i * 3) + 2], lcd[i * 3]);
        }

        return mean;
    }

    /// <summary>The face is kept open and shared; null if it isn't a local file FreeType can open.</summary>
    public FT_FaceRec_* Open(IDWriteFontFace5* fontFace)
    {
        var (path, index) = GetFile(fontFace);
        if (path is null)
            return null;
        if (this._faces.TryGetValue((path, index), out var existing))
            return existing;

        FT_FaceRec_* face = null;
        var bytes = Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* p = bytes)
        {
            if (FT.FT_New_Face(this._library, p, (nint)index, &face) != FT_Error.FT_Err_Ok)
                face = null;
        }

        this._faces.Add((path, index), face);
        return face;
    }

    public static bool IsScalable(FT_FaceRec_* face) => (face->face_flags & (nint)FT_FACE_FLAG.FT_FACE_FLAG_SCALABLE) != 0;

    /// <summary>
    /// <paramref name="axes"/> is keyed by DWRITE_FONT_AXIS_TAG, missing axes at their defaults, and
    /// <paramref name="embolden"/> is in ems, applied before the transformation.
    /// </summary>
    public RasterGlyph RasterizeRun(
        FT_FaceRec_* face,
        float px,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        FreeTypeParams parameters,
        Matrix2x2 transform,
        IReadOnlyDictionary<uint, float>? axes,
        float embolden)
    {
        this.SetAxes(face, axes);
        var scale = this.SetSize(face, px);
        var strength = (nint)Misc.StdRound(embolden * px * 64);

        // Emboldening needs outlines.
        var loadFlags = (FT_LOAD)(parameters.LoadFlags | ((parameters.RenderMode & 15) << LoadTargetShift) |
                                  (strength != 0 ? (int)FT_LOAD.FT_LOAD_NO_BITMAP : 0));

        // Vertical stripes need per-subpixel coverage, hinted as the parameters' mode is.
        var renderMode = (FT_Render_Mode_)parameters.RenderMode;
        if (this.SubpixelOrder.IsVertical && renderMode is not (FT_Render_Mode_.FT_RENDER_MODE_MONO or FT_Render_Mode_.FT_RENDER_MODE_SDF))
            renderMode = FT_Render_Mode_.FT_RENDER_MODE_LCD_V;

        // FreeType transforms column vectors with y growing upwards (16.16 fixed point).
        var matrix = new FT_Matrix_
        {
            xx = (nint)Misc.StdRound(transform.M11 * 65536),
            xy = (nint)Misc.StdRound(-transform.M12 * 65536),
            yx = (nint)Misc.StdRound(-transform.M21 * 65536),
            yy = (nint)Misc.StdRound(transform.M22 * 65536),
        };

        var pieces = new List<RasterGlyph>((int)count);
        var x = 0f;
        for (var i = 0; i < count; i++)
        {
            // The glyph's origin on screen (y down).
            var lx = x + (offsets is null ? 0 : offsets[i].advanceOffset);
            var ly = offsets is null ? 0 : -offsets[i].ascenderOffset;
            var gx = originX + (transform.M11 * lx) + (transform.M12 * ly);
            var gy = -((transform.M21 * lx) + (transform.M22 * ly));
            x += advances[i];

            if (FT.FT_Load_Glyph(face, glyphs[i], loadFlags) != FT_Error.FT_Err_Ok)
                continue;
            var slot = face->glyph;
            var bitmapGlyph = slot->format == FT_Glyph_Format_.FT_GLYPH_FORMAT_BITMAP;
            if (!bitmapGlyph)
            {
                // Bitmaps of a bitmap face can't be transformed, so they are placed (and scaled) afterwards instead.
                if (strength != 0)
                    FT.FT_Outline_EmboldenXY(&slot->outline, strength, strength);
                FT.FT_Outline_Transform(&slot->outline, &matrix);
                FT.FT_Outline_Translate(&slot->outline, (nint)Misc.StdRound((gx - MathF.Floor(gx)) * 64), (nint)Misc.StdRound(gy * 64));
                if (FT.FT_Render_Glyph(slot, renderMode) != FT_Error.FT_Err_Ok)
                    continue;
            }

            var piece = this.ReadBitmap(slot);
            if (piece.Width == 0)
                continue;

            // Bitmaps ignore the transform, and unscaled ones can only move by whole pixels.
            var fraction = gx - MathF.Floor(gx);
            if (scale != 1)
                piece = piece.Scaled(scale, fraction, -gy);
            else if (bitmapGlyph)
                piece = piece with { Left = piece.Left + (int)Misc.StdRound(fraction), Top = piece.Top - (int)Misc.StdRound(gy) };
            pieces.Add(piece with { Left = piece.Left + (int)MathF.Floor(gx) });
        }

        return RasterGlyph.Merge(pieces, advance);
    }

    /// <summary>
    /// Points are in pixels with y growing downwards, and <paramref name="tags"/> and <paramref name="contourEnds"/> are
    /// as in FT_Outline, filled with nonzero winding.
    /// </summary>
    public byte[] FillOutline(ReadOnlySpan<(float X, float Y)> points, ReadOnlySpan<byte> tags, ReadOnlySpan<short> contourEnds, int width, int height)
    {
        var coverage = new byte[width * height];
        if (points.IsEmpty || width <= 0 || height <= 0)
            return coverage;

        var ftPoints = new FT_Vector_[points.Length];
        for (var i = 0; i < points.Length; i++)
            ftPoints[i] = new() { x = (nint)Misc.StdRound(points[i].X * 64), y = (nint)Misc.StdRound((height - points[i].Y) * 64) };
        var ftTags = tags.ToArray();
        var ends = contourEnds.ToArray();
        fixed (FT_Vector_* p = ftPoints)
        fixed (byte* t = ftTags)
        fixed (short* c = ends)
        fixed (byte* buffer = coverage)
        {
            var outline = new FT_Outline_
            {
                n_points = (short)ftPoints.Length,
                n_contours = (short)ends.Length,
                points = p,
                tags = t,
                contours = c,
            };
            var bitmap = new FT_Bitmap_
            {
                rows = (uint)height,
                width = (uint)width,
                pitch = width,
                buffer = buffer,
                num_grays = 256,
                pixel_mode = FT_Pixel_Mode_.FT_PIXEL_MODE_GRAY,
            };
            Check(FT.FT_Outline_Get_Bitmap(this._library, &outline, &bitmap));
        }

        return coverage;
    }

    [DllImport("freetype", CallingConvention = CallingConvention.Cdecl)]
    private static extern FT_Error FT_Get_MM_Var(FT_FaceRec_* face, MmVar** master);

    [DllImport("freetype", CallingConvention = CallingConvention.Cdecl)]
    private static extern FT_Error FT_Done_MM_Var(FT_LibraryRec_* library, MmVar* master);

    [DllImport("freetype", CallingConvention = CallingConvention.Cdecl)]
    private static extern FT_Error FT_Library_SetLcdFilter(FT_LibraryRec_* library, int filter);

    [DllImport("freetype", CallingConvention = CallingConvention.Cdecl)]
    private static extern FT_Error FT_Set_Var_Design_Coordinates(FT_FaceRec_* face, uint count, nint* coordinates);

    private void SetAxes(FT_FaceRec_* face, IReadOnlyDictionary<uint, float>? values)
    {
        if ((face->face_flags & (nint)FT_FACE_FLAG.FT_FACE_FLAG_MULTIPLE_MASTERS) == 0)
            return;

        if (!this._faceAxes.TryGetValue(face, out var axes))
        {
            axes = [];
            MmVar* mm;
            if (FT_Get_MM_Var(face, &mm) == FT_Error.FT_Err_Ok)
            {
                axes = new (uint, nint, nint, nint)[mm->NumAxis];
                for (var i = 0; i < axes.Length; i++)
                {
                    // FreeType's tags have the first letter highest; DirectWrite's lowest.
                    var a = mm->Axis[i];
                    axes[i] = (BinaryPrimitives.ReverseEndianness((uint)a.Tag), a.Minimum, a.Default, a.Maximum);
                }

                FT_Done_MM_Var(this._library, mm);
            }

            this._faceAxes.Add(face, axes);
        }

        if (axes.Length == 0)
            return;

        var coordinates = new nint[axes.Length];
        for (var i = 0; i < axes.Length; i++)
        {
            var (tag, min, def, max) = axes[i];
            coordinates[i] = values is not null && values.TryGetValue(tag, out var v) ? Math.Clamp((nint)Misc.StdRound(v * 65536), min, max) : def;
        }

        if (this._faceCoordinates.TryGetValue(face, out var current) && current.AsSpan().SequenceEqual(coordinates))
            return;
        fixed (nint* c = coordinates)
            Check(FT_Set_Var_Design_Coordinates(face, (uint)coordinates.Length, c));
        this._faceCoordinates[face] = coordinates;
    }

    private static void Check(FT_Error error)
    {
        if (error != FT_Error.FT_Err_Ok)
            throw new InvalidOperationException($"FreeType failed: {error}");
    }

    private static (string? Path, uint Index) GetFile(IDWriteFontFace5* fontFace)
    {
        using var file = FontFileKey.Of(fontFace);
        using var local = default(MyComPtr<IDWriteLocalFontFileLoader>);
        var iid = IID.IID_IDWriteLocalFontFileLoader;
        if (file.Loader is null || file.Loader->QueryInterface(&iid, (void**)local.GetAddressOf()).FAILED)
            return (null, 0);

        fixed (byte* key = file.Key)
        {
            uint length;
            local.Value->GetFilePathLengthFromKey(key, (uint)file.Key.Length, &length).ThrowOnError();
            var path = new char[length + 1];
            fixed (char* p = path)
                local.Value->GetFilePathFromKey(key, (uint)file.Key.Length, p, length + 1).ThrowOnError();
            return (new(path, 0, (int)length), fontFace->GetIndex());
        }
    }

    /// <summary>
    /// Returns the scale from the bitmap size chosen (the smallest not below <paramref name="px"/>, else the largest) to
    /// <paramref name="px"/>; 1 for an outline face.
    /// </summary>
    private float SetSize(FT_FaceRec_* face, float px)
    {
        if (IsScalable(face))
        {
            if (!this._faceSizes.TryGetValue(face, out var current) || current != px)
            {
                Check(FT.FT_Set_Char_Size(face, 0, (nint)Misc.StdRound(px * 64), 72, 72));
                this._faceSizes[face] = px;
            }

            return 1;
        }

        var best = -1;
        var bestPx = 0f;
        for (var i = 0; i < face->num_fixed_sizes; i++)
        {
            var strike = face->available_sizes[i].y_ppem / 64f;
            if (best < 0 || (bestPx < px ? strike > bestPx : strike >= px && strike < bestPx))
                (best, bestPx) = (i, strike);
        }

        if (best < 0)
            throw new InvalidOperationException("The font has neither outlines nor bitmaps.");
        Check(FT.FT_Select_Size(face, best));
        return px / bestPx;
    }

    private RasterGlyph ReadBitmap(FT_GlyphSlotRec_* slot)
    {
        var bitmap = &slot->bitmap;
        int w = (int)bitmap->width, h = (int)bitmap->rows;
        if (bitmap->pixel_mode == FT_Pixel_Mode_.FT_PIXEL_MODE_LCD)
            w /= 3;
        else if (bitmap->pixel_mode == FT_Pixel_Mode_.FT_PIXEL_MODE_LCD_V)
            h /= 3;
        if (w == 0 || h == 0)
            return new(0, 0, 0, 0, 0, []);

        if (bitmap->pixel_mode is FT_Pixel_Mode_.FT_PIXEL_MODE_LCD or FT_Pixel_Mode_.FT_PIXEL_MODE_LCD_V)
        {
            // Subpixels follow each other in a row (LCD) or in three rows (LCD_V).
            var vertical = bitmap->pixel_mode == FT_Pixel_Mode_.FT_PIXEL_MODE_LCD_V;
            var lcd = new byte[w * h * 3];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    for (var c = 0; c < 3; c++)
                    {
                        lcd[(((y * w) + x) * 3) + c] = vertical
                                                           ? bitmap->buffer[(((y * 3) + c) * bitmap->pitch) + x]
                                                           : bitmap->buffer[(y * bitmap->pitch) + (x * 3) + c];
                    }
                }
            }

            return new(0, slot->bitmap_left, -slot->bitmap_top, w, h, TakeSubpixelMean(lcd, this.SubpixelOrder.IsBlueFirst), lcd);
        }

        var alpha = new byte[w * h];
        if (bitmap->pixel_mode == FT_Pixel_Mode_.FT_PIXEL_MODE_BGRA)
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                    alpha[(y * w) + x] = bitmap->buffer[(y * bitmap->pitch) + (x * 4) + 3];
            }
        }
        else
        {
            FT_Bitmap_ converted;
            FT.FT_Bitmap_Init(&converted);
            Check(FT.FT_Bitmap_Convert(this._library, bitmap, &converted, 1));
            var max = Math.Max(1, converted.num_grays - 1);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                    alpha[(y * w) + x] = (byte)(converted.buffer[(y * converted.pitch) + x] * 255 / max);
            }

            FT.FT_Bitmap_Done(this._library, &converted);
        }

        return new(0, slot->bitmap_left, -slot->bitmap_top, w, h, alpha);
    }

    /// <summary>FT_MM_Var; FreeType's longs are 64-bit in the bundled library, as FreeTypeSharp declares them.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MmVar
    {
        public uint NumAxis;
        public uint NumDesigns;
        public uint NumNamedStyles;
        public VarAxis* Axis;
        public void* NamedStyle;
    }

    /// <summary>FT_Var_Axis.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VarAxis
    {
        public byte* Name;
        public nint Minimum;
        public nint Default;
        public nint Maximum;
        public nuint Tag;
        public uint StrId;
    }
}
