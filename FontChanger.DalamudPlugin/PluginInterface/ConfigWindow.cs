using System.Collections.Generic;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;

using FontChanger.DalamudPlugin.Configuration;
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

    // The system's font families, listed when an attribute menu or the font chooser opens.
    private List<SystemFontFamilyId>? _systemFamilies;

    public ConfigWindow(Plugin plugin) : base($"Font Changer##{typeof(ConfigWindow).FullName}")
    {
        this._plugin = plugin;
        this.SizeConstraints = new() { MinimumSize = new(480, 400), MaximumSize = new(float.MaxValue, float.MaxValue) };
        this.IsOpen = plugin.Configuration.ConfigWindowOpen;
    }

    public override void OnOpen() => this.SaveOpen(true);

    public override void OnClose() => this.SaveOpen(false);

    private void SaveOpen(bool open)
    {
        if (this._plugin.Configuration.ConfigWindowOpen == open)
            return;
        this._plugin.Configuration.ConfigWindowOpen = open;
        Plugin.PluginInterface.SavePluginConfig(this._plugin.Configuration);
    }

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

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }
}
