using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ImGuiFontChooserDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Rendering;
using FontChanger.DalamudPlugin.Rendering.Game;
using FontChanger.DalamudPlugin.Rendering.Shaders;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.PluginInterface;

internal sealed class ConfigWindow : Window
{
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

    private static void DrawFontTab(ConfigWindow t)
    {
        var presets = t._plugin.Presets;

        var systemFallback = presets.SystemFallback;
        if (ImGui.Checkbox("Use System Fallback Fonts", ref systemFallback)) presets.SetSystemFallback(systemFallback);

        if (ImGui.Button("Reload")) presets.Reload();
        ImGui.SameLine();
        ImGui.TextUnformatted(presets.Status);

        t.DrawFontChooser();

        var families = GameFontNames.Families;
        if (t._selectedFamily is null || !families.Contains(t._selectedFamily))
            t._selectedFamily = families.Count != 0 ? families[0] : null;

        var height = Math.Max(ImGui.GetContentRegionAvail().Y, ImGui.GetFrameHeightWithSpacing() * 6);
        Ig.Child("##families", new(ImGui.GetFontSize() * 11, height), true, t, static t => {
            var presets = t._plugin.Presets;
            foreach (var f in GameFontNames.Families)
            {
                var sources = presets.GetSources(f);
                var count = presets.IsFamilyEnabled(f) ? sources.Count(s => s.Enabled) : 0;
                var label = count == 0 ? f : $"{f}  ({count})";
                if (ImGui.Selectable($"{label}###family{f}", string.Equals(t._selectedFamily, f, StringComparison.OrdinalIgnoreCase)) && t._selectedFamily != f)
                {
                    t._selectedFamily = f;
                    t._selectedFamilyElement = -1;
                }
            }
        });
        ImGui.SameLine();
        Ig.Child("##chain", new(0, height), true, t, static t => {
            var presets = t._plugin.Presets;
            if (t._selectedFamily is not { } family)
                return;

            var sources = presets.GetSources(family);
            if (t._selectedFamilyElement >= sources.Count)
                t._selectedFamilyElement = sources.Count - 1;

            var familyEnabled = presets.IsFamilyEnabled(family);
            if (ImGui.Checkbox("Enabled", ref familyEnabled))
                presets.SetFamilyEnabled(family, familyEnabled);

            ImGui.SameLine();
            if (ImGui.Button("Edit...###editSources")) ImGui.OpenPopup("##editSources");
            Ig.Popup("##editSources", (t, family, sources), static ctx => {
                var presets = ctx.t._plugin.Presets;
                var has = ctx.t._selectedFamilyElement >= 0 && ctx.t._selectedFamilyElement < ctx.sources.Count;

                if (ImGui.MenuItem("Add presets...", string.Empty))
                {
                    ctx.t._fileDialog.OpenFileDialog($"Add presets for {ctx.family}", "FontChanger presets{.json},All files{.*}", (ok, paths) => {
                        if (ok && paths.Count != 0)
                        {
                            presets.AddPresets(ctx.family, paths);
                            ctx.t._selectedFamilyElement = presets.GetSources(ctx.family).Count - 1;
                        }
                    }, 0);
                }

                if (ImGui.MenuItem("Add system font...", string.Empty))
                {
                    ctx.t.OpenFontChooser($"Add a system font for {ctx.family}", null, font => {
                        presets.AddFont(ctx.family, font);
                        ctx.t._selectedFamilyElement = presets.GetSources(ctx.family).Count - 1;
                    });
                }

                ImGui.Separator();

                if (ImGui.MenuItem("Change...", string.Empty, false, has))
                    ctx.t.PickAnother(ctx.family, ctx.t._selectedFamilyElement, ctx.sources[ctx.t._selectedFamilyElement]);

                if (ImGui.MenuItem("Move up", string.Empty, false, has && ctx.t._selectedFamilyElement > 0))
                    ctx.t._selectedFamilyElement = presets.Move(ctx.family, ctx.t._selectedFamilyElement, -1);
                if (ImGui.MenuItem("Move down", string.Empty, false, has && ctx.t._selectedFamilyElement < ctx.sources.Count - 1))
                    ctx.t._selectedFamilyElement = presets.Move(ctx.family, ctx.t._selectedFamilyElement, 1);
                if (ImGui.MenuItem("Remove", string.Empty, false, has)) presets.Remove(ctx.family, ctx.t._selectedFamilyElement);

                ImGui.Separator();

                if (ImGui.MenuItem("Copy to other families...", string.Empty))
                    ctx.t._copyFromFamily = ctx.family;

                if (ImGui.MenuItem("Remove all (use the game's fonts)", string.Empty, false, ctx.sources.Count != 0))
                {
                    presets.Clear(ctx.family);
                    ctx.t._selectedFamilyElement = -1;
                }
            });

            t.DrawCopyConfirmation();

            using var table = ImRaii.Table("##sources", 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY, new(0, 0));
            if (!table) return;

            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Stretch", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("Style", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableHeadersRow();

            for (var i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var enabled = source.Enabled;
                if (ImGui.Checkbox($"##enabled{i}", ref enabled)) presets.SetEnabled(family, i, enabled);

                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{(source.Type == FontSourceType.Preset ? "Preset" : "System font")}###source{i}", t._selectedFamilyElement == i,
                        ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap)) t._selectedFamilyElement = i;
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    t._selectedFamilyElement = i;
                    t.PickAnother(family, i, source);
                }

                if (ImGui.IsItemHovered() && source.Type == FontSourceType.Preset)
                    ImGui.SetTooltip(source.Path);

                if (ImGui.BeginDragDropSource())
                {
                    t._draggedFamilyElement = i;
                    ImGui.SetDragDropPayload("FontSource", [1]);
                    ImGui.TextUnformatted(GetSourceName(source));
                    ImGui.EndDragDropSource();
                }

                if (ImGui.BeginDragDropTarget())
                {
                    if (!ImGui.AcceptDragDropPayload("FontSource").IsNull && t._draggedFamilyElement is { } from && from != i)
                        t._selectedFamilyElement = presets.Move(family, from, i - from);
                    ImGui.EndDragDropTarget();
                }

                Ig.PopupContextItem($"##sourceMenu{i}", (t, i, family, source), static ctx => {
                    ctx.t._selectedFamilyElement = ctx.i;
                    var presets = ctx.t._plugin.Presets;
                    if (ImGui.MenuItem("Remove", string.Empty)) presets.Remove(ctx.family, ctx.i);

                    ImGui.Separator();

                    switch (ctx.source.Type)
                    {
                        case FontSourceType.SystemFont:
                        {
                            if (ImGui.MenuItem("Monospaced digits", string.Empty, ctx.source.MonospacedDigits))
                                presets.SetMonospacedDigits(ctx.family, ctx.i, !ctx.source.MonospacedDigits);
                            break;
                        }
                        case FontSourceType.Preset:
                        {
                            if (ImGui.MenuItem("Show in Explorer", string.Empty, false, File.Exists(ctx.source.Path)))
                                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{ctx.source.Path}\"") { UseShellExecute = true });
                            break;
                        }
                    }
                });

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(GetSourceName(source));

                ImGui.TableNextColumn();
                t.DrawAttributeCell(family, i, source, "weight", f => f.Weight, LookupStruct.DescribeWeight, (f, v) => f with { Weight = v });

                ImGui.TableNextColumn();
                t.DrawAttributeCell(family, i, source, "stretch", f => f.Stretch, LookupStruct.DescribeStretch, (f, v) => f with { Stretch = v });

                ImGui.TableNextColumn();
                t.DrawAttributeCell(family, i, source, "style", f => f.Style, LookupStruct.DescribeStyle, (f, v) => f with { Style = v });
            }
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

        if (t._pendingEdge is { } pending && !active)
        {
            t._pendingEdge = null;
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
            if (Ig.Radios("Subpixel layout", ref layout, "Follow Windows ClearType settings", "RGB", "BGR"))
            {
                mode = SubpixelMode.System + layout;
                modeChanged = true;
            }
        }

        var own = mode is SubpixelMode.Rgb or SubpixelMode.Bgr;
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
            Tooltip("Darkens partly covered pixels, making strokes look heavier. 0 leaves them as they are.");
            changed |= ImGui.SliderFloat("Gamma", ref gamma, 1, 3, "%.2f");
            active |= ImGui.IsItemActive();
            Tooltip("The display's gamma, which blending corrects for. Higher values make light text heavier and dark text lighter.");
        }

        if (changed) t._pendingClearType = new SubpixelRenderingParameters(level, contrast, gamma).Clamped();

        if (t._pendingClearType is { } pendingClearType && !active)
        {
            t._pendingClearType = null;
            configuration.ClearType = pendingClearType;
            save = true;
            Plugin.Framework.RunOnTick(t._plugin.ApplySubpixel);
        }

        if (save) Plugin.PluginInterface.SavePluginConfig(configuration);
    }

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }

    private static void DrawAdvancedTab(ConfigWindow t)
    {
        if (t._plugin.NamePlates is { } namePlates)
        {
            var mode = (int)namePlates.Mode;
            if (Ig.Radios("Nameplate text rendering", ref mode, "None", "Once", "Live"))
                Plugin.Framework.RunOnTick(() => namePlates.Mode = (NamePlateMode)mode);
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

    private void DrawAttributeCell(
        string family,
        int index,
        FontSource source,
        string kind,
        Func<FamilyFont, int> get,
        Func<int, string> describe,
        Func<FamilyFont, int, FamilyFont> set)
    {
        if (source.Font is not { } font)
            return;

        var popupId = $"##{kind}Menu{index}";
        if (ImGui.Selectable($"{Describe(get(font))}###{kind}{index}"))
            ImGui.OpenPopup(popupId);

        using var popup = ImRaii.Popup(popupId);
        if (!popup)
            return;

        var current = get(font);
        var values = new SortedSet<int> { current };
        if (FindSystemFamily(font.Name) is { } systemFamily)
        {
            foreach (var id in systemFamily.Fonts.OfType<SystemFontId>())
                values.Add(kind switch { "weight" => id.Weight, "stretch" => id.Stretch, _ => id.Style });
        }

        foreach (var value in values)
        {
            if (ImGui.MenuItem(Describe(value), string.Empty, value == current) && value != current)
                this._plugin.Presets.Replace(family, index, source with { Font = set(font, value) });
        }

        return;

        string Describe(int value) => $"{value} {ToTitle(describe(value))}";
    }

    private void PickAnother(string family, int index, FontSource source)
    {
        var presets = this._plugin.Presets;
        if (source.Type == FontSourceType.SystemFont)
        {
            this.OpenFontChooser($"Change the system font for {family}", source.Font, font => presets.Replace(family, index, source with { Font = font }));
            return;
        }

        this._fileDialog.OpenFileDialog(
            $"Change the preset for {family}",
            "FontChanger presets{.json},All files{.*}",
            (ok, paths) => {
                if (ok && paths.Count != 0)
                    presets.Replace(family, index, source with { Path = Path.GetFullPath(paths[0]) });
            },
            1);
    }

    private void DrawCopyConfirmation()
    {
        const string Id = "Copy to other families##confirmCopy";
        if (this._copyFromFamily is not null && !ImGui.IsPopupOpen(Id))
            ImGui.OpenPopup(Id);

        var open = true;
        using var popup = ImRaii.PopupModal(Id, ref open, ImGuiWindowFlags.AlwaysAutoResize);
        if (!popup)
        {
            this._copyFromFamily = null;
            return;
        }

        ImGui.TextUnformatted($"Replace the sources of all other families with those of {this._copyFromFamily}?");
        if (ImGui.Button("Yes"))
        {
            if (this._copyFromFamily is { } from)
                this._plugin.Presets.CopyToOthers(from);
            this._copyFromFamily = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("No"))
        {
            this._copyFromFamily = null;
            ImGui.CloseCurrentPopup();
        }
    }

    private void OpenFontChooser(string title, FamilyFont? current, Action<FamilyFont> apply)
    {
        if (this._fontChooser is not null)
            return;

        var dialog = SingleFontChooserDialog.CreateAuto((UiBuilder)Plugin.PluginInterface.UiBuilder);
        dialog.Title = title;
        dialog.FontFamilyExcludeFilter = x => x is not SystemFontFamilyId;
        if (!TryFindSystemFont(current, out var currentId))
            TryFindSystemFont(new() { Name = "Segoe UI" }, out currentId);
        dialog.SelectedFont = new() { FontId = currentId!, SizePx = 24 };
        this._fontChooser = new(dialog, apply);
    }

    private void DrawFontChooser()
    {
        if (this._fontChooser is not { } open || !open.Dialog.ResultTask.IsCompleted)
            return;

        this._fontChooser = null;
        var result = open.Dialog.ResultTask;
        if (result.IsCompletedSuccessfully && ToFamilyFont(result.Result) is { } font)
            open.Apply(font);
    }

    private static string GetSourceName(FontSource source) =>
        source.Type == FontSourceType.Preset ? source.Path : source.Font?.Name ?? string.Empty;

    private static string ToTitle(string name) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Replace('_', ' ').ToLowerInvariant());

    private static FamilyFont? ToFamilyFont(SingleFontSpec spec)
    {
        if (spec.FontId is not SystemFontId id)
            return null;
        var name = id.Family is SystemFontFamilyId familyId ? familyId.EnglishName : id.EnglishName;
        return new() { Name = name, Weight = id.Weight, Stretch = id.Stretch, Style = id.Style };
    }

    private static bool TryFindSystemFont(FamilyFont? font, [NotNullWhen(true)] out IFontId? id)
    {
        if (font is null || FindSystemFamily(font.Name) is not { Fonts.Count: > 0 } family)
        {
            id = null;
            return false;
        }

        id = family.Fonts[family.FindBestMatch(font.Weight, font.Stretch, font.Style)];
        return true;
    }

    private static SystemFontFamilyId? FindSystemFamily(string name) =>
        IFontFamilyId.ListSystemFonts(false)
                     .OfType<SystemFontFamilyId>()
                     .FirstOrDefault(x => string.Equals(x.EnglishName, name, StringComparison.OrdinalIgnoreCase));

    private sealed record FontChooser(SingleFontChooserDialog Dialog, Action<FamilyFont> Apply);
}
