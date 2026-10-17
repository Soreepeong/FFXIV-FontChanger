using System;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Utilities;

namespace FontChanger.DalamudPlugin.PluginInterface;

internal sealed partial class ConfigWindow
{
    private static void DrawAdvancedTab(ConfigWindow t)
    {
        if (t._plugin.NamePlates is { } namePlates)
        {
            var mode = (int)t._plugin.Configuration.NamePlates;
            if (Ig.Radios("Nameplate text rendering", ref mode, "None", "Once", "Live"))
            {
                t._plugin.Configuration.NamePlates = (NamePlateMode)mode;
                Plugin.PluginInterface.SavePluginConfig(t._plugin.Configuration);
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
                Plugin.PluginInterface.SavePluginConfig(t._plugin.Configuration);
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset")) t._pendingAtlas = new AtlasSettings().Clamped();
    }
}
