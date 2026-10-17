using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.Utility.Raii;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.PluginInterface;

internal sealed partial class ConfigWindow
{
    private static void DrawFontTab(ConfigWindow t)
    {
        var presets = t._plugin.Presets;

        var systemFallback = presets.SystemFallback;
        if (ImGui.Checkbox("Use System Fallback Fonts", ref systemFallback)) presets.SetSystemFallback(systemFallback);

        if (ImGui.Button("Reload")) presets.Reload();
        ImGui.SameLine();
        ImGui.TextUnformatted(presets.Status);

        t.DrawFontChooser();

        var families = FontNames.Families;
        if (t._selectedFamily is null || !families.Contains(t._selectedFamily))
            t._selectedFamily = families.Count != 0 ? families[0] : null;

        var height = Math.Max(ImGui.GetContentRegionAvail().Y, ImGui.GetFrameHeightWithSpacing() * 6);
        Ig.Child("##families", new(ImGui.GetFontSize() * 11, height), true, t, static t => {
            var presets = t._plugin.Presets;
            foreach (var f in FontNames.Families)
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
                    ctx.t._fileDialog.OpenFileDialog($"Add presets for {ctx.family}", PresetFilter, (ok, paths) => {
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
                if (ImGui.Checkbox($"##enabled{i}", ref enabled)) presets.Replace(family, i, source with { Enabled = enabled });

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
                                presets.Replace(ctx.family, ctx.i, ctx.source with { MonospacedDigits = !ctx.source.MonospacedDigits });
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
        {
            this._systemFamilies = null;
            ImGui.OpenPopup(popupId);
        }

        using var popup = ImRaii.Popup(popupId);
        if (!popup)
            return;

        var current = get(font);
        var values = new SortedSet<int> { current };
        if (this.FindSystemFamily(font.Name) is { } systemFamily)
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
            PresetFilter,
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

    private static string GetSourceName(FontSource source) =>
        source.Type == FontSourceType.Preset ? source.Path : source.Font?.Name ?? string.Empty;

    private static string ToTitle(string name) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Replace('_', ' ').ToLowerInvariant());
}
