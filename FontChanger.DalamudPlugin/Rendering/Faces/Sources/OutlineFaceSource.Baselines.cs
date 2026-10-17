using System;
using System.Collections.Generic;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Faces.Sources;

// The font's baselines, from its BASE table.
internal abstract unsafe partial class OutlineFaceSource
{
    private static readonly uint[] BaselineTags =
    [
        Preset.Tag("romn"u8), Preset.Tag("ideo"u8), Preset.Tag("idtp"u8), Preset.Tag("icfb"u8), Preset.Tag("icft"u8),
    ];

    private Dictionary<uint, int>? _baselines;

    public bool TryGetBaseline(float px, Baseline baseline, out float y)
    {
        this._baselines ??= ReadBaselines(this.Font.Face);
        if (!this._baselines.TryGetValue(BaselineTags[(int)baseline], out var value))
        {
            y = 0;
            return false;
        }

        y = value * px * this.Transform.M22 / this.Font.Metrics.designUnitsPerEm;
        return true;
    }

    /// <summary>
    /// Reads the baselines of a font's BASE table (xivres read_baselines), in font units above the glyphs' origin: the
    /// roman baseline of Latin (or the default script), and the ideographic ones of Han, kana or Hangul (or the default).
    /// </summary>
    private static Dictionary<uint, int> ReadBaselines(IDWriteFontFace5* face)
    {
        var result = new Dictionary<uint, int>();
        void* data;
        uint size;
        void* context;
        BOOL exists;
        if (face->TryGetFontTable(Preset.Tag("BASE"u8), &data, &size, &context, &exists).FAILED || !exists)
            return result;
        try
        {
            var table = new ReadOnlySpan<byte>(data, (int)size);
            foreach (var (tag, value) in ReadHorizontalBaselines(table, [Preset.Tag("latn"u8), Preset.Tag("DFLT"u8)]))
            {
                if (tag == BaselineTags[(int)Baseline.Roman])
                    result.TryAdd(tag, value);
            }

            foreach (var (tag, value) in ReadHorizontalBaselines(
                         table,
                         [Preset.Tag("hani"u8), Preset.Tag("kana"u8), Preset.Tag("hang"u8), Preset.Tag("DFLT"u8)]))
            {
                if (tag != BaselineTags[(int)Baseline.Roman] && Array.IndexOf(BaselineTags, tag) >= 0)
                    result.TryAdd(tag, value);
            }
        }
        finally
        {
            face->ReleaseFontTable(context);
        }

        return result;
    }

    /// <summary>
    /// Reads the horizontal baselines of the first of <paramref name="scripts"/> the BASE table lists, or of its first
    /// script if none (xivres util::truetype::Base::ReadHorizontalBaselines).
    /// </summary>
    private static List<(uint Tag, int Value)> ReadHorizontalBaselines(ReadOnlySpan<byte> table, uint[] scripts)
    {
        var result = new List<(uint, int)>();
        if (U16(table, 0) != 1)
            return result;

        // Axis { Offset16 baseTagListOffset; Offset16 baseScriptListOffset; }
        var axis = U16(table, 4);
        if (axis == 0)
            return result;
        var tagListOffset = U16(table, axis);
        var scriptListOffset = U16(table, axis + 2);
        if (tagListOffset == 0 || scriptListOffset == 0)
            return result;

        // BaseTagList { uint16 baseTagCount; Tag baselineTags[]; }
        var tagList = axis + tagListOffset;
        var tags = new uint[U16(table, tagList)];
        for (var i = 0; i < tags.Length; i++)
        {
            if (tagList + 2 + (4 * i) + 4 > table.Length)
                return result;
            tags[i] = BitConverter.ToUInt32(table.Slice(tagList + 2 + (4 * i), 4));
        }

        // BaseScriptList { uint16 baseScriptCount; { Tag baseScriptTag; Offset16 baseScriptOffset; }[] }
        var scriptList = axis + scriptListOffset;
        var scriptCount = U16(table, scriptList);
        int? script = null;
        foreach (var wanted in scripts)
        {
            for (var i = 0; i < scriptCount && script is null; i++)
            {
                if (scriptList + 2 + (6 * i) + 6 > table.Length)
                    return result;
                if (BitConverter.ToUInt32(table.Slice(scriptList + 2 + (6 * i), 4)) == wanted)
                    script = scriptList + U16(table, scriptList + 2 + (6 * i) + 4);
            }

            if (script is not null)
                break;
        }

        if (script is null && scriptCount != 0)
            script = scriptList + U16(table, scriptList + 6);
        if (script is not { } s)
            return result;

        // BaseScript { Offset16 baseValuesOffset; ... }
        // BaseValues { uint16 defaultBaselineIndex; uint16 baseCoordCount; Offset16 baseCoordOffsets[]; }
        // BaseCoord { uint16 format; int16 coordinate; ... }
        var valuesOffset = U16(table, s);
        if (valuesOffset == 0)
            return result;
        var values = s + valuesOffset;
        var coordCount = U16(table, values + 2);
        for (var i = 0; i < coordCount && i < tags.Length; i++)
        {
            var coord = U16(table, values + 4 + (2 * i));
            if (coord != 0 && values + coord + 4 <= table.Length)
                result.Add((tags[i], (short)U16(table, values + coord + 2)));
        }

        return result;

        static int U16(ReadOnlySpan<byte> t, int offset) => offset >= 0 && offset + 2 <= t.Length ? (t[offset] << 8) | t[offset + 1] : 0;
    }
}
