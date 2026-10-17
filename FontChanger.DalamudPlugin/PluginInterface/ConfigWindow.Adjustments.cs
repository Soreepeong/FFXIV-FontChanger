using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Enums;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.PluginInterface;

internal sealed partial class ConfigWindow
{
    private static readonly (string Label, FontScripts Flag)[] ScriptChoices =
    [
        ("Non-CJK", FontScripts.NonCjk),
        ("Han", FontScripts.Han),
        ("Kana", FontScripts.Kana),
        ("Hangul", FontScripts.Hangul),
        ("Game symbols (PUA)", FontScripts.GameSymbols),
    ];

    // Applied only when let go, as each change saves the configuration and makes the fonts again.
    private (string Family, int Index, FontAdjustments Value)? _pendingAdjustments;

    private string? _draggedSlider;

    // Six rows (use for, digits, size, raise, letter spacing, stroke weight) and two lines of pixel notes.
    private static float AdjustmentsPaneHeight => (ImGui.GetFrameHeightWithSpacing() * 6) + (ImGui.GetTextLineHeightWithSpacing() * 2) + (ImGui.GetStyle().WindowPadding.Y * 2);

    private void DrawAdjustmentsPane(string family, int index, FontSource? source, float height)
    {
        Ig.Child("##adjustments", new(0, height), true, (this, family, index, source), static ctx => {
            var (t, family, index, source) = ctx;
            if (source is not { Type: FontSourceType.SystemFont })
            {
                ImGui.TextDisabled("Select a system font to adjust how the fonts are made from it.");
                return;
            }

            var adjustments = t._pendingAdjustments is { } p && p.Family == family && p.Index == index ? p.Value : source.Adjustments;
            using var table = ImRaii.Table("##adjustmentRows", 2, ImGuiTableFlags.SizingFixedFit);
            if (!table)
                return;
            ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("##controls", ImGuiTableColumnFlags.WidthStretch);

            Label("Use for");
            for (var i = 0; i < ScriptChoices.Length; i++)
            {
                var (label, flag) = ScriptChoices[i];
                if (i != 0)
                    ImGui.SameLine();
                var on = adjustments.Scripts.HasFlag(flag);
                if (ImGui.Checkbox(label, ref on))
                    t.Apply(family, index, source, adjustments with { Scripts = on ? adjustments.Scripts | flag : adjustments.Scripts & ~flag });
            }

            Label("Digits");
            var monospaced = source.MonospacedDigits;
            if (ImGui.Checkbox("Monospaced", ref monospaced))
                t._plugin.Presets.Replace(family, index, source with { MonospacedDigits = monospaced });

            Label("Size");
            t.Slider("Non-CJK##size", string.Empty, -15, 15, adjustments.NonCjkSize, family, index, source, v => adjustments with { NonCjkSize = v });
            ImGui.SameLine();
            t.Slider("CJK##size", string.Empty, -15, 15, adjustments.CjkSize, family, index, source, v => adjustments with { CjkSize = v });
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Larger or smaller than fit to the game's font.");

            Label("Raise / lower");
            t.Slider("##raise", " of size", -10, 10, adjustments.Raise, family, index, source, v => adjustments with { Raise = v });
            Note(DescribePixels(family, ("Up", adjustments.Raise)));

            Label("Letter spacing");
            t.Slider("Non-CJK##spacing", " of size", -10, 10, adjustments.NonCjkSpacing, family, index, source, v => adjustments with { NonCjkSpacing = v });
            ImGui.SameLine();
            t.Slider("CJK##spacing", " of size", -10, 10, adjustments.CjkSpacing, family, index, source, v => adjustments with { CjkSpacing = v });
            Note(DescribePixels(family, ("Non-CJK", adjustments.NonCjkSpacing), ("CJK", adjustments.CjkSpacing)));

            Label("Stroke weight");
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 8);
            using (var combo = ImRaii.Combo("##strokeWeight", adjustments.StrokeWeight.ToString()))
            {
                if (combo)
                {
                    foreach (var weight in Enum.GetValues<StrokeWeight>())
                    {
                        if (ImGui.Selectable(weight.ToString(), adjustments.StrokeWeight == weight) && adjustments.StrokeWeight != weight)
                            t.Apply(family, index, source, adjustments with { StrokeWeight = weight });
                    }
                }
            }

            ImGui.SameLine();
            using (ImRaii.Disabled(adjustments == FontAdjustments.None))
            {
                if (ImGui.Button("Reset adjustments"))
                    t.Apply(family, index, source, new());
            }
        });

        return;

        static void Label(string text)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(text);
            ImGui.TableNextColumn();
        }

        // Drawn even when empty, so the pane keeps its height.
        static void Note(IEnumerable<string> lines) => ImGui.TextDisabled(string.Join("; ", lines) is { Length: > 0 } text ? text : " ");
    }

    private void Apply(string family, int index, FontSource source, FontAdjustments adjustments)
    {
        this._pendingAdjustments = null;
        this._plugin.Presets.Replace(family, index, source with { Adjustments = adjustments });
    }

    private void Slider(string label, string suffix, int min, int max, float fraction, string family, int index, FontSource source, Func<float, FontAdjustments> with)
    {
        // ImGui rounds a dragged value to the shown precision, so whole percents are shown while dragging but typed values keep decimals.
        var percent = fraction * 100;
        var whole = this._draggedSlider == label || MathF.Abs(percent - MathF.Round(percent)) < 0.005f;
        var format = (whole ? "%+.0f%%" : "%+.2f%%") + suffix;

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
        if (ImGui.SliderFloat(label, ref percent, min, max, format))
            this._pendingAdjustments = (family, index, with(percent / 100));
        if (ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left) && !ImGui.GetIO().KeyCtrl)
            this._draggedSlider = label;
        else if (this._draggedSlider == label)
            this._draggedSlider = null;

        if (ImGui.IsItemDeactivatedAfterEdit() && this._pendingAdjustments is { } p && p.Family == family && p.Index == index)
            this.Apply(family, index, source, p.Value);
    }

    private static string DescribeAdjustments(FontAdjustments adjustments)
    {
        var parts = new List<string>();
        if (adjustments.Scripts != FontScripts.All)
        {
            parts.Add(
                adjustments.Scripts == FontScripts.None
                    ? "nothing"
                    : string.Join("+", ScriptChoices.Where(c => adjustments.Scripts.HasFlag(c.Flag)).Select(c => c.Label)));
        }

        if (adjustments.NonCjkSize != 0)
            parts.Add($"non-CJK {Percent(adjustments.NonCjkSize)}");
        if (adjustments.CjkSize != 0)
            parts.Add($"CJK {Percent(adjustments.CjkSize)}");
        if (adjustments.Raise != 0)
            parts.Add($"raised {Percent(adjustments.Raise)}");
        if (adjustments.NonCjkSpacing != 0)
            parts.Add($"non-CJK spacing {Percent(adjustments.NonCjkSpacing)}");
        if (adjustments.CjkSpacing != 0)
            parts.Add($"CJK spacing {Percent(adjustments.CjkSpacing)}");
        if (adjustments.StrokeWeight != StrokeWeight.Normal)
            parts.Add(adjustments.StrokeWeight.ToString().ToLowerInvariant());
        return string.Join(", ", parts);
    }

    // Rounded away from zero, as the fonts are made.
    private static IEnumerable<string> DescribePixels(string family, params (string Label, float Fraction)[] values)
    {
        var sizes = FontNames.FontNamesOf(family).Select(f => f.Size).Distinct().Order().ToList();
        foreach (var (label, fraction) in values)
        {
            if (fraction == 0 || sizes.Count == 0)
                continue;

            var bySize = sizes.GroupBy(s => (int)MathF.Round(fraction * s, MidpointRounding.AwayFromZero))
                              .Select(g => $"{g.Key:+0;-0;0} px at {string.Join(", ", g.Select(s => s.ToString(CultureInfo.InvariantCulture)))}");
            yield return $"{label}: {string.Join("; ", bySize)}";
        }
    }

    private static string Percent(float fraction) => (fraction * 100).ToString("+0.##;-0.##", CultureInfo.InvariantCulture) + "%";
}
