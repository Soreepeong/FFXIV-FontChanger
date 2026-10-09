using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using FreeTypeSharp;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace CustomFonts;

/// <summary>
/// Rasterizes glyphs with FreeType: for elements a preset draws with FreeType, and for fonts with only bitmaps (which
/// DirectWrite doesn't draw), whose nearest bitmap size is scaled to the size asked for. Fonts are opened from the files
/// of DirectWrite's font faces, so glyph indices from DirectWrite's shaping are FreeType's.
/// </summary>
internal sealed unsafe class FreeTypeFonts : IDisposable
{
    // FT_LOAD_TARGET_(mode): the hinting the render mode is for.
    private const int LoadTargetShift = 16;

    private const nint FaceFlagMultipleMasters = 1 << 8;

    private readonly Dictionary<(string Path, uint Index), nint> faces = [];
    private FT_LibraryRec_* library;

    // The size each face was last set to; a bitmap face's selected strike scale.
    private readonly Dictionary<nint, float> faceSizes = [];

    // A variable face's axes (tag, minimum, default, maximum; 16.16), and the design coordinates it was last set to.
    private readonly Dictionary<nint, (uint Tag, nint Min, nint Default, nint Max)[]> faceAxes = [];
    private readonly Dictionary<nint, nint[]> faceCoordinates = [];

    private FreeTypeFonts(FT_LibraryRec_* library) => this.library = library;

    /// <summary>Starts FreeType; null if its native library can't be loaded.</summary>
    public static FreeTypeFonts? Create()
    {
        try
        {
            // FreeTypeSharp looks for freetype.dll from the application's directory (the game's): the plugin's copy is
            // loaded first, which the system then gives it by name.
            var directory = Host.Current.AssemblyDirectory;
            NativeLibrary.TryLoad(Path.Combine(directory, "freetype.dll"), out _);

            FT_LibraryRec_* library;
            Check(FT.FT_Init_FreeType(&library));
            return new(library);
        }
        catch (Exception ex)
        {
            Host.Log.Warning(ex, "FreeType can't be used; its elements are drawn with DirectWrite");
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var face in this.faces.Values)
        {
            if (face != 0)
                FT.FT_Done_Face((FT_FaceRec_*)face);
        }

        this.faces.Clear();
        if (this.library is not null)
        {
            FT.FT_Done_FreeType(this.library);
            this.library = null;
        }
    }

    /// <summary>Opens the font file of a DirectWrite font face (kept open, shared); 0 if it isn't a local file.</summary>
    public nint Open(IDWriteFontFace* fontFace)
    {
        var (path, index) = GetFile(fontFace);
        if (path is null)
            return 0;
        if (this.faces.TryGetValue((path, index), out var existing))
            return existing;

        FT_FaceRec_* face = null;
        var bytes = Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* p = bytes)
        {
            if (FT.FT_New_Face(this.library, p, (nint)index, &face) != FT_Error.FT_Err_Ok)
                face = null;
        }

        this.faces.Add((path, index), (nint)face);
        return (nint)face;
    }

    /// <summary>Gets whether a face has outlines; one with only bitmaps is drawn by scaling its nearest bitmap size.</summary>
    public static bool IsScalable(nint face) => (((FT_FaceRec_*)face)->face_flags & (nint)FT_FACE_FLAG.FT_FACE_FLAG_SCALABLE) != 0;

    /// <summary>
    /// Rasterizes a run of glyphs (a glyph, or a shaped cluster) with the pen at <paramref name="originX"/>, each glyph at
    /// its advance and offset, as <see cref="GlyphRasterizer.RasterizeRun"/> does with DirectWrite. A variable face is set
    /// to <paramref name="axes"/> (as DWRITE_FONT_AXIS_TAG; others at their defaults), and outlines are emboldened by
    /// <paramref name="embolden"/> ems before they are transformed.
    /// </summary>
    public RasterGlyph RasterizeRun(
        nint facePtr,
        float px,
        ushort* glyphs,
        float* advances,
        DWRITE_GLYPH_OFFSET* offsets,
        uint count,
        float originX,
        int advance,
        FreeTypeParams parameters,
        GlyphTransform transform,
        IReadOnlyDictionary<uint, float>? axes = null,
        float embolden = 0)
    {
        var face = (FT_FaceRec_*)facePtr;
        this.SetAxes(face, axes);
        var scale = this.SetSize(face, px);
        var strength = (nint)Rounding.Round(embolden * px * 64);

        // Emboldening needs outlines.
        var loadFlags = (FT_LOAD)(parameters.LoadFlags | ((parameters.RenderMode & 15) << LoadTargetShift) | (strength != 0 ? 0x8 : 0));

        // FreeType transforms column vectors with y growing upwards (16.16 fixed point).
        var matrix = new FT_Matrix_
        {
            xx = (nint)Rounding.Round(transform.M11 * 65536),
            xy = (nint)Rounding.Round(-transform.M12 * 65536),
            yx = (nint)Rounding.Round(-transform.M21 * 65536),
            yy = (nint)Rounding.Round(transform.M22 * 65536),
        };

        // The glyphs' coverage, placed relative to the pen, merged into one box.
        var pieces = new List<RasterGlyph>((int)count);
        var x = 0f;
        for (var i = 0; i < count; i++)
        {
            // The glyph's origin on screen (y down): its place in the run, transformed, after the pen's origin.
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
                // Outlines are emboldened, transformed, and moved by the fraction; bitmaps (of a bitmap face, scaled) are
                // placed afterwards.
                if (strength != 0)
                    FT.FT_Outline_EmboldenXY(&slot->outline, strength, strength);
                FT.FT_Outline_Transform(&slot->outline, &matrix);
                FT.FT_Outline_Translate(&slot->outline, (nint)Rounding.Round((gx - MathF.Floor(gx)) * 64), (nint)Rounding.Round(gy * 64));
                if (FT.FT_Render_Glyph(slot, (FT_Render_Mode_)parameters.RenderMode) != FT_Error.FT_Err_Ok)
                    continue;
            }

            var piece = this.ReadBitmap(slot);
            if (piece.Width == 0)
                continue;

            // Bitmaps ignore the transform: scaled with the fraction, or at their own size moved by whole pixels.
            var fraction = gx - MathF.Floor(gx);
            if (scale != 1)
                piece = piece.Scaled(scale, fraction, -gy);
            else if (bitmapGlyph)
                piece = piece with { Left = piece.Left + (int)Rounding.Round(fraction), Top = piece.Top - (int)Rounding.Round(gy) };
            pieces.Add(piece with { Left = piece.Left + (int)MathF.Floor(gx) });
        }

        return RasterGlyph.Merge(pieces, advance);
    }

    /// <summary>
    /// Fills an outline as FreeType does a glyph's (nonzero winding), into coverage of <paramref name="width"/> by
    /// <paramref name="height"/> pixels. Points are in pixels with y growing downwards, tags are FreeType's
    /// (FT_CURVE_TAG_ON, _CONIC, _CUBIC), and each contour ends at its index in <paramref name="contourEnds"/>.
    /// </summary>
    public byte[] FillOutline(ReadOnlySpan<(float X, float Y)> points, ReadOnlySpan<byte> tags, ReadOnlySpan<short> contourEnds, int width, int height)
    {
        var coverage = new byte[width * height];
        if (points.IsEmpty || width <= 0 || height <= 0)
            return coverage;

        var ftPoints = new FT_Vector_[points.Length];
        for (var i = 0; i < points.Length; i++)
            ftPoints[i] = new() { x = (nint)Rounding.Round(points[i].X * 64), y = (nint)Rounding.Round((height - points[i].Y) * 64) };
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
            Check(FT.FT_Outline_Get_Bitmap(this.library, &outline, &bitmap));
        }

        return coverage;
    }

    [DllImport("freetype", CallingConvention = CallingConvention.Cdecl)]
    private static extern FT_Error FT_Get_MM_Var(FT_FaceRec_* face, MMVar** master);

    [DllImport("freetype", CallingConvention = CallingConvention.Cdecl)]
    private static extern FT_Error FT_Done_MM_Var(FT_LibraryRec_* library, MMVar* master);

    [DllImport("freetype", CallingConvention = CallingConvention.Cdecl)]
    private static extern FT_Error FT_Set_Var_Design_Coordinates(FT_FaceRec_* face, uint count, nint* coordinates);

    /// <summary>Sets a variable face's design coordinates: the given axis values, the others at their defaults.</summary>
    private void SetAxes(FT_FaceRec_* face, IReadOnlyDictionary<uint, float>? values)
    {
        if ((face->face_flags & FaceFlagMultipleMasters) == 0)
            return;

        if (!this.faceAxes.TryGetValue((nint)face, out var axes))
        {
            axes = [];
            MMVar* mm;
            if (FT_Get_MM_Var(face, &mm) == FT_Error.FT_Err_Ok)
            {
                axes = new (uint, nint, nint, nint)[mm->NumAxis];
                for (var i = 0; i < axes.Length; i++)
                {
                    // FreeType's tags have the first letter highest; DirectWrite's lowest.
                    var a = mm->Axis[i];
                    axes[i] = (System.Buffers.Binary.BinaryPrimitives.ReverseEndianness((uint)a.Tag), a.Minimum, a.Default, a.Maximum);
                }

                FT_Done_MM_Var(this.library, mm);
            }

            this.faceAxes.Add((nint)face, axes);
        }

        if (axes.Length == 0)
            return;

        var coordinates = new nint[axes.Length];
        for (var i = 0; i < axes.Length; i++)
        {
            var (tag, min, def, max) = axes[i];
            coordinates[i] = values is not null && values.TryGetValue(tag, out var v) ? Math.Clamp((nint)Rounding.Round(v * 65536), min, max) : def;
        }

        if (this.faceCoordinates.TryGetValue((nint)face, out var current) && current.AsSpan().SequenceEqual(coordinates))
            return;
        fixed (nint* c = coordinates)
            Check(FT_Set_Var_Design_Coordinates(face, (uint)coordinates.Length, c));
        this.faceCoordinates[(nint)face] = coordinates;
    }

    private static void Check(FT_Error error)
    {
        if (error != FT_Error.FT_Err_Ok)
            throw new InvalidOperationException($"FreeType failed: {error}");
    }

    /// <summary>Gets the file and face index of a DirectWrite font face, if it is a local file.</summary>
    private static (string? Path, uint Index) GetFile(IDWriteFontFace* fontFace)
    {
        using var file = FontFileKey.Of(fontFace);
        IDWriteLocalFontFileLoader* local;
        var iid = IID.IID_IDWriteLocalFontFileLoader;
        if (file.Loader is null || ((IUnknown*)file.Loader)->QueryInterface(&iid, (void**)&local).FAILED)
            return (null, 0);
        try
        {
            fixed (byte* key = file.Key)
            {
                uint length;
                local->GetFilePathLengthFromKey(key, (uint)file.Key.Length, &length).ThrowOnError();
                var path = new char[length + 1];
                fixed (char* p = path)
                    local->GetFilePathFromKey(key, (uint)file.Key.Length, p, length + 1).ThrowOnError();
                return (new string(path, 0, (int)length), fontFace->GetIndex());
            }
        }
        finally
        {
            local->Release();
        }
    }

    /// <summary>
    /// Sets a face to a size: an outline face to it exactly (1 returned), a bitmap face to its nearest bitmap size, the
    /// smallest not below it or else the largest, returning the scale from that size to the one asked for.
    /// </summary>
    private float SetSize(FT_FaceRec_* face, float px)
    {
        if (IsScalable((nint)face))
        {
            if (!this.faceSizes.TryGetValue((nint)face, out var current) || current != px)
            {
                Check(FT.FT_Set_Char_Size(face, 0, (nint)Rounding.Round(px * 64), 72, 72));
                this.faceSizes[(nint)face] = px;
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

    /// <summary>Reads a glyph slot's bitmap as 8-bit coverage (converting 1, 2 and 4-bit, and taking BGRA's alpha).</summary>
    private RasterGlyph ReadBitmap(FT_GlyphSlotRec_* slot)
    {
        var bitmap = &slot->bitmap;
        int w = (int)bitmap->width, h = (int)bitmap->rows;
        if (w == 0 || h == 0)
            return new(0, 0, 0, 0, 0, []);

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
            Check(FT.FT_Bitmap_Convert(this.library, bitmap, &converted, 1));
            var max = Math.Max(1, converted.num_grays - 1);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                    alpha[(y * w) + x] = (byte)(converted.buffer[(y * converted.pitch) + x] * 255 / max);
            }

            FT.FT_Bitmap_Done(this.library, &converted);
        }

        return new(0, slot->bitmap_left, -slot->bitmap_top, w, h, alpha);
    }

    /// <summary>FT_MM_Var; FreeType's longs are 64-bit in the bundled library, as FreeTypeSharp declares them.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MMVar
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
