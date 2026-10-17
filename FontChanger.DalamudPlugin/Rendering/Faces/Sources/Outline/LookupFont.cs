using System;
using System.Collections.Generic;

using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources.Outline;

/// <summary>
/// The font a lookup resolves to, as FontChanger makes it (Structs.cpp ResolveFont, ResolveSynthesis): the face DirectWrite
/// matches, its simulations, and the axis values of a variable font. With explicit synthesis, the real face is taken
/// instead, and what the lookup asks for that it lacks is made (<see cref="SynthesizedFace"/>).
/// </summary>
internal sealed unsafe partial class LookupFont : IDisposable
{
    private readonly DWRITE_FONT_SIMULATIONS _simulations;

    // The axis values, without the optical size when it follows the text size; and the faces made with them.
    private readonly DWRITE_FONT_AXIS_VALUE[] _axes = [];
    private readonly Dictionary<float, MyComPtr<IDWriteFontFace5>> _sizedFaces = [];
    private readonly DWRITE_FONT_AXIS_RANGE _opticalSizeRange;

    private MyComPtr<IDWriteFontResource> _resource;
    private MyComPtr<IDWriteFont3> _font;
    private MyComPtr<IDWriteFontFace5> _face;

    /// <summary>Takes a reference of its own to the font.</summary>
    private LookupFont(IDWriteFont3* font, LookupStruct lookup, Renderer renderer)
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
            if (face->HasVariations())
            {
                fixed (IDWriteFontResource** resource = this._resource)
                    face->GetFontResource(resource).ThrowOnError();
                var r = this._resource.Value;
                ranges = new DWRITE_FONT_AXIS_RANGE[r->GetFontAxisCount()];
                fixed (DWRITE_FONT_AXIS_RANGE* p = ranges)
                    r->GetFontAxisRanges(p, (uint)ranges.Length).ThrowOnError();
                this._axes = new DWRITE_FONT_AXIS_VALUE[face->GetFontAxisValueCount()];
                fixed (DWRITE_FONT_AXIS_VALUE* p = this._axes)
                    face->GetFontAxisValues(p, (uint)this._axes.Length).ThrowOnError();
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

    /// <summary>
    /// Gets the face: with the simulations, but for a variable font, the matched instance (for its file and metrics;
    /// glyphs are drawn from <see cref="GetFace"/>'s).
    /// </summary>
    public IDWriteFontFace5* Face => this._face.Value;

    public DWRITE_FONT_METRICS Metrics { get; }

    private SynthesizedFace Synthesis { get; }

    /// <summary>Gets the transformation on screen that synthesis makes, before the element's: a slant (FreeType), a width.</summary>
    public Matrix2x2 Transform => this.Synthesis.Transform;

    /// <summary>Gets how much FreeType emboldens glyphs, in ems (negative values thin them); they advance that much further.</summary>
    public float Embolden => this.Synthesis.Embolden;

    /// <summary>Gets a variable font's axis values (as DWRITE_FONT_AXIS_TAG), but the optical size if it follows the text size.</summary>
    private IReadOnlyDictionary<uint, float> AxisValues { get; }

    /// <summary>Gets whether the optical size follows the text size.</summary>
    public bool AutoOpticalSize { get; }

    /// <summary>Gets whether the font is variable (DirectWrite has them from Windows 10 21H1).</summary>
    private bool IsVariable => this._resource;

    /// <summary>Finds the font a lookup asks for and makes it as the element draws it; null if the family isn't installed.</summary>
    public static LookupFont? Create(FontServices services, LookupStruct lookup, Renderer renderer)
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

            return new(font.Value, lookup, renderer);
        }
        finally
        {
            font.Dispose();
        }
    }

    /// <summary>Gets the face at a text size: the optical size set to it, if it follows. Owned by this object.</summary>
    public IDWriteFontFace5* GetFace(float px)
    {
        if (!this.IsVariable)
            return this.Face;

        var key = this.AutoOpticalSize ? Misc.StdRound(px * 4) / 4 : 0;
        if (this._sizedFaces.TryGetValue(key, out var existing))
            return existing.Value;

        var values = new DWRITE_FONT_AXIS_VALUE[this._axes.Length + (this.AutoOpticalSize ? 1 : 0)];
        this._axes.CopyTo(values, 0);
        if (this.AutoOpticalSize)
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
            face.Value->Release();
        this._sizedFaces.Clear();
        this._resource.Dispose();
        this._face.Dispose();
        this._font.Dispose();
    }

    /// <summary>Makes a face of the same font resource and axis values as a face, with other simulations.</summary>
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

    /// <summary>Gets the font without simulations a simulated font (of the system's) is made from.</summary>
    private static MyComPtr<IDWriteFont3> GetRealFont(FontServices services, IDWriteFont3* font)
    {
        using var face = default(MyComPtr<IDWriteFontFace5>);
        font->CreateFontFace(face.GetAddressOf()).ThrowOnError();
        using var realFace = default(MyComPtr<IDWriteFontFace5>);
        WithSimulations(face.Value, DWRITE_FONT_SIMULATIONS.DWRITE_FONT_SIMULATIONS_NONE, realFace.GetAddressOf()).ThrowOnError();
        using var real = default(MyComPtr<IDWriteFont>);
        services.SystemCollection->GetFontFromFontFace((IDWriteFontFace*)realFace.Value, real.GetAddressOf()).ThrowOnError();
        var real3 = default(MyComPtr<IDWriteFont3>);
        real.CopyTo(real3.GetAddressOf()).ThrowOnError();
        return real3;
    }
}
