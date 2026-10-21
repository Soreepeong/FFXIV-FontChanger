using System;
using System.Collections.Generic;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Fonts.Shaders;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.PluginInterface;

internal sealed partial class ConfigWindow : Window
{
    private const string PresetFilter = "FontChanger presets{.json},All files{.*}";

    private readonly Plugin _plugin;
    private readonly FileDialogManager _fileDialog = new();
    private FontChooser? _fontChooser;
    private EdgeSettings? _pendingEdge;
    private SubpixelRenderingParameters? _pendingClearType;
    private AtlasSettings? _pendingAtlas;

    private string? _selectedFamily;
    private string? _copyFromFamily;
    private int _selectedFamilyElement = -1;
    private int? _draggedFamilyElement;

    private (string Family, int Size)? _pendingFontSize;

    // Refreshed when an attribute menu or the font chooser opens, so newly installed fonts show without listing them every frame.
    private List<SystemFontFamilyId>? _systemFamilies;

    public ConfigWindow(Plugin plugin) : base($"Font Changer##{typeof(ConfigWindow).FullName}")
    {
        this._plugin = plugin;
        this.SizeConstraints = new() { MinimumSize = new(480, 400), MaximumSize = new(float.MaxValue, float.MaxValue) };
        this.IsOpen = plugin.Configuration.ConfigWindowOpen;
    }

    public override void OnOpen() => this.SaveOpen(true);

    public override void OnClose() => this.SaveOpen(false);

    public override void Draw()
    {
        var replacer = this._plugin.Replacer;
        var enabled = replacer.Enabled;
        if (ImGui.Checkbox("Use Custom Fonts", ref enabled))
            Plugin.Framework.RunOnTick(() => replacer.Enabled = enabled);

        this._fileDialog.Draw();

        Ig.TabBar("##tabs", this, static t => {
            Ig.TabItem("Font", t, DrawFontTab);
            Ig.TabItem("Rendering", t, DrawRenderingTab);
            Ig.TabItem("Advanced", t, DrawAdvancedTab);
        });
    }

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
            SliderFloat("Scale", ref scale, 0, 0.25f, "%.3f", ref changed, ref active);
            SliderFloat("Minimum", ref min, 0.25f, 8f, "%.2f", ref changed, ref active);
            SliderFloat("Maximum", ref max, 0.25f, 8f, "%.2f", ref changed, ref active);
            SliderFloat("Softness", ref softness, 0, EdgeShader.MaxSoftness, "%.2f", ref changed, ref active);
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
        }

        var own = mode is SubpixelMode.Rgb or SubpixelMode.Bgr || vertical;
        if (modeChanged)
        {
            if (own && configuration.ClearType == new SubpixelRenderingParameters())
            {
                var windows = SubpixelShaders.SettingsRecord.FromWindow(Plugin.PluginInterface.UiBuilder.WindowHandlePtr);
                configuration.ClearType = new SubpixelRenderingParameters(windows.Level, windows.Contrast, windows.Gamma, windows.GrayscaleContrast).Clamped();
            }

            configuration.Subpixel = mode;
            save = true;
            Plugin.Framework.RunOnTick(t._plugin.ApplySubpixel);
        }

        var applied = t._plugin.Replacer.Subpixel;
        var clearType = t._pendingClearType
                        ?? (mode == SubpixelMode.System
                                ? new(applied.Level, applied.Contrast, applied.Gamma, applied.GrayscaleContrast)
                                : configuration.ClearType);
        var (level, contrast, gamma, grayscaleContrast) = (clearType.Level, clearType.Contrast, clearType.Gamma, clearType.GrayscaleContrast);
        changed = false;
        active = false;
        using (ImRaii.Disabled(!own))
        {
            SliderFloat("Color separation", ref level, 0, 1, "%.2f", ref changed, ref active);
            SliderFloat("Enhanced contrast", ref contrast, 0, 4, "%.2f", ref changed, ref active);
            SliderFloat("Gamma", ref gamma, 1, 2.2f, "%.2f", ref changed, ref active);
            SliderFloat("Enhanced contrast (scaled text)", ref grayscaleContrast, 0, 4, "%.2f", ref changed, ref active);
        }

        if (changed) t._pendingClearType = new SubpixelRenderingParameters(level, contrast, gamma, grayscaleContrast).Clamped();

        if (TakeWhenReleased(ref t._pendingClearType, active) is { } pendingClearType)
        {
            configuration.ClearType = pendingClearType;
            save = true;
            Plugin.Framework.RunOnTick(t._plugin.ApplySubpixel);
        }

        if (save) t.SaveConfiguration();
    }

    private static void DrawAdvancedTab(ConfigWindow t)
    {
        if (t._plugin.NamePlates is { } namePlates)
        {
            var mode = (int)t._plugin.Configuration.NamePlates;
            if (Ig.Radios("Nameplate text rendering", ref mode, "None", "Once", "Live"))
            {
                t._plugin.Configuration.NamePlates = (NamePlateMode)mode;
                t.SaveConfiguration();
                Plugin.Framework.RunOnTick(() => namePlates.Mode = (NamePlateMode)mode);
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Glyph atlas pages");

        var changed = false;
        var atlas = t._pendingAtlas ?? t._plugin.Replacer.Atlas;

        Span<int> axisSize = [atlas.AxisPageWidth, atlas.AxisPageHeight];
        changed |= ImGui.InputInt("AXIS page size", axisSize, 256);

        var axisPages = atlas.AxisMaxPages;
        changed |= ImGui.InputInt("AXIS max pages", ref axisPages);

        Span<int> otherSize = [atlas.OtherPageWidth, atlas.OtherPageHeight];
        changed |= ImGui.InputInt("Other page size", otherSize, 256);

        var otherPages = atlas.OtherMaxPages;
        changed |= ImGui.InputInt("Other max pages", ref otherPages);

        var format = (int)atlas.Format;
        changed |= Ig.Radios("Texture format", ref format, "BGRA8888", "BGRA4444");
        if (format == (int)AtlasTextureFormat.Bgra4444 && !t._plugin.Replacer.Supports4444)
            ImGui.TextDisabled("The game's graphics device doesn't support BGRA4444; BGRA8888 is used.");

        if (changed)
        {
            t._pendingAtlas = new AtlasSettings
            {
                AxisPageWidth = axisSize[0],
                AxisPageHeight = axisSize[1],
                AxisMaxPages = axisPages,
                OtherPageWidth = otherSize[0],
                OtherPageHeight = otherSize[1],
                OtherMaxPages = otherPages,
                Format = (AtlasTextureFormat)format,
            }.Clamped();
        }

        using (ImRaii.Disabled(t._pendingAtlas is null || t._pendingAtlas == t._plugin.Replacer.Atlas))
        {
            if (ImGui.Button("Apply"))
            {
                var pending = t._pendingAtlas!.Value;
                t._pendingAtlas = null;
                Plugin.Framework.RunOnTick(() => t._plugin.Replacer.Atlas = pending);
                t._plugin.Configuration.Atlas = pending;
                t.SaveConfiguration();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset")) t._pendingAtlas = new AtlasSettings().Clamped();
    }

    private static void SliderFloat(string label, ref float value, float min, float max, string format, ref bool changed, ref bool active)
    {
        changed |= ImGui.SliderFloat(label, ref value, min, max, format);
        active |= ImGui.IsItemActive();
    }

    private static T? TakeWhenReleased<T>(ref T? pending, bool active) where T : struct
    {
        if (active || pending is not { } value)
            return null;
        pending = null;
        return value;
    }

    private void SaveOpen(bool open)
    {
        if (this._plugin.Configuration.ConfigWindowOpen == open)
            return;
        this._plugin.Configuration.ConfigWindowOpen = open;
        this.SaveConfiguration();
    }

    private void SaveConfiguration() => Plugin.PluginInterface.SavePluginConfig(this._plugin.Configuration);
}
