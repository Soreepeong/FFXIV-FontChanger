using System;
using System.Diagnostics.CodeAnalysis;

using FontChanger.DalamudPlugin.Fonts.Enums;
using FontChanger.DalamudPlugin.Fonts.Rasterization;
using FontChanger.DalamudPlugin.Fonts.Text;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using static TerraFX.Interop.DirectX.DirectX;

namespace FontChanger.DalamudPlugin.Fonts;

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

    public ref IDWriteTextAnalyzer Analyzer => ref *this._analyzer.Value;

    public ref IDWriteFontCollection3 SystemCollection => ref *this._systemFonts.Value;

    public DirectWriteTextAnalyzer TextAnalyzer { get; }

    public DirectWriteGlyphRasterizer DirectWrite { get; }

    public FreeTypeGlyphRasterizer FreeType { get; }

    public ImageRenderer Images { get; }

    public SubpixelOrder SubpixelOrder
    {
        get;
        set
        {
            field = Misc.DebugAssertMainThread(value);
            this.DirectWrite.SubpixelOrder = value;
            this.FreeType.SubpixelOrder = value;
        }
    }

    /// <summary>The closest match, simulated if the family lacks it.</summary>
    public bool TryFindFont(LookupStruct lookup, out MyComPtr<IDWriteFont3> match)
    {
        match = default;
        uint index;
        fixed (char* name = lookup.FamilyName)
        {
            BOOL exists;
            if (this._systemFonts.Value->FindFamilyName(name, &index, &exists).FAILED || !exists)
                return false;
        }

        using var fontFamily = default(MyComPtr<IDWriteFontFamily2>);
        if (this._systemFonts.Value->GetFontFamily(index, fontFamily.GetAddressOf()).FAILED)
            return false;
        var font = default(MyComPtr<IDWriteFont3>);
        if (fontFamily.Value->GetFirstMatchingFont(
                (DWRITE_FONT_WEIGHT)lookup.Weight,
                (DWRITE_FONT_STRETCH)lookup.Stretch,
                (DWRITE_FONT_STYLE)lookup.Style,
                font.GetAddressOf()).FAILED)
        {
            font.Dispose();
            return false;
        }

        match = font;
        return true;
    }

    public bool TryOpenProbe(LookupStruct lookup, [NotNullWhen(true)] out FontProbe? probe)
    {
        probe = null;
        if (!this.TryFindFont(lookup, out var match))
            return false;

        using var font = match;
        var face = default(MyComPtr<IDWriteFontFace5>);
        if (font.Value->CreateFontFace(face.GetAddressOf()).FAILED)
            return false;

        probe = new(face);
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
