using System;
using System.Collections.Generic;

using FontChanger.DalamudPlugin.Rendering.Rasterization;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;
using FontChanger.Presets.Utilities;

using TerraFX.Interop.DirectX;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources.Outline;

internal sealed partial class LookupFont
{
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
        public static unsafe SynthesizedFace Resolve(LookupStruct lookup, Renderer renderer, IDWriteFont3* font, ReadOnlySpan<DWRITE_FONT_AXIS_RANGE> ranges)
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
}
