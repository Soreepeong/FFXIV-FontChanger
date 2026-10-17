using System;
using System.Collections.Generic;

using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Fonts.Faces.Sources.Outline;

/// <summary>
/// As FontChanger's ElementFonts.cpp ResolveFont and ResolveSynthesis, explicit synthesis starts from the real face and
/// synthesizes what it lacks.
/// </summary>
internal sealed unsafe class LookupFont : IDisposable
{
    /// <summary>The slope of DirectWrite's oblique simulation (about 18.77 degrees), which FreeType slants by too.</summary>
    public const float ObliqueSlope = 0.33985f;

    private readonly DWRITE_FONT_SIMULATIONS _simulations;

    // Without the optical size when it follows the text size, which GetFace adds per size.
    private readonly DWRITE_FONT_AXIS_VALUE[] _axes = [];
    private readonly Dictionary<float, MyComPtr<IDWriteFontFace5>> _sizedFaces = [];
    private readonly DWRITE_FONT_AXIS_RANGE _opticalSizeRange;
    private readonly bool _autoOpticalSize;

    // As DWRITE_FONT_AXIS_TAG, without the optical size when it follows the text size.
    private readonly IReadOnlyDictionary<uint, float> _axisValues;

    private MyComPtr<IDWriteFontResource> _resource;
    private MyComPtr<IDWriteFont3> _font;
    private MyComPtr<IDWriteFontFace5> _face;

    /// <summary>Takes a reference of its own to the font.</summary>
    private LookupFont(IDWriteFont3* font, LookupStruct lookup, ElementSource source)
    {
        this._font = new(font, true);
        try
        {
            fixed (IDWriteFontFace5** created = this._face)
                font->CreateFontFace(created).ThrowOnError();
            var face = this._face.Value;
            DWRITE_FONT_METRICS metrics;
            face->GetMetrics(&metrics);
            this.Metrics = metrics;

            // A variable font starts from its instance (DirectWrite lists the named ones as fonts), less the optical size.
            var ranges = Array.Empty<DWRITE_FONT_AXIS_RANGE>();
            var instanceAxes = Array.Empty<DWRITE_FONT_AXIS_VALUE>();
            if (face->HasVariations())
            {
                fixed (IDWriteFontResource** resource = this._resource)
                    face->GetFontResource(resource).ThrowOnError();
                var r = this._resource.Value;
                ranges = new DWRITE_FONT_AXIS_RANGE[r->GetFontAxisCount()];
                fixed (DWRITE_FONT_AXIS_RANGE* p = ranges)
                    r->GetFontAxisRanges(p, (uint)ranges.Length).ThrowOnError();
                instanceAxes = new DWRITE_FONT_AXIS_VALUE[face->GetFontAxisValueCount()];
                fixed (DWRITE_FONT_AXIS_VALUE* p = instanceAxes)
                    face->GetFontAxisValues(p, (uint)instanceAxes.Length).ThrowOnError();
            }

            var axisValues = new Dictionary<uint, float>();
            foreach (var a in instanceAxes)
            {
                if (a.axisTag != DWRITE_FONT_AXIS_TAG.DWRITE_FONT_AXIS_TAG_OPTICAL_SIZE)
                    axisValues[(uint)a.axisTag] = a.value;
            }

            var (simulations, weightDelta, oblique, scaleX, synthesizedAxes) = Synthesize(lookup, source, font, ranges);
            this._simulations = simulations ?? font->GetSimulations();
            foreach (var (tag, value) in synthesizedAxes)
                axisValues[tag] = value;
            foreach (var (tag, value) in lookup.Variations)
                axisValues[tag] = value;
            this._axisValues = axisValues;

            // x' = scaleX (x - slope y), y growing downwards: points above the baseline move right.
            this.Transform = new(scaleX, oblique ? -ObliqueSlope * scaleX : 0, 0, 1);

            // DirectWrite's bold simulation grows glyphs by 1/50 em, which is taken as the step from weight 400 to 700.
            this.Embolden = weightDelta / 300f / 50f;

            if (this.IsVariable)
            {
                // Unless set, the optical size follows the text size.
                var list = new List<DWRITE_FONT_AXIS_VALUE>();
                foreach (var range in ranges)
                {
                    if (axisValues.TryGetValue((uint)range.axisTag, out var v))
                        list.Add(new() { axisTag = range.axisTag, value = Math.Clamp(v, range.minValue, range.maxValue) });
                    else if (range.axisTag == DWRITE_FONT_AXIS_TAG.DWRITE_FONT_AXIS_TAG_OPTICAL_SIZE)
                        this._autoOpticalSize = true;
                }

                this._axes = list.ToArray();
                this._opticalSizeRange = Array.Find(ranges, r => r.axisTag == DWRITE_FONT_AXIS_TAG.DWRITE_FONT_AXIS_TAG_OPTICAL_SIZE);
            }
            else if (this._simulations != face->GetSimulations())
            {
                // The face of the font comes with the font's simulations; one with others is made from its resource.
                var simulated = default(MyComPtr<IDWriteFontFace5>);
                WithSimulations(face, this._simulations, simulated.GetAddressOf()).ThrowOnError();
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

    /// <summary>For a variable font, the matched instance, used for its file and metrics; glyphs are drawn from <see cref="GetFace"/>'s.</summary>
    public IDWriteFontFace5* Face => this._face.Value;

    public DWRITE_FONT_METRICS Metrics { get; }

    /// <summary>Synthesis's slant (FreeType) and width on screen, applied before the element's transformation.</summary>
    public Matrix2x2 Transform { get; }

    /// <summary>In ems (negative values thin glyphs); glyphs advance that much further.</summary>
    public float Embolden { get; }

    // DirectWrite has variable fonts from Windows 10 21H1.
    private bool IsVariable => this._resource;

    public static LookupFont? Create(FontServices services, LookupStruct lookup, ElementSource source)
    {
        if (!services.TryFindFont(lookup, out var font))
            return null;
        try
        {
            // DirectWrite may match a simulated font: with explicit synthesis, the real face it is made from is taken.
            if (lookup.AllowSynthesis is not null && font.Value->GetSimulations() != DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE)
            {
                var real = GetRealFont(services, font.Value);
                font.Dispose();
                font = real;
            }

            return new(font.Value, lookup, source);
        }
        finally
        {
            font.Dispose();
        }
    }

    /// <summary>Owned by this object.</summary>
    public IDWriteFontFace5* GetFace(float px)
    {
        if (!this.IsVariable)
            return this.Face;

        var key = this._autoOpticalSize ? Misc.StdRound(px * 4) / 4 : 0;
        if (this._sizedFaces.TryGetValue(key, out var existing))
            return existing.Value;

        var values = new DWRITE_FONT_AXIS_VALUE[this._axes.Length + (this._autoOpticalSize ? 1 : 0)];
        this._axes.CopyTo(values, 0);
        if (this._autoOpticalSize)
        {
            var range = this._opticalSizeRange;
            values[^1] = new() { axisTag = DWRITE_FONT_AXIS_TAG.DWRITE_FONT_AXIS_TAG_OPTICAL_SIZE, value = Math.Clamp(key, range.minValue, range.maxValue) };
        }

        var face = default(MyComPtr<IDWriteFontFace5>);
        fixed (DWRITE_FONT_AXIS_VALUE* p = values)
            this._resource.Value->CreateFontFace(this._simulations, p, (uint)values.Length, face.GetAddressOf()).ThrowOnError();
        this._sizedFaces.Add(key, face);
        return face.Value;
    }

    /// <summary>For FreeType, keyed by DWRITE_FONT_AXIS_TAG.</summary>
    public IReadOnlyDictionary<uint, float> GetAxisValues(float px)
    {
        if (!this._autoOpticalSize)
            return this._axisValues;
        return new Dictionary<uint, float>(this._axisValues) { [(uint)DWRITE_FONT_AXIS_TAG.DWRITE_FONT_AXIS_TAG_OPTICAL_SIZE] = px };
    }

    public void Dispose()
    {
        foreach (var face in this._sizedFaces.Values)
            face.Value->Release();
        this._sizedFaces.Clear();
        this._resource.Dispose();
        this._face.Dispose();
        this._font.Dispose();
    }

    /// <summary>As ElementFonts.cpp ResolveSynthesis; null simulations keep the matched font's.</summary>
    private static (DWRITE_FONT_SIMULATIONS? Simulations, int WeightDelta, bool Oblique, float ScaleX, Dictionary<uint, float> AxisValues) Synthesize(
        LookupStruct lookup,
        ElementSource source,
        IDWriteFont3* font,
        ReadOnlySpan<DWRITE_FONT_AXIS_RANGE> ranges)
    {
        var axisValues = new Dictionary<uint, float>();
        if (lookup.AllowSynthesis is not { } allow)
            return (null, 0, false, 1, axisValues);

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
                        realWeight = (int)Misc.StdRound(value);
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
            if (source == ElementSource.FreeType)
                weightDelta = lookup.Weight - realWeight;
            else if (lookup.Weight >= 600 && realWeight <= 500)
                simulations |= DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_BOLD;

            if (lookup.Style != 0 && realStyle == 0)
            {
                if (source == ElementSource.FreeType)
                    oblique = true;
                else
                    simulations |= DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_OBLIQUE;
            }

            if (GetStretchPercent(lookup.Stretch) is var requested and > 0 && realStretchPercent > 0 && requested != realStretchPercent)
                scaleX = requested / realStretchPercent;
        }

        return (simulations, weightDelta, oblique, scaleX, axisValues);
    }

    /// <summary>The usWidthClass percentages of the normal width; 0 if undefined.</summary>
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

    private static HRESULT WithSimulations(IDWriteFontFace5* face, DWRITE_FONT_SIMULATIONS simulations, IDWriteFontFace5** made)
    {
        using var resource = default(MyComPtr<IDWriteFontResource>);
        var hr = face->GetFontResource(resource.GetAddressOf());
        if (hr.FAILED)
            return hr;

        var axes = new DWRITE_FONT_AXIS_VALUE[face->GetFontAxisValueCount()];
        fixed (DWRITE_FONT_AXIS_VALUE* p = axes)
        {
            hr = face->GetFontAxisValues(p, (uint)axes.Length);
            return hr.FAILED ? hr : resource.Value->CreateFontFace(simulations, p, (uint)axes.Length, made);
        }
    }

    private static MyComPtr<IDWriteFont3> GetRealFont(FontServices services, IDWriteFont3* font)
    {
        using var face = default(MyComPtr<IDWriteFontFace5>);
        font->CreateFontFace(face.GetAddressOf()).ThrowOnError();
        using var realFace = default(MyComPtr<IDWriteFontFace5>);
        WithSimulations(face.Value, DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE, realFace.GetAddressOf()).ThrowOnError();
        using var real = default(MyComPtr<IDWriteFont>);
        services.SystemCollection.GetFontFromFontFace((IDWriteFontFace*)realFace.Value, real.GetAddressOf()).ThrowOnError();
        var real3 = default(MyComPtr<IDWriteFont3>);
        real.CopyTo(real3.GetAddressOf()).ThrowOnError();
        return real3;
    }
}
