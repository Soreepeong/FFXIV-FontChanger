using System.Collections.Generic;
using System.Text;

using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CustomFonts;

/// <summary>What the plugin reads of the rest of the game's UI: the loaded units, and resource handles' file names.</summary>
internal static unsafe class GameUi
{
    /// <summary>Gets every loaded unit.</summary>
    public static List<nint> GetLoadedUnits()
    {
        var units = new List<nint>();
        var stage = AtkStage.Instance();
        var manager = stage is null ? null : stage->RaptureAtkUnitManager;
        if (manager is null)
            return units;
        ref var list = ref manager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < list.Count; i++)
        {
            var unit = list.Entries[i].Value;
            if (unit is not null)
                units.Add((nint)unit);
        }

        return units;
    }

    /// <summary>Gets a resource handle's file name.</summary>
    public static string GetFileName(ResourceHandle* handle)
    {
        ref var name = ref handle->FileName;
        return name.Length > 4096 ? string.Empty : Encoding.UTF8.GetString(name.AsSpan());
    }
}
