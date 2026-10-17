using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

using Dalamud.Interface;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.ImGuiFontChooserDialog;

using FontChanger.DalamudPlugin.Configuration;

namespace FontChanger.DalamudPlugin.PluginInterface;

// Choosing a system font, and finding the system's fonts by family.
internal sealed partial class ConfigWindow
{
    private void OpenFontChooser(string title, FamilyFont? current, Action<FamilyFont> apply)
    {
        if (this._fontChooser is not null)
            return;

        var dialog = SingleFontChooserDialog.CreateAuto((UiBuilder)Plugin.PluginInterface.UiBuilder);
        dialog.Title = title;
        dialog.FontFamilyExcludeFilter = x => x is not SystemFontFamilyId;
        this._systemFamilies = null;
        if (!this.TryFindSystemFont(current, out var currentId))
            this.TryFindSystemFont(new() { Name = "Segoe UI" }, out currentId);
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

    private static FamilyFont? ToFamilyFont(SingleFontSpec spec)
    {
        if (spec.FontId is not SystemFontId id)
            return null;
        var name = id.Family is SystemFontFamilyId familyId ? familyId.EnglishName : id.EnglishName;
        return new() { Name = name, Weight = id.Weight, Stretch = id.Stretch, Style = id.Style };
    }

    private bool TryFindSystemFont(FamilyFont? font, [NotNullWhen(true)] out IFontId? id)
    {
        if (font is null || this.FindSystemFamily(font.Name) is not { Fonts.Count: > 0 } family)
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

    private sealed record FontChooser(SingleFontChooserDialog Dialog, Action<FamilyFont> Apply);
}
