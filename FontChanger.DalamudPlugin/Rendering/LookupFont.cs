using System;
using System.Collections.Generic;

using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering;

/// <summary>
/// How a requested face is made from the closest real face of its family (FontChanger's Structs.h SynthesizedFace): the
/// simulations DirectWrite applies (null to keep the matched font's, as without explicit synthesis), the weight FreeType
/// adds by emboldening, whether FreeType slants, the horizontal scale that gives the requested width, and the axis values
/// of a variable font that give the requested properties.
/// </summary>
internal sealed record SynthesizedFace(
    DWRITE_FONT_SIMULATIONS? Simulations,
    int WeightDelta,
    bool Oblique,
    float ScaleX,
    IReadOnlyDictionary<uint, float> AxisValues)
{
    /// <summary>The slope of DirectWrite's oblique simulation (about 18.77 degrees), which FreeType slants by too.</summary>
    public const float ObliqueSlope = 0.33985f;

    public static readonly SynthesizedFace None = new(null, 0, false, 1, new Dictionary<uint, float>());

    /// <summary>Gets the transformation on screen, before the element's: the slant, then the scale.</summary>
    /// <remarks>x' = scaleX (x - slope y), y growing downwards: points above the baseline move right.</remarks>
    public Matrix2x2 Transform => new(this.ScaleX, this.Oblique ? -ObliqueSlope * this.ScaleX : 0, 0, 1);

    /// <summary>
    /// Gets how much FreeType emboldens, in ems: DirectWrite's bold simulation makes glyphs 1/50 em wider and taller; going
    /// from 400 to 700 is taken to be that, and other weights in proportion.
    /// </summary>
    public float Embolden => this.WeightDelta / 300f / 50f;

    /// <summary>
    /// Works out how the face a lookup asks for is made from a real face (Structs.cpp ResolveSynthesis): a variable font's
    /// axes where it has them (<paramref name="ranges"/>; those the lookup sets are left to it), then simulations,
    /// emboldening, slant and width. Without explicit synthesis, nothing is made.
    /// </summary>
    public static unsafe SynthesizedFace Resolve(LookupStruct lookup, Renderer renderer, IDWriteFont* font, ReadOnlySpan<DWRITE_FONT_AXIS_RANGE> ranges)
    {
        if (lookup.AllowSynthesis is not { } allow)
            return None;

        var axisValues = new Dictionary<uint, float>();
        var realWeight = (int)font->GetWeight();
        var realStyle = (int)font->GetStyle();
        var realStretchPercent = GetStretchPercent((int)font->GetStretch());
        foreach (var range in ranges)
        {
            var tag = (uint)range.axisTag;
            var userSet = lookup.Variations.ContainsKey(tag);
            switch (range.axisTag)
            {
                case DWRITE_FONT_AXIS_TAG.DWRITE_FONT_AXIS_TAG_WEIGHT:
                    if (userSet)
                    {
                        realWeight = lookup.Weight;
                    }
                    else
                    {
                        var value = Math.Clamp(lookup.Weight, range.minValue, range.maxValue);
                        axisValues[tag] = value;
                        realWeight = (int)Rounding.Round(value);
                    }

                    break;

                case DWRITE_FONT_AXIS_TAG.DWRITE_FONT_AXIS_TAG_WIDTH:
                    if (userSet)
                    {
                        realStretchPercent = GetStretchPercent(lookup.Stretch);
                    }
                    else if (GetStretchPercent(lookup.Stretch) is var requested and > 0)
                    {
                        var value = Math.Clamp(requested, range.minValue, range.maxValue);
                        axisValues[tag] = value;
                        realStretchPercent = value;
                    }

                    break;

                case DWRITE_FONT_AXIS_TAG.DWRITE_FONT_AXIS_TAG_ITALIC:
                    if (userSet)
                    {
                        realStyle = lookup.Style;
                    }
                    else if (lookup.Style != 0 && range.maxValue >= 1)
                    {
                        axisValues[tag] = 1;
                        realStyle = 2;
                    }

                    break;

                case DWRITE_FONT_AXIS_TAG.DWRITE_FONT_AXIS_TAG_SLANT:
                    // Negative values lean right: as far as the oblique simulation, if the font goes that far.
                    if (userSet)
                    {
                        realStyle = lookup.Style;
                    }
                    else if (lookup.Style != 0 && range.minValue < 0 && realStyle == 0)
                    {
                        axisValues[tag] = Math.Max(range.minValue, -MathF.Atan(ObliqueSlope) * 180 / MathF.PI);
                        realStyle = 1;
                    }

                    break;
            }
        }

        var simulations = DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE;
        var weightDelta = 0;
        var oblique = false;
        var scaleX = 1f;
        if (allow)
        {
            // DirectWrite only makes a bold face, and only from one that isn't bold already.
            if (renderer == Renderer.FreeType)
                weightDelta = lookup.Weight - realWeight;
            else if (lookup.Weight >= 600 && realWeight <= 500)
                simulations |= DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_BOLD;

            if (lookup.Style != 0 && realStyle == 0)
            {
                if (renderer == Renderer.FreeType)
                    oblique = true;
                else
                    simulations |= DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE;
            }

            if (GetStretchPercent(lookup.Stretch) is var requested and > 0 && realStretchPercent > 0 && requested != realStretchPercent)
                scaleX = requested / realStretchPercent;
        }

        return new(simulations, weightDelta, oblique, scaleX, axisValues);
    }

    /// <summary>Gets the width of a stretch as a percentage of the normal width (usWidthClass); 0 if undefined.</summary>
    private static float GetStretchPercent(int stretch) => stretch switch
    {
        1 => 50,
        2 => 62.5f,
        3 => 75,
        4 => 87.5f,
        5 => 100,
        6 => 112.5f,
        7 => 125,
        8 => 150,
        9 => 200,
        _ => 0,
    };
}

/// <summary>
/// The font a lookup resolves to, as FontChanger makes it (Structs.cpp ResolveFont, ResolveSynthesis): the face DirectWrite
/// matches, its simulations, and the axis values of a variable font. With explicit synthesis, the real face is taken
/// instead, and what the lookup asks for that it lacks is made (<see cref="SynthesizedFace"/>).
/// </summary>
internal sealed unsafe class LookupFont : IDisposable
{
    private readonly DWRITE_FONT_SIMULATIONS _simulations;

    // The axis values, without the optical size when it follows the text size; and the faces made with them.
    private readonly DWRITE_FONT_AXIS_VALUE[] _axes = [];
    private readonly Dictionary<float, ComPtr<IDWriteFontFace5>> _sizedFaces = [];
    private readonly DWRITE_FONT_AXIS_RANGE _opticalSizeRange;

    private ComPtr<IDWriteFontResource> _resource;
    private ComPtr<IDWriteFont> _font;
    private ComPtr<IDWriteFontFace> _face;

    /// <summary>Takes a reference of its own to the font.</summary>
    private LookupFont(IDWriteFactory2* factory, IDWriteFont* font, LookupStruct lookup, Renderer renderer)
    {
        this._font = new(font);
        try
        {
            font->CreateFontFace(this._face.GetAddressOf()).ThrowOnError();
            var face = this._face.Get();
            DWRITE_FONT_METRICS metrics;
            face->GetMetrics(&metrics);
            this.Metrics = metrics;

            // A variable font starts from its instance (DirectWrite lists the named ones as fonts), less the optical size.
            var ranges = Array.Empty<DWRITE_FONT_AXIS_RANGE>();
            using var face5 = default(ComPtr<IDWriteFontFace5>);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19043)
                && this._face.CopyTo(face5.GetAddressOf()).SUCCEEDED
                && face5.Get()->HasVariations())
            {
                face5.Get()->GetFontResource(this._resource.GetAddressOf()).ThrowOnError();
                var r = this._resource.Get();
                ranges = new DWRITE_FONT_AXIS_RANGE[r->GetFontAxisCount()];
                fixed (DWRITE_FONT_AXIS_RANGE* p = ranges)
                    r->GetFontAxisRanges(p, (uint)ranges.Length).ThrowOnError();
                this._axes = new DWRITE_FONT_AXIS_VALUE[face5.Get()->GetFontAxisValueCount()];
                fixed (DWRITE_FONT_AXIS_VALUE* p = this._axes)
                    face5.Get()->GetFontAxisValues(p, (uint)this._axes.Length).ThrowOnError();
            }

            var axisValues = new Dictionary<uint, float>();
            foreach (var a in this._axes)
            {
                if (a.axisTag != DWRITE_FONT_AXIS_TAG.DWRITE_FONT_AXIS_TAG_OPTICAL_SIZE)
                    axisValues[(uint)a.axisTag] = a.value;
            }

            this.Synthesis = SynthesizedFace.Resolve(lookup, renderer, font, ranges);
            this._simulations = this.Synthesis.Simulations ?? font->GetSimulations();
            foreach (var (tag, value) in this.Synthesis.AxisValues)
                axisValues[tag] = value;
            foreach (var (tag, value) in lookup.Variations)
                axisValues[tag] = value;
            this.AxisValues = axisValues;

            // Layouts match this font again by the properties that make DirectWrite apply the same simulations.
            var bold = (this._simulations & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_BOLD) != 0;
            var oblique = (this._simulations & DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE) != 0;
            (this.LayoutWeight, this.LayoutStretch, this.LayoutStyle) = this.Synthesis.Simulations is null
                                                                            ? (lookup.Weight, lookup.Stretch, lookup.Style)
                                                                            : (bold ? Math.Max((int)font->GetWeight(), 700) : (int)font->GetWeight(),
                                                                                  (int)font->GetStretch(), oblique ? 1 : (int)font->GetStyle());

            if (this.IsVariable)
            {
                // Unless set, the optical size follows the text size.
                var list = new List<DWRITE_FONT_AXIS_VALUE>();
                foreach (var range in ranges)
                {
                    if (axisValues.TryGetValue((uint)range.axisTag, out var v))
                        list.Add(new() { axisTag = range.axisTag, value = Math.Clamp(v, range.minValue, range.maxValue) });
                    else if (range.axisTag == DWRITE_FONT_AXIS_TAG.DWRITE_FONT_AXIS_TAG_OPTICAL_SIZE)
                        this.AutoOpticalSize = true;
                }

                this._axes = list.ToArray();
                this._opticalSizeRange = Array.Find(ranges, r => r.axisTag == DWRITE_FONT_AXIS_TAG.DWRITE_FONT_AXIS_TAG_OPTICAL_SIZE);
            }
            else if (this._simulations != face->GetSimulations())
            {
                // The face of the font comes with the font's simulations; one with others is made from the file.
                var simulated = WithSimulations(factory, face, this._simulations);
                this._face.Dispose();
                this._face = simulated;
            }
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    public IDWriteFont* Font => this._font.Get();

    /// <summary>
    /// Gets the face: with the simulations, but for a variable font, the matched instance (for its file and metrics;
    /// glyphs are drawn from <see cref="GetFace"/>'s).
    /// </summary>
    public IDWriteFontFace* Face => this._face.Get();

    public DWRITE_FONT_METRICS Metrics { get; }

    /// <summary>Gets what synthesis makes of the face.</summary>
    public SynthesizedFace Synthesis { get; }

    /// <summary>Gets the transformation on screen that synthesis makes, before the element's: a slant (FreeType), a width.</summary>
    public Matrix2x2 Transform => this.Synthesis.Transform;

    /// <summary>Gets how much FreeType emboldens glyphs, in ems (negative values thin them); they advance that much further.</summary>
    public float Embolden => this.Synthesis.Embolden;

    /// <summary>Gets a variable font's axis values (as DWRITE_FONT_AXIS_TAG), but the optical size if it follows the text size.</summary>
    public IReadOnlyDictionary<uint, float> AxisValues { get; }

    /// <summary>Gets the properties text layouts ask for, which match this font with these simulations.</summary>
    public int LayoutWeight { get; }

    public int LayoutStretch { get; }

    public int LayoutStyle { get; }

    /// <summary>Gets the axis values text layouts set, in the font's order (none if it isn't variable).</summary>
    public ReadOnlySpan<DWRITE_FONT_AXIS_VALUE> LayoutAxes => this._axes;

    /// <summary>Gets whether the optical size follows the text size.</summary>
    public bool AutoOpticalSize { get; }

    /// <summary>Gets whether the font is variable (DirectWrite has them from Windows 10 21H1).</summary>
    private bool IsVariable => !this._resource.IsNull;

    /// <summary>Finds the font a lookup asks for and makes it as the element draws it; null if the family isn't installed.</summary>
    public static LookupFont? Create(GlyphRasterizer rasterizer, LookupStruct lookup, Renderer renderer)
    {
        if (!rasterizer.TryFindFont(lookup, out var font))
            return null;
        try
        {
            // DirectWrite may match a simulated font: with explicit synthesis, the real face it is made from is taken.
            if (lookup.AllowSynthesis is not null && font.Get()->GetSimulations() != DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE)
            {
                var real = GetRealFont(rasterizer.Factory, font.Get());
                font.Dispose();
                font = real;
            }

            return new(rasterizer.Factory, font.Get(), lookup, renderer);
        }
        finally
        {
            font.Dispose();
        }
    }

    /// <summary>Gets the face at a text size: the optical size set to it, if it follows. Owned by this object.</summary>
    public IDWriteFontFace* GetFace(float px)
    {
        if (!this.IsVariable)
            return this.Face;

        var key = this.AutoOpticalSize ? Rounding.Round(px * 4) / 4 : 0;
        if (this._sizedFaces.TryGetValue(key, out var existing))
            return (IDWriteFontFace*)existing.Get();

        var values = new DWRITE_FONT_AXIS_VALUE[this._axes.Length + (this.AutoOpticalSize ? 1 : 0)];
        this._axes.CopyTo(values, 0);
        if (this.AutoOpticalSize)
        {
            var range = this._opticalSizeRange;
            values[^1] = new() { axisTag = DWRITE_FONT_AXIS_TAG.DWRITE_FONT_AXIS_TAG_OPTICAL_SIZE, value = Math.Clamp(key, range.minValue, range.maxValue) };
        }

        var face = default(ComPtr<IDWriteFontFace5>);
        fixed (DWRITE_FONT_AXIS_VALUE* p = values)
            this._resource.Get()->CreateFontFace(this._simulations, p, (uint)values.Length, face.GetAddressOf()).ThrowOnError();
        this._sizedFaces.Add(key, face);
        return (IDWriteFontFace*)face.Get();
    }

    /// <summary>Gets the axis values FreeType sets at a text size (as DWRITE_FONT_AXIS_TAG).</summary>
    public IReadOnlyDictionary<uint, float> GetAxisValues(float px)
    {
        if (!this.AutoOpticalSize)
            return this.AxisValues;
        return new Dictionary<uint, float>(this.AxisValues) { [(uint)DWRITE_FONT_AXIS_TAG.DWRITE_FONT_AXIS_TAG_OPTICAL_SIZE] = px };
    }

    public void Dispose()
    {
        foreach (var face in this._sizedFaces.Values)
            face.Get()->Release();
        this._sizedFaces.Clear();
        this._resource.Dispose();
        this._face.Dispose();
        this._font.Dispose();
    }

    /// <summary>Makes a face of the same file and index as a face, with other simulations.</summary>
    public static ComPtr<IDWriteFontFace> WithSimulations(IDWriteFactory2* factory, IDWriteFontFace* face, DWRITE_FONT_SIMULATIONS simulations)
    {
        uint count = 1;
        using var file = default(ComPtr<IDWriteFontFile>);
        face->GetFiles(&count, file.GetAddressOf()).ThrowOnError();
        var made = default(ComPtr<IDWriteFontFace>);
        factory->CreateFontFace(face->GetType(), 1, file.GetAddressOf(), face->GetIndex(), simulations, made.GetAddressOf()).ThrowOnError();
        return made;
    }

    /// <summary>Gets the font without simulations a simulated font is made from.</summary>
    private static ComPtr<IDWriteFont> GetRealFont(IDWriteFactory2* factory, IDWriteFont* font)
    {
        using var face = default(ComPtr<IDWriteFontFace>);
        font->CreateFontFace(face.GetAddressOf()).ThrowOnError();
        using var realFace = WithSimulations(factory, face.Get(), DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE);
        using var family = default(ComPtr<IDWriteFontFamily>);
        font->GetFontFamily(family.GetAddressOf()).ThrowOnError();
        using var collection = default(ComPtr<IDWriteFontCollection>);
        family.Get()->GetFontCollection(collection.GetAddressOf()).ThrowOnError();
        var real = default(ComPtr<IDWriteFont>);
        collection.Get()->GetFontFromFontFace(realFace.Get(), real.GetAddressOf()).ThrowOnError();
        return real;
    }
}
