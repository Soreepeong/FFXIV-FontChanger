using System;

using FontChanger.DalamudPlugin.Rendering.Enums;
using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.DalamudPlugin.Rendering.Text;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DirectX;

namespace FontChanger.DalamudPlugin.Rendering;

/// <summary>
/// What fonts are found, shaped, and drawn with: DirectWrite (its factory, the system's fonts and analysis), and the
/// rasterizers of DirectWrite, FreeType, and Direct2D, which share its subpixel settings.
/// </summary>
internal sealed unsafe class FontServices : IDisposable
{
    private MyComPtr<IDWriteFactory7> _factory;
    private MyComPtr<IDWriteFontCollection3> _systemFonts;
    private MyComPtr<IDWriteTextAnalyzer> _analyzer;

    public FontServices()
    {
        try
        {
            var iid = IID.IID_IDWriteFactory7;
            fixed (IDWriteFactory7** factory = this._factory)
                DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &iid, (IUnknown**)factory).ThrowOnError();
            fixed (IDWriteTextAnalyzer** analyzer = this._analyzer)
                this._factory.Value->CreateTextAnalyzer(analyzer).ThrowOnError();

            // Weight, stretch and style families, as the newer overloads (which take a family model) don't make by default.
            using var systemFonts = default(MyComPtr<IDWriteFontCollection>);
            this._factory.Value->GetSystemFontCollection(systemFonts.GetAddressOf(), BOOL.FALSE).ThrowOnError();
            systemFonts.CopyTo(ref this._systemFonts).ThrowOnError();

            this.TextAnalyzer = new(this);
            this.FreeType = new();
            this.DirectWrite = new(this._factory.Value, this.FreeType);
            this.Images = new();
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    public ref IDWriteFactory7 Factory => ref *this._factory.Value;

    /// <summary>Gets DirectWrite's text analyzer, which finds scripts and shapes text (owned by this object).</summary>
    public ref IDWriteTextAnalyzer Analyzer => ref *this._analyzer.Value;

    /// <summary>Gets the cutting of lines into runs of a script.</summary>
    public DirectWriteTextAnalyzer TextAnalyzer { get; }

    /// <summary>Gets the system's font collection (owned by this object).</summary>
    public IDWriteFontCollection3* SystemCollection => this._systemFonts.Value;

    /// <summary>Gets DirectWrite's rasterization of glyph runs.</summary>
    public DirectWriteGlyphRasterizer DirectWrite { get; }

    /// <summary>Gets FreeType's rasterization.</summary>
    public FreeTypeGlyphRasterizer FreeType { get; }

    /// <summary>Gets Direct2D's drawing of SVG documents and bitmaps.</summary>
    public ImageRenderer Images { get; }

    /// <summary>
    /// Gets or sets whether glyphs are drawn with ClearType coverage too, in which order, by DirectWrite and FreeType.
    /// Framework thread, before glyphs are made again.
    /// </summary>
    public SubpixelOrder SubpixelOrder
    {
        get;
        set
        {
            field = value;
            this.DirectWrite.SubpixelOrder = value;
            this.FreeType.SubpixelOrder = value;
        }
    }

    /// <summary>
    /// Finds the font of a family closest to a weight, stretch, and style (simulated if the family lacks it); false if the
    /// family isn't installed. The caller releases it.
    /// </summary>
    public bool TryFindFont(LookupStruct lookup, out MyComPtr<IDWriteFont3> match)
    {
        match = default;
        uint index;
        BOOL exists;
        fixed (char* name = lookup.Name)
            this._systemFonts.Value->FindFamilyName(name, &index, &exists).ThrowOnError();
        if (!exists)
            return false;

        using var fontFamily = default(MyComPtr<IDWriteFontFamily2>);
        this._systemFonts.Value->GetFontFamily(index, fontFamily.GetAddressOf()).ThrowOnError();
        var font = default(MyComPtr<IDWriteFont3>);
        fontFamily.Value->GetFirstMatchingFont(
            (DWRITE_FONT_WEIGHT)lookup.Weight,
            (DWRITE_FONT_STRETCH)lookup.Stretch,
            (DWRITE_FONT_STYLE)lookup.Style,
            font.GetAddressOf()).ThrowOnError();
        match = font;
        return true;
    }

    public void Dispose()
    {
        ForceNullableDispose(this.Images);
        ForceNullableDispose(this.FreeType);
        this._analyzer.Dispose();
        this._systemFonts.Dispose();
        this._factory.Dispose();
        return;

        static void ForceNullableDispose(IDisposable? d) => d?.Dispose();
    }
}
