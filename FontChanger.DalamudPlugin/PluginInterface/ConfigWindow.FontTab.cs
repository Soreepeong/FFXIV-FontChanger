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
using Dalamud.Interface.ImGuiFontChooserDialog;
using Dalamud.Interface.Utility.Raii;

using FontChanger.DalamudPlugin.Configuration;
using FontChanger.DalamudPlugin.Game;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

namespace FontChanger.DalamudPlugin.PluginInterface;

internal sealed partial class ConfigWindow
{
    private static readonly FontAttribute[] FontAttributes =
    [
        new("Weight", f => f.Weight, LookupStruct.DescribeWeight, (f, v) => f with { Weight = v }),
        new("Stretch", f => f.Stretch, LookupStruct.DescribeStretch, (f, v) => f with { Stretch = v }),
        new("Style", f => f.Style, LookupStruct.DescribeStyle, (f, v) => f with { Style = v }),
    ];

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
        Ig.Child("##families", new(ImGui.GetFontSize() * 11, height), true, t, static t => t.DrawFamilyList());
        ImGui.SameLine();
        Ig.Child("##chain", new(0, height), true, t, static t => t.DrawChain());
    }

    private void DrawFamilyList()
    {
        var presets = this._plugin.Presets;
        foreach (var f in FontNames.Families)
        {
            var sources = presets.GetSources(f);
            var count = presets.IsFamilyEnabled(f) ? sources.Count(s => s.Enabled) : 0;
            var label = count == 0 ? f : $"{f}  ({count})";
            if (ImGui.Selectable($"{label}###family{f}", string.Equals(this._selectedFamily, f, StringComparison.OrdinalIgnoreCase)) && this._selectedFamily != f)
            {
                this._selectedFamily = f;
                this._selectedFamilyElement = -1;
            }
        }
    }

    private void DrawChain()
    {
        var presets = this._plugin.Presets;
        if (this._selectedFamily is not { } family)
            return;

        var sources = presets.GetSources(family);
        if (this._selectedFamilyElement >= sources.Count)
            this._selectedFamilyElement = sources.Count - 1;

        var familyEnabled = presets.IsFamilyEnabled(family);
        if (ImGui.Checkbox("Enabled", ref familyEnabled))
            presets.SetFamilyEnabled(family, familyEnabled);

        ImGui.SameLine();
        if (ImGui.Button("Edit...###editSources")) ImGui.OpenPopup("##editSources");
        Ig.Popup("##editSources", (this, family, sources), static ctx => {
            var (t, family, sources) = ctx;
            var presets = t._plugin.Presets;
            var has = t._selectedFamilyElement >= 0 && t._selectedFamilyElement < sources.Count;

            if (ImGui.MenuItem("Add presets...", string.Empty))
            {
                t._fileDialog.OpenFileDialog($"Add presets for {family}", PresetFilter, (ok, paths) => {
                    if (ok && paths.Count != 0)
                    {
                        presets.AddPresets(family, paths);
                        t._selectedFamilyElement = presets.GetSources(family).Count - 1;
                    }
                }, 0);
            }

            if (ImGui.MenuItem("Add system font...", string.Empty))
            {
                t.OpenFontChooser($"Add a system font for {family}", null, font => {
                    presets.AddFont(family, font);
                    t._selectedFamilyElement = presets.GetSources(family).Count - 1;
                });
            }

            ImGui.Separator();

            if (ImGui.MenuItem("Change...", string.Empty, false, has))
                t.PickAnother(family, t._selectedFamilyElement, sources[t._selectedFamilyElement]);

            if (ImGui.MenuItem("Move up", string.Empty, false, has && t._selectedFamilyElement > 0))
                t._selectedFamilyElement = presets.Move(family, t._selectedFamilyElement, -1);
            if (ImGui.MenuItem("Move down", string.Empty, false, has && t._selectedFamilyElement < sources.Count - 1))
                t._selectedFamilyElement = presets.Move(family, t._selectedFamilyElement, 1);
            if (ImGui.MenuItem("Remove", string.Empty, false, has)) presets.Remove(family, t._selectedFamilyElement);

            ImGui.Separator();

            if (ImGui.MenuItem("Copy to other families...", string.Empty))
                t._copyFromFamily = family;

            if (ImGui.MenuItem("Remove all (use the game's fonts)", string.Empty, false, sources.Count != 0))
            {
                presets.Clear(family);
                t._selectedFamilyElement = -1;
            }
        });

        this.DrawCopyConfirmation();

        var paneHeight = AdjustmentsPaneHeight;
        if (ImGui.BeginTable("##sources", 7,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY,
                new(0, MathF.Max(ImGui.GetFrameHeightWithSpacing() * 3, ImGui.GetContentRegionAvail().Y - paneHeight - ImGui.GetStyle().ItemSpacing.Y))))
        {
            this.DrawSourceRows(family, sources);
            ImGui.EndTable();
        }

        sources = presets.GetSources(family);
        var selected = this._selectedFamilyElement;
        this.DrawAdjustmentsPane(family, selected, selected >= 0 && selected < sources.Count ? sources[selected] : null, paneHeight);
    }

    private void DrawSourceRows(string family, IReadOnlyList<FontSource> sources)
    {
        var presets = this._plugin.Presets;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Adjustments", ImGuiTableColumnFlags.WidthStretch, 0.6f);
        foreach (var attribute in FontAttributes)
            ImGui.TableSetupColumn(attribute.Name, ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableHeadersRow();

        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var enabled = source.Enabled;
            if (ImGui.Checkbox($"##enabled{i}", ref enabled)) presets.Replace(family, i, source with { Enabled = enabled });

            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{(source.Type == FontSourceType.Preset ? "Preset" : "System font")}###source{i}", this._selectedFamilyElement == i,
                    ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap)) this._selectedFamilyElement = i;
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                this._selectedFamilyElement = i;
                this.PickAnother(family, i, source);
            }

            if (ImGui.IsItemHovered() && source.Type == FontSourceType.Preset)
                ImGui.SetTooltip(source.Path);

            if (ImGui.BeginDragDropSource())
            {
                this._draggedFamilyElement = i;
                ImGui.SetDragDropPayload("FontSource", [1]);
                ImGui.TextUnformatted(GetSourceName(source));
                ImGui.EndDragDropSource();
            }

            if (ImGui.BeginDragDropTarget())
            {
                if (!ImGui.AcceptDragDropPayload("FontSource").IsNull && this._draggedFamilyElement is { } from && from != i)
                    this._selectedFamilyElement = presets.Move(family, from, i - from);
                ImGui.EndDragDropTarget();
            }

            Ig.PopupContextItem($"##sourceMenu{i}", (this, i, family, source), static ctx => {
                var (t, i, family, source) = ctx;
                t._selectedFamilyElement = i;
                if (ImGui.MenuItem("Remove", string.Empty)) t._plugin.Presets.Remove(family, i);

                // A system font's options are in the pane below the grid.
                if (source.Type == FontSourceType.Preset)
                {
                    ImGui.Separator();
                    if (ImGui.MenuItem("Show in Explorer", string.Empty, false, File.Exists(source.Path)))
                        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{source.Path}\"") { UseShellExecute = true });
                }
            });

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GetSourceName(source));

            ImGui.TableNextColumn();
            if (source.Type == FontSourceType.SystemFont && DescribeAdjustments(source.Adjustments) is { Length: > 0 } adjustments)
            {
                ImGui.TextUnformatted(adjustments);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(adjustments);
            }

            foreach (var attribute in FontAttributes)
            {
                ImGui.TableNextColumn();
                this.DrawAttributeCell(family, i, source, attribute);
            }
        }
    }

    private void DrawAttributeCell(string family, int index, FontSource source, FontAttribute attribute)
    {
        if (source.Font is not { } font)
            return;

        var popupId = $"##{attribute.Name}Menu{index}";
        if (ImGui.Selectable($"{Describe(attribute.Get(font))}###{attribute.Name}{index}"))
        {
            this._systemFamilies = null;
            ImGui.OpenPopup(popupId);
        }

        using var popup = ImRaii.Popup(popupId);
        if (!popup)
            return;

        var current = attribute.Get(font);
        var values = new SortedSet<int> { current };
        if (this.FindSystemFamily(font.FamilyName) is { } systemFamily)
        {
            foreach (var id in systemFamily.Fonts.OfType<SystemFontId>())
                values.Add(attribute.Get(ToFamilyFont(id)));
        }

        foreach (var value in values)
        {
            if (ImGui.MenuItem(Describe(value), string.Empty, value == current) && value != current)
                this._plugin.Presets.Replace(family, index, source with { Font = attribute.Set(font, value) });
        }

        return;

        string Describe(int value) => $"{value} {ToTitle(attribute.Describe(value))}";
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

    private void OpenFontChooser(string title, FamilyFont? current, Action<FamilyFont> apply)
    {
        if (this._fontChooser is not null)
            return;

        var dialog = SingleFontChooserDialog.CreateAuto((UiBuilder)Plugin.PluginInterface.UiBuilder);
        dialog.Title = title;
        dialog.FontFamilyExcludeFilter = x => x is not SystemFontFamilyId;
        this._systemFamilies = null;
        if (!this.TryFindSystemFont(current, out var currentId))
            this.TryFindSystemFont(new() { FamilyName = "Segoe UI" }, out currentId);
        dialog.SelectedFont = new() { FontId = currentId!, SizePx = 24 };
        this._fontChooser = new(dialog, apply);
    }

    private void DrawFontChooser()
    {
        if (this._fontChooser is not { } open || !open.Dialog.ResultTask.IsCompleted)
            return;

        this._fontChooser = null;
        var result = open.Dialog.ResultTask;
        if (result.IsCompletedSuccessfully && result.Result.FontId is SystemFontId id)
            open.Apply(ToFamilyFont(id));
    }

    private bool TryFindSystemFont(FamilyFont? font, [NotNullWhen(true)] out IFontId? id)
    {
        if (font is null || this.FindSystemFamily(font.FamilyName) is not { Fonts.Count: > 0 } family)
        {
            id = null;
            return false;
        }

        id = family.Fonts[family.FindBestMatch(font.Weight, font.Stretch, font.Style)];
        return true;
    }

    private SystemFontFamilyId? FindSystemFamily(string name) =>
        (this._systemFamilies ??= IFontFamilyId.ListSystemFonts(false).OfType<SystemFontFamilyId>().ToList())
        .Find(x => string.Equals(x.EnglishName, name, StringComparison.OrdinalIgnoreCase));

    private static FamilyFont ToFamilyFont(SystemFontId id) => new()
    {
        FamilyName = id.Family is SystemFontFamilyId familyId ? familyId.EnglishName : id.EnglishName,
        Weight = id.Weight,
        Stretch = id.Stretch,
        Style = id.Style,
    };

    private static string GetSourceName(FontSource source) =>
        source.Type == FontSourceType.Preset ? source.Path : source.Font?.FamilyName ?? string.Empty;

    private static string ToTitle(string name) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Replace('_', ' ').ToLowerInvariant());

    private sealed record FontChooser(SingleFontChooserDialog Dialog, Action<FamilyFont> Apply);

    private sealed record FontAttribute(string Name, Func<FamilyFont, int> Get, Func<int, string> Describe, Func<FamilyFont, int, FamilyFont> Set);
}
