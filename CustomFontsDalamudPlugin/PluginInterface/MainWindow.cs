using System.IO;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;

namespace CustomFonts;

/// <summary>The plugin's settings: the preset, whether the replacement is on, nameplate text and edge outlines.</summary>
internal sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly FileDialogManager fileDialog = new();
    private string? presetPath;
    private EdgeSettings? pendingEdge;

    public MainWindow(Plugin plugin)
        : base("Custom Fonts##CustomFontsMain")
    {
        this.plugin = plugin;
        this.SizeConstraints = new() { MinimumSize = new(480, 240), MaximumSize = new(float.MaxValue, float.MaxValue) };
    }

    public override void Draw()
    {
        this.fileDialog.Draw();
        this.DrawPreset();

        var replacer = this.plugin.Replacer;
        var enabled = replacer.Enabled;
        // Between frames: switching lays out every cached text node again.
        if (ImGui.Checkbox("Draw with DirectWrite glyphs", ref enabled))
            Plugin.Framework.RunOnTick(() => replacer.Enabled = enabled);

        var namePlates = this.plugin.NamePlates;
        var mode = (int)namePlates.Mode;
        if (ImGui.Combo("Nameplate text", ref mode, "As the game (baked, resampled)\0Baked once at full size\0Live every frame (sharpest, costly)\0"))
            Plugin.Framework.RunOnTick(() => namePlates.Mode = (NamePlateMode)mode);

        this.DrawEdgeSettings(replacer);
    }

    private void DrawPreset()
    {
        this.presetPath ??= this.plugin.Configuration.PresetPath;
        ImGui.InputText("##presetPath", ref this.presetPath, 1024);
        ImGui.SameLine();
        if (ImGui.Button("Browse...###presetBrowse"))
        {
            // Picking a file loads it.
            var current = this.presetPath.Trim().Trim('"');
            this.fileDialog.OpenFileDialog(
                "Choose a FontChanger preset",
                "Presets{.json}",
                (ok, paths) =>
                {
                    if (!ok || paths.Count == 0)
                        return;
                    this.presetPath = paths[0];
                    this.plugin.Presets.Apply(paths[0], this.plugin.Configuration.SystemFallback);
                },
                1,
                current.Length != 0 && Path.GetDirectoryName(current) is { Length: > 0 } directory && Directory.Exists(directory) ? directory : null);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("Preset file");
        var systemFallback = this.plugin.Configuration.SystemFallback;
        var changed = ImGui.Checkbox("Draw characters the preset lacks with Windows' fallback fonts (instead of the game's)", ref systemFallback);
        if (ImGui.Button("Load") || changed)
            this.plugin.Presets.Apply(this.presetPath, systemFallback);
        ImGui.SameLine();
        if (ImGui.Button("Use built-in face"))
        {
            this.presetPath = string.Empty;
            this.plugin.Presets.Apply(string.Empty, systemFallback);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(this.plugin.Presets.Status);
    }

    /// <summary>
    /// The edge outline's width: <c>clamp(scale * size, min, max)</c>. Every glyph is made again for new values, so the
    /// dragged values are kept until no slider is held.
    /// </summary>
    private void DrawEdgeSettings(FontReplacer replacer)
    {
        var edge = this.pendingEdge ?? replacer.Edge;
        ImGui.TextUnformatted("Edge outline: width = clamp(scale x text size, min, max)");

        var (scale, min, max) = (edge.Scale, edge.Min, edge.Max);
        var changed = ImGui.SliderFloat("Scale (px per px of text)", ref scale, 0, 0.25f, "%.3f");
        var active = ImGui.IsItemActive();
        changed |= ImGui.SliderFloat("Minimum (px)", ref min, 0.25f, 8f, "%.2f");
        active |= ImGui.IsItemActive();
        changed |= ImGui.SliderFloat("Maximum (px)", ref max, 0.25f, 8f, "%.2f");
        active |= ImGui.IsItemActive();
        if (changed)
            this.pendingEdge = new EdgeSettings { Scale = scale, Min = min, Max = max }.Clamped();

        if (this.pendingEdge is { } pending && !active)
        {
            this.pendingEdge = null;
            Plugin.Framework.RunOnTick(() => replacer.SetEdge(pending));
            this.plugin.Configuration.Edge = pending;
            Plugin.PluginInterface.SavePluginConfig(this.plugin.Configuration);
        }

        ImGui.TextUnformatted($"At 12 px: {edge.GetWidth(12):0.##} px, 24 px: {edge.GetWidth(24):0.##} px, 48 px: {edge.GetWidth(48):0.##} px");
    }
}
