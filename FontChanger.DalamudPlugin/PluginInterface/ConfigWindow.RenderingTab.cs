using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Rendering.Shaders;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.PluginInterface;

internal sealed partial class ConfigWindow
{
    private static void DrawRenderingTab(ConfigWindow t)
    {
        var configuration = t._plugin.Configuration;
        var save = false;

        var edge = t._pendingEdge ?? configuration.Edge;
        var (edgeEnabled, scale, min, max, softness) = (edge.Enabled, edge.Scale, edge.Min, edge.Max, edge.Softness);
        var changed = ImGui.Checkbox("Use text border customization", ref edgeEnabled);
        var active = false;
        using (ImRaii.Disabled(!edgeEnabled))
        {
            changed |= ImGui.SliderFloat("Scale", ref scale, 0, 0.25f, "%.3f");
            active |= ImGui.IsItemActive();
            changed |= ImGui.SliderFloat("Minimum", ref min, 0.25f, 8f, "%.2f");
            active |= ImGui.IsItemActive();
            changed |= ImGui.SliderFloat("Maximum", ref max, 0.25f, 8f, "%.2f");
            active |= ImGui.IsItemActive();
            changed |= ImGui.SliderFloat("Softness", ref softness, 0, EdgeShader.MaxSoftness, "%.2f");
            active |= ImGui.IsItemActive();
        }

        if (changed) t._pendingEdge = new EdgeSettings(scale, min, max, softness, edgeEnabled).Clamped();

        if (TakeWhenReleased(ref t._pendingEdge, active) is { } pending)
        {
            configuration.Edge = pending;
            save = true;
            Plugin.Framework.RunOnTick(() => t._plugin.Replacer.Edge = pending);
        }

        ImGui.Separator();

        var mode = configuration.Subpixel;
        var modeChanged = false;
        var subpixel = mode != SubpixelMode.Off;
        if (ImGui.Checkbox("Use subpixel rendering", ref subpixel))
        {
            mode = subpixel ? SubpixelMode.System : SubpixelMode.Off;
            modeChanged = true;
        }

        using (ImRaii.Disabled(!subpixel))
        {
            var layout = Math.Max((int)mode - (int)SubpixelMode.System, 0);
            using (ImRaii.Group())
            {
                if (Ig.Radios("Subpixel layout", ref layout, "Follow Windows ClearType settings", "RGB", "BGR", "RGB (vertical)", "BGR (vertical)"))
                {
                    mode = SubpixelMode.System + layout;
                    modeChanged = true;
                }
            }

            Tooltip(
                "The order of each pixel's red, green and blue stripes. Windows only knows horizontal ones: choose a vertical layout for a monitor turned to portrait, or a handheld whose screen is a portrait panel turned to landscape (red at the top for RGB).");
        }

        var vertical = mode is SubpixelMode.VerticalRgb or SubpixelMode.VerticalBgr;
        using (ImRaii.Disabled(!vertical))
        {
            var renderer = (int)configuration.VerticalSubpixel;
            using (ImRaii.Group())
            {
                if (Ig.Radios("Vertical layout rendering", ref renderer, "FreeType", "DirectWrite, turned"))
                {
                    configuration.VerticalSubpixel = (VerticalSubpixelRenderer)renderer;
                    save = true;
                    Plugin.Framework.RunOnTick(() => t._plugin.Replacer.VerticalSubpixel = (VerticalSubpixelRenderer)renderer);
                }
            }

            Tooltip(
                "DirectWrite has no ClearType for vertical stripes, so fonts set to DirectWrite are drawn either by FreeType (hinted, but they look somewhat different) or by DirectWrite with the text turned a quarter and turned back (unhinted, so softer). Fonts set to FreeType are always drawn by FreeType.");
        }

        var own = mode is SubpixelMode.Rgb or SubpixelMode.Bgr || vertical;
        if (modeChanged)
        {
            if (own && configuration.ClearType == new SubpixelRenderingParameters())
            {
                var windows = SubpixelShaders.SettingsRecord.FromWindow(Plugin.PluginInterface.UiBuilder.WindowHandlePtr);
                configuration.ClearType = new SubpixelRenderingParameters(windows.Level, windows.Contrast, windows.Gamma).Clamped();
            }

            configuration.Subpixel = mode;
            save = true;
            Plugin.Framework.RunOnTick(t._plugin.ApplySubpixel);
        }

        var applied = t._plugin.Replacer.Subpixel;
        var clearType = t._pendingClearType
                        ?? (mode == SubpixelMode.System ? new(applied.Level, applied.Contrast, applied.Gamma) : configuration.ClearType);
        var (level, contrast, gamma) = (clearType.Level, clearType.Contrast, clearType.Gamma);
        changed = false;
        active = false;
        using (ImRaii.Disabled(!own))
        {
            changed |= ImGui.SliderFloat("Color separation", ref level, 0, 1, "%.2f");
            active |= ImGui.IsItemActive();
            Tooltip(
                "How differently the red, green and blue subpixels of a pixel are drawn (the ClearType level): 0 is grayscale, 1 is full ClearType. Lower values weaken color fringes but blur strokes.");
            changed |= ImGui.SliderFloat("Enhanced contrast", ref contrast, 0, 4, "%.2f");
            active |= ImGui.IsItemActive();
            Tooltip(
                "Darkens partly covered pixels, making strokes look heavier. 0 leaves them as they are. As in DirectWrite, light text gets less of it, and none from a lightness of 0.75.");
            changed |= ImGui.SliderFloat("Gamma", ref gamma, 1, 2.2f, "%.2f");
            active |= ImGui.IsItemActive();
            Tooltip("The display's gamma, which blending corrects for. Higher values make light text heavier and dark text lighter.");
        }

        if (changed) t._pendingClearType = new SubpixelRenderingParameters(level, contrast, gamma).Clamped();

        if (TakeWhenReleased(ref t._pendingClearType, active) is { } pendingClearType)
        {
            configuration.ClearType = pendingClearType;
            save = true;
            Plugin.Framework.RunOnTick(t._plugin.ApplySubpixel);
        }

        if (save) Plugin.PluginInterface.SavePluginConfig(configuration);
    }
}
