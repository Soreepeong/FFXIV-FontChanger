using System;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.Utility.Raii;

using FontChanger.DalamudPlugin.Fonts.Enums;

namespace FontChanger.DalamudPlugin.PluginInterface;

internal sealed partial class ConfigWindow
{
    private static void DrawNamePlateTab(ConfigWindow t)
    {
        if (t._plugin.NamePlates is null)
        {
            ImGui.TextWrapped("Nameplate text isn't drawn by the plugin in this version of the game.");
            return;
        }

        if (t._selectedFamily is null || !(TextFaceNames.TryGetFace(t._selectedFamily, out var face) && TextFaceNames.IsNamePlates(face)))
            t.SelectFamily(TextFaceNames.NamePlates);

        t.DrawFamiliesAndChain(static t => t.DrawNamePlateList());
    }

    private void DrawNamePlateList()
    {
        this.DrawFamilyItem(TextFaceNames.NamePlates, "All nameplates");
        this.DrawFamilyItem(TextFaceNames.NamePlateTitles, "All titles");

        ImGui.Separator();
        ImGui.TextDisabled("By kind");
        foreach (var (kind, label) in TextFaceNames.NamePlateKinds)
            this.DrawFamilyItem(TextFaceNames.NameOf(TextFaceNames.KindFace(kind)), ToTitle(label));

        ImGui.Separator();
        ImGui.TextDisabled("By character");
        var titleWidth = ImGui.CalcTextSize("Title  (0)").X;
        foreach (var rule in this._plugin.Configuration.NamePlateRules)
        {
            var nameWidth = Math.Max(ImGui.GetContentRegionAvail().X - titleWidth - ImGui.GetStyle().ItemSpacing.X, ImGui.GetFontSize());
            this.DrawFamilyItem(rule, rule[TextFaceNames.NamePlateRulePrefix.Length..], nameWidth);
            ImGui.SameLine();
            this.DrawFamilyItem(TextFaceNames.NamePlateRuleTitleName(rule), "Title", titleWidth);
        }

        if (ImGui.SmallButton("Add a character..."))
            ImGui.OpenPopup("##addNamePlateRule");
        this.DrawAddNamePlateRule();
    }

    private void DrawAddNamePlateRule()
    {
        using var popup = ImRaii.Popup("##addNamePlateRule");
        if (!popup)
            return;

        ImGui.TextUnformatted("Nameplates of a character in fonts of their own");
        ImGui.InputText("Name", ref this._newRuleName, 64);
        ImGui.InputText("Home world", ref this._newRuleWorld, 32);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Empty for an NPC, or for a player of any world.");
        if (ImGui.Button("From target") && Plugin.TargetManager.Target is { } target)
        {
            this._newRuleName = target.Name.TextValue;
            this._newRuleWorld = target is IPlayerCharacter pc && pc.HomeWorld.ValueNullable is { } world ? world.Name.ExtractText() : string.Empty;
        }

        ImGui.SameLine();
        var name = this._newRuleName.Trim();
        using (ImRaii.Disabled(name.Length == 0))
        {
            if (ImGui.Button("Add"))
            {
                var rule = TextFaceNames.NamePlateRuleName(name, this._newRuleWorld.Trim());
                var configuration = this._plugin.Configuration;
                if (!configuration.NamePlateRules.Contains(rule, StringComparer.OrdinalIgnoreCase))
                {
                    configuration.NamePlateRules = configuration.NamePlateRules.Add(rule);
                    this.SaveConfiguration();
                }

                Plugin.RegisterNamePlateRule(rule);
                this.SelectFamily(rule);
                this._newRuleName = this._newRuleWorld = string.Empty;
                ImGui.CloseCurrentPopup();
            }
        }
    }

    private void RemoveNamePlateRule(string rule)
    {
        var configuration = this._plugin.Configuration;
        configuration.NamePlateRules = configuration.NamePlateRules.RemoveAll(r => string.Equals(r, rule, StringComparison.OrdinalIgnoreCase));
        this.SaveConfiguration();
        this._plugin.Presets.Clear(rule);
        this._plugin.Presets.Clear(TextFaceNames.NamePlateRuleTitleName(rule));
        this.SelectFamily(TextFaceNames.NamePlates);
    }
}
