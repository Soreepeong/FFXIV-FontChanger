using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace CustomFonts;

/// <summary>
/// Compares what the game's code says (<see cref="GameLayout"/>) with FFXIVClientStructs, and logs where they differ. The
/// plugin doesn't use FFXIVClientStructs otherwise: XivAlexander implements the same without it, from the same
/// signatures, so this checks them in game.
/// </summary>
internal static unsafe class ClientStructsCheck
{
    private static bool done;

    // Captured offsets, and the FFXIVClientStructs fields (summed along the path) they should equal.
    private static readonly (string Capture, (Type Type, string Field)[] Path)[] Offsets =
    [
        ("AtkStage.AtkFontManager", [(typeof(AtkStage), "AtkFontManager")]),
        ("AtkStage.RaptureAtkUnitManager", [(typeof(AtkStage), "RaptureAtkUnitManager")]),
        ("AtkModule.AtkFontManager", [(typeof(AtkModule), "AtkFontManager")]),
        ("AtkUnitManager.AllLoadedUnitsList.Count", [(typeof(AtkUnitManager), "AllLoadedUnitsList"), (typeof(AtkUnitList), "Count")]),
        ("AtkUnitBase.UldManager", [(typeof(AtkUnitBase), "UldManager")]),
        ("AtkUldManager.NodeList", [(typeof(AtkUldManager), "NodeList")]),
        ("AtkUldManager.NodeListCount", [(typeof(AtkUldManager), "NodeListCount")]),
        ("AtkResNode.Type", [(typeof(AtkResNode), "Type")]),
        ("AtkResNode.ParentNode", [(typeof(AtkResNode), "ParentNode")]),
        ("AtkResNode.ScaleX", [(typeof(AtkResNode), "ScaleX")]),
        ("AtkResNode.ScaleY", [(typeof(AtkResNode), "ScaleY")]),
        ("AtkResNode.Width", [(typeof(AtkResNode), "Width")]),
        ("AtkResNode.Height", [(typeof(AtkResNode), "Height")]),
        ("AtkComponentNode.Component", [(typeof(AtkComponentNode), "Component")]),
        ("AtkComponentBase.UldManager", [(typeof(AtkComponentBase), "UldManager")]),
        ("AtkTextNode.NodeText", [(typeof(AtkTextNode), "NodeText")]),
        ("AtkTextNode.AlignmentFontType", [(typeof(AtkTextNode), "AlignmentFontType")]),
        ("AtkTextNode.FontSize", [(typeof(AtkTextNode), "FontSize")]),
        ("ResourceHandle.FileName", [(typeof(ResourceHandle), "FileName")]),
        ("Texture.D3D11Texture2D", [(typeof(Texture), "D3D11Texture2D")]),
        ("Texture.D3D11ShaderResourceView", [(typeof(Texture), "D3D11ShaderResourceView")]),
        ("AddonNamePlate.BakePlate", [(typeof(AddonNamePlate), "BakePlate")]),
        ("AddonNamePlate.NamePlateObjectArray", [(typeof(AddonNamePlate), "NamePlateObjectArray")]),
        ("BakePlateRenderer.CurrentBakeData", [(typeof(AddonNamePlate.BakePlateRenderer), "CurrentBakeData")]),
        ("BakePlateRenderer.DisableFixedFontResolution", [(typeof(AddonNamePlate.BakePlateRenderer), "DisableFixedFontResolution")]),
        ("BakeData.TextYOffset", [(typeof(AddonNamePlate.BakeData), "TextYOffset")]),
        ("BakeData.Alpha", [(typeof(AddonNamePlate.BakeData), "Alpha")]),
        ("NamePlateObject.NameText", [(typeof(AddonNamePlate.NamePlateObject), "NameText")]),
        ("NamePlateObject.TextW", [(typeof(AddonNamePlate.NamePlateObject), "TextW")]),
        ("NamePlateObject.TextH", [(typeof(AddonNamePlate.NamePlateObject), "TextH")]),
        ("NamePlateObject.NeedsToBeBaked", [(typeof(AddonNamePlate.NamePlateObject), "NeedsToBeBaked")]),
    ];

    /// <summary>
    /// Logs where the captured layouts and instances differ from FFXIVClientStructs'. Once; never throws. A development
    /// check: left out of release builds.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Run()
    {
        if (done)
            return;
        done = true;
        try
        {
            var differences = new List<string>();
            var checkedCount = 0;
            foreach (var (capture, path) in Offsets)
            {
                if (GameLayout.TryGet(capture) is not { } actual || Sum(path) is not { } expected)
                    continue;
                checkedCount++;
                if (actual != expected)
                    differences.Add($"{capture}: 0x{actual:X} by the game's code, 0x{expected:X} by FFXIVClientStructs");
            }

            void Instance(string what, nint actual, nint expected)
            {
                checkedCount++;
                if (actual != expected)
                    differences.Add($"{what}: {actual:X} by the game's code, {expected:X} by FFXIVClientStructs");
            }

            Instance("AtkStage", GameUi.Stage, (nint)AtkStage.Instance());
            Instance("AtkFontManager", (nint)GameFontManager.Instance(), (nint)AtkStage.Instance()->AtkFontManager);
            if (GameLayout.TryGet("AtkModule.AtkFontManager") is { } moduleFontManager)
                Instance("AtkModule", (nint)GameFontManager.Instance() - moduleFontManager, (nint)RaptureAtkModule.Instance());

            if (differences.Count == 0)
                Host.Log.Information("The game's code agrees with FFXIVClientStructs on {Count} offsets and instances", checkedCount);
            else
                Host.Log.Warning("The game's code disagrees with FFXIVClientStructs: {Differences}", string.Join("; ", differences));
        }
        catch (Exception ex)
        {
            Host.Log.Warning(ex, "Comparing with FFXIVClientStructs failed");
        }
    }

    private static int? Sum((Type Type, string Field)[] path)
    {
        var sum = 0;
        foreach (var (type, name) in path)
        {
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetCustomAttributes(typeof(FieldOffsetAttribute), false).FirstOrDefault() is not FieldOffsetAttribute a)
                return null;
            sum += a.Value;
        }

        return sum;
    }
}
