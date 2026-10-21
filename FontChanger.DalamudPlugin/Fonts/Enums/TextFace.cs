using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using FFXIVClientStructs.FFXIV.Client.UI;

namespace FontChanger.DalamudPlugin.Fonts.Enums;

/// <summary>Values from <see cref="TextFaceNames.FirstNamePlateRule"/> are the nameplate rules', made as their names are first seen.</summary>
internal enum TextFace
{
    Default,
    NamePlate,
    NamePlateTitle,
    ChatTab1,
    ChatTab2,
    ChatTab3,
    ChatTab4,

    /// <summary>Plus a <see cref="UIObjectKind"/>.</summary>
    NamePlateKind = 20,
}

/// <summary>A face's name is the settings' key of its sources among the families', which no game font family is named.</summary>
internal static class TextFaceNames
{
    public const int ChatTabCount = 4;
    public const string NamePlates = "Nameplates";
    public const string NamePlateTitles = "Nameplate titles";
    public const string NamePlateRulePrefix = "Nameplate: ";
    public const string NamePlateRuleTitlePrefix = "Nameplate title: ";
    public const TextFace FirstNamePlateRule = (TextFace)100;

    public static readonly IReadOnlyList<(UIObjectKind Kind, string Label)> NamePlateKinds =
    [
        (UIObjectKind.PlayerCharacter, "player characters"),
        (UIObjectKind.EventNpcCompanion, "event NPCs and companions"),
        (UIObjectKind.Retainer, "retainers"),
        (UIObjectKind.BattleNpcEnemy, "enemies"),
        (UIObjectKind.BattleNpcFriendly, "friendly battle NPCs"),
        (UIObjectKind.EventObject, "event objects"),
        (UIObjectKind.Treasure, "treasure"),
        (UIObjectKind.GatheringPoint, "gathering points"),
        (UIObjectKind.BattleNpcSubkind6, "battle NPCs of sub-kind 6"),
        (UIObjectKind.Other, "others"),
    ];

    /// <summary>The faces other than the rules', in order.</summary>
    public static readonly IReadOnlyList<(TextFace Face, string Name)> Faces =
    [
        (TextFace.NamePlate, NamePlates),
        (TextFace.NamePlateTitle, NamePlateTitles),
        .. NamePlateKinds.Select(k => (KindFace(k.Kind), $"{NamePlates}: {k.Label}")),
        .. Enumerable.Range(0, ChatTabCount).Select(i => (ChatTab(i), $"Chat tab {i + 1}")),
    ];

    private static readonly ConcurrentDictionary<string, TextFace> ByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<TextFace, string> ByFace = new();
    private static int _ruleCount;

    static TextFaceNames()
    {
        foreach (var (face, name) in Faces)
            (ByName[name], ByFace[face]) = (face, name);
    }

    public static TextFace ChatTab(int tab) => TextFace.ChatTab1 + tab;

    public static int ChatTabOf(TextFace face) => face is >= TextFace.ChatTab1 and <= TextFace.ChatTab4 ? face - TextFace.ChatTab1 : -1;

    public static TextFace KindFace(UIObjectKind kind) => (byte)kind < NamePlateKinds.Count ? TextFace.NamePlateKind + (byte)kind : TextFace.NamePlate;

    public static bool IsNamePlateKind(TextFace face) => face >= TextFace.NamePlateKind && face < TextFace.NamePlateKind + NamePlateKinds.Count;

    public static bool IsNamePlateRule(TextFace face) => face >= FirstNamePlateRule;

    public static bool IsNamePlateRuleTitle(TextFace face) =>
        IsNamePlateRule(face) && NameOf(face).StartsWith(NamePlateRuleTitlePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>These share an atlas, as a plate's title is drawn with the textures of the font the plate's text picked.</summary>
    public static bool IsNamePlates(TextFace face) =>
        face is TextFace.NamePlate or TextFace.NamePlateTitle || IsNamePlateKind(face) || IsNamePlateRule(face);

    /// <summary>Whose fonts a face's text takes for the game fonts the face has none of.</summary>
    public static TextFace FallbackOf(TextFace face) =>
        IsNamePlateRuleTitle(face) ? TextFace.NamePlateTitle
        : face != TextFace.NamePlate && IsNamePlates(face) ? TextFace.NamePlate
        : TextFace.Default;

    public static string NamePlateRuleName(string characterName, string world) =>
        world.Length == 0 ? $"{NamePlateRulePrefix}{characterName}" : $"{NamePlateRulePrefix}{characterName}@{world}";

    public static string NamePlateRuleTitleName(string rule) => NamePlateRuleTitlePrefix + rule[NamePlateRulePrefix.Length..];

    /// <summary>Only finds rules seen by now, by <see cref="TryGetFace"/>.</summary>
    public static bool TryGetNamePlateRule(string rule, out TextFace face) => ByName.TryGetValue(rule, out face) && IsNamePlateRule(face);

    public static string NameOf(TextFace face) => ByFace[face];

    /// <summary>False for a game font family.</summary>
    public static bool TryGetFace(string family, out TextFace face)
    {
        if (ByName.TryGetValue(family, out face))
            return true;
        if (!IsRuleName(family, NamePlateRulePrefix) && !IsRuleName(family, NamePlateRuleTitlePrefix))
            return false;
        face = ByName.GetOrAdd(family, static name => {
            var made = FirstNamePlateRule + Interlocked.Increment(ref _ruleCount) - 1;
            ByFace[made] = name;
            return made;
        });
        return true;

        static bool IsRuleName(string family, string prefix) =>
            family.Length > prefix.Length && family.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
