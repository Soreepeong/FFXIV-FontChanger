using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ImGuiFontChooserDialog;
using Dalamud.Interface.Windowing;

namespace CustomFonts;

/// <summary>The plugin's settings: the presets, whether the replacement is on, nameplate text and edge outlines.</summary>
internal sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly FileDialogManager fileDialog = new();
    private string? presetFolder;
    // The font chooser open, if one is.
    private FontChooser? fontChooser;
    private EdgeSettings? pendingEdge;

    // The name shown of the system font last shown, which takes listing the system's fonts to find.
    private (FamilyFont Font, string Label)? fontLabel;

    // The family presets are selected for; null for all.
    private string? family;

    public MainWindow(Plugin plugin)
        : base("Custom Fonts##CustomFontsMain")
    {
        this.plugin = plugin;
        this.SizeConstraints = new() { MinimumSize = new(480, 400), MaximumSize = new(float.MaxValue, float.MaxValue) };

        // Open if it was when the plugin was last unloaded. Saved as it opens and closes, not at unloading, which removes
        // the window without closing it.
        this.IsOpen = plugin.Configuration.MainWindowOpen;
    }

    public override void OnOpen() => this.SaveOpen(true);

    public override void OnClose() => this.SaveOpen(false);

    private void SaveOpen(bool open)
    {
        if (this.plugin.Configuration.MainWindowOpen == open)
            return;
        this.plugin.Configuration.MainWindowOpen = open;
        Plugin.PluginInterface.SavePluginConfig(this.plugin.Configuration);
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

        if (this.plugin.NamePlates is { } namePlates)
        {
            var mode = (int)namePlates.Mode;
            if (ImGui.Combo("Nameplate text", ref mode, "As the game (baked, resampled)\0Baked once at full size\0Live every frame (sharpest, costly)\0"))
                Plugin.Framework.RunOnTick(() => namePlates.Mode = (NamePlateMode)mode);
        }
        else
        {
            ImGui.TextDisabled("Nameplate text: as the game (this version isn't supported; see the log)");
        }

        this.DrawEdgeSettings(replacer);
    }

    private void DrawPreset()
    {
        var presets = this.plugin.Presets;
        this.presetFolder ??= presets.Folder;
        ImGui.InputText("##presetFolder", ref this.presetFolder, 1024);
        var entered = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SameLine();
        if (ImGui.Button("Browse...###presetBrowse"))
        {
            // Picking a folder uses it.
            var current = this.presetFolder.Trim().Trim('"');
            this.fileDialog.OpenFolderDialog(
                "Choose a folder of FontChanger presets",
                (ok, path) =>
                {
                    if (!ok || path.Length == 0)
                        return;
                    this.presetFolder = path;
                    presets.SetFolder(path);
                },
                current.Length != 0 && Directory.Exists(current) ? current : null);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("Preset folder");
        if (entered)
            presets.SetFolder(this.presetFolder);

        var systemFallback = presets.SystemFallback;
        if (ImGui.Checkbox("Draw characters the presets lack with Windows' fallback fonts (instead of the game's)", ref systemFallback))
            presets.SetSystemFallback(systemFallback);
        if (this.family is not null && !presets.Families.Contains(this.family))
            this.family = null;
        if (ImGui.Button("Reload"))
            presets.Reload();
        ImGui.SameLine();
        if (ImGui.Button(this.family is null ? "Use the game's fonts for all###gameFonts" : $"Use the game's fonts for {this.family}###gameFonts"))
            presets.ClearSelection(this.family);
        ImGui.SameLine();
        ImGui.TextUnformatted(presets.Status);
        this.DrawFontChoice();

        // The family list and the tree take what the settings below them leave.
        var reserved = ImGui.GetFrameHeightWithSpacing() * 7;
        var height = Math.Max(ImGui.GetContentRegionAvail().Y - reserved, ImGui.GetFrameHeightWithSpacing() * 4);
        if (ImGui.BeginChild("##families", new Vector2(ImGui.GetFontSize() * 11, height), true))
            this.DrawFamilies();
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##presetTree", new Vector2(0, height), true))
        {
            var tree = presets.Tree;
            if (tree.Folders.Count == 0 && tree.Files.Count == 0)
                ImGui.TextUnformatted("No presets (.json files) in the folder.");
            else
                this.DrawPresetTree(tree);
        }

        ImGui.EndChild();
    }

    /// <summary>Draws the families to select presets for, with how many each has; hovering one lists them.</summary>
    private void DrawFamilies()
    {
        var presets = this.plugin.Presets;
        if (ImGui.Selectable("All families", this.family is null))
            this.family = null;
        foreach (var f in presets.Families)
        {
            var selected = presets.GetSelection(f);
            var font = presets.GetFont(f);
            var label = selected.Count == 0 ? f : $"{f}  ({selected.Count})";
            if (font is not null)
                label += "  [font]";
            if (ImGui.Selectable($"{label}###family{f}", string.Equals(this.family, f, StringComparison.OrdinalIgnoreCase)))
                this.family = f;
            if (ImGui.IsItemHovered())
            {
                var tooltip = selected.Count == 0 && font is null
                                  ? "The game's fonts"
                                  : string.Join("\n", selected.Select((p, i) => $"{i + 1}. {Path.GetFileNameWithoutExtension(p)}"));
                if (font is not null)
                    tooltip = $"System font: {font.Name}" + (selected.Count != 0 ? $" (over the presets)\n{tooltip}" : string.Empty);
                ImGui.SetTooltip(tooltip);
            }
        }
    }

    /// <summary>
    /// Draws the system font that draws the family's faces (or every family's, for all families) over its presets, made as
    /// <see cref="FaceFromFont"/> makes them; and its face.
    /// </summary>
    private void DrawFontChoice()
    {
        var presets = this.plugin.Presets;
        var chosen = (this.family is { } f ? (IReadOnlyList<string>)[f] : presets.Families).Select(presets.GetFont).ToList();
        var current = chosen.Count != 0 && chosen.All(c => c == chosen[0]) ? chosen[0] : null;
        var various = current is null && chosen.Any(c => c is not null);

        // Dalamud's font chooser, with the system's fonts; what is chosen in it goes to the family (or all) it was opened for.
        var label = current is null ? various ? "(various)" : "(presets)" : this.GetFontLabel(current);
        if (ImGui.Button($"{label}###chooseSystemFont") && this.fontChooser is null)
        {
            var dialog = SingleFontChooserDialog.CreateAuto((UiBuilder)Plugin.PluginInterface.UiBuilder);
            dialog.Title = this.family is null ? "System font for all families" : $"System font for {this.family}";
            dialog.FontFamilyExcludeFilter = x => x is not SystemFontFamilyId;
            dialog.SelectedFont = new() { FontId = FindSystemFont(current) ?? FindSystemFont(new() { Name = "Segoe UI" })!, SizePx = 24 };

            // The fonts before, given back if the chooser is cancelled; each one chosen in it is previewed in game.
            var before = (this.family is { } one ? (IReadOnlyList<string>)[one] : presets.Families).ToDictionary(x => x, presets.GetFont);
            var chooser = new FontChooser(dialog, this.family, before);
            dialog.SelectedFontSpecChanged += spec => chooser.Preview(ToFamilyFont(spec));
            this.fontChooser = chooser;
        }

        if (this.fontChooser is { } open)
        {
            if (open.Dialog.ResultTask.IsCompleted)
            {
                this.fontChooser = null;
                var result = open.Dialog.ResultTask;
                if (!result.IsCompletedSuccessfully)
                {
                    if (open.Applied is not null)
                        presets.SetFonts(open.Before);
                }
                else if (ToFamilyFont(result.Result) is { } final)
                {
                    // The last preview is the choice, only to be saved.
                    if (final != open.Applied)
                        presets.SetFont(open.Family, final);
                    else
                        presets.Save();
                }
            }
            else if (open.TakePreview() is { } preview)
            {
                presets.SetFont(open.Family, preview, false);
            }
        }

        ImGui.SameLine();

        if (current is not null || various)
        {
            if (ImGui.Button("Use presets###useFontPresets"))
                presets.SetFont(this.family, null);
            ImGui.SameLine();
        }

        ImGui.TextUnformatted(this.family is null ? "System font for all families" : $"System font for {this.family}");

        var monospacedDigits = presets.MonospacedDigits;
        if (ImGui.Checkbox("Monospaced digits for system fonts (tabular figures, or cells as wide as the 0)", ref monospacedDigits))
            presets.SetMonospacedDigits(monospacedDigits);
    }

    /// <summary>Gets the system font a font chooser's choice is, or null if it isn't a system font.</summary>
    private static FamilyFont? ToFamilyFont(SingleFontSpec spec)
    {
        if (spec.FontId is not SystemFontId id)
            return null;
        var name = id.Family is SystemFontFamilyId familyId ? familyId.EnglishName : id.EnglishName;
        return new() { Name = name, Weight = id.Weight, Stretch = id.Stretch, Style = id.Style };
    }

    /// <summary>Gets a system font's name as shown: its family's and its face's, in the user's language.</summary>
    private string GetFontLabel(FamilyFont font)
    {
        if (this.fontLabel is { } cached && cached.Font == font)
            return cached.Label;
        var locale = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
        var label = FindSystemFont(font) is { } id ? $"{id.Family.GetLocalizedName(locale)} {id.GetLocalizedName(locale)}" : font.Name;
        this.fontLabel = (font, label);
        return label;
    }

    /// <summary>Finds a system font's face as Dalamud's font chooser lists it; null if it isn't installed.</summary>
    private static IFontId? FindSystemFont(FamilyFont? font)
    {
        if (font is null)
            return null;
        var family = IFontFamilyId.ListSystemFonts(false)
                                  .OfType<SystemFontFamilyId>()
                                  .FirstOrDefault(x => string.Equals(x.EnglishName, font.Name, StringComparison.OrdinalIgnoreCase));
        return family is null || family.Fonts.Count == 0 ? null : family.Fonts[family.FindBestMatch(font.Weight, font.Stretch, font.Style)];
    }

    /// <summary>
    /// Draws a folder's contents: selecting a preset for a family puts its faces of the family over those of the presets
    /// selected for it before; for all families, it is selected for every family (or deselected if it is for all).
    /// </summary>
    private void DrawPresetTree(PresetTreeNode node)
    {
        var presets = this.plugin.Presets;
        foreach (var folder in node.Folders)
        {
            // Named by the folder alone, within its parent's: it stays open when the tree is read again.
            if (ImGui.TreeNodeEx(folder.Name, ImGuiTreeNodeFlags.SpanAvailWidth))
            {
                this.DrawPresetTree(folder);
                ImGui.TreePop();
            }
        }

        foreach (var file in node.Files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            string label;
            bool selected;
            if (this.family is { } f)
            {
                var order = presets.GetSelectionOrder(f, file);
                (label, selected) = (order == 0 ? name : $"{name}  [{order}]", order != 0);
            }
            else
            {
                var count = presets.GetFamilyCount(file);
                var all = presets.Families.Count;
                (label, selected) = (count == 0 ? name : count >= all ? $"{name}  [all]" : $"{name}  [{count}/{all}]", count != 0);
            }

            if (ImGui.Selectable($"{label}###{file}", selected))
            {
                if (this.family is { } toggled)
                    presets.Toggle(toggled, file);
                else
                    presets.ToggleForAll(file);
            }
        }
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

    /// <summary>
    /// An open font chooser: the family it was opened for (null for all), each family's font before it, and the font
    /// chosen in it, previewed once the choice has stayed for <see cref="PreviewDelay"/> (each preview makes every glyph
    /// again, too much to do on every step through the list).
    /// </summary>
    private sealed class FontChooser(SingleFontChooserDialog dialog, string? family, IReadOnlyDictionary<string, FamilyFont?> before)
    {
        private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(300);

        private FamilyFont? pending;
        private long pendingSince;

        public SingleFontChooserDialog Dialog => dialog;

        public string? Family => family;

        public IReadOnlyDictionary<string, FamilyFont?> Before => before;

        /// <summary>Gets the font last previewed, or null if none was.</summary>
        public FamilyFont? Applied { get; private set; }

        /// <summary>Notes a font chosen in the chooser, to preview once it stays chosen.</summary>
        public void Preview(FamilyFont? font)
        {
            this.pending = font;
            this.pendingSince = Environment.TickCount64;
        }

        /// <summary>Gets the font to preview now, if one has stayed chosen long enough and isn't the one previewed.</summary>
        public FamilyFont? TakePreview()
        {
            if (this.pending is not { } font || Environment.TickCount64 - this.pendingSince < PreviewDelay.TotalMilliseconds)
                return null;
            this.pending = null;
            if (font == this.Applied)
                return null;
            this.Applied = font;
            return font;
        }
    }
}
