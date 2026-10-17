using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;

using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

namespace FontChanger.DalamudPlugin.Game;

/// <summary>
/// Names the game's fonts as presets do (<c>AXIS_12</c>, <c>TrumpGothic_184</c>).
/// </summary>
/// <remarks>
/// The fonts load from fixed tables: one for the game, and two for the lobby, picked by a module setting that isn't kept.
/// Lobby fonts (their textures are <c>font_lobby%d.tex</c>) map to the faces of the game's (FontChanger's
/// exportMapFontLobbyToFont); the lobby table is the one whose sizes match the fonts.
/// </remarks>
internal static unsafe class FontNames
{
    private static string[]? _gameNames;
    private static string[]? _lobbyNamesA;
    private static string[]? _lobbyNamesB;

    public static IReadOnlyList<string> Families { get; private set; } = [];

    public static void Initialize()
    {
        _lobbyNamesA = Read(AtkFontManager.LobbyFontTableStandardResolution());
        _lobbyNamesB = Read(AtkFontManager.LobbyFontTableHighResolution());
        _gameNames = Read(AtkFontManager.GameFontTable());
        Families = _gameNames.Select(Preset.FamilyOf).Where(f => f.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<(string Name, float Size)> FaceNamesOf(string family) =>
        (_gameNames ?? [])
        .Where(n => string.Equals(Preset.FamilyOf(n), family, StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(n => (n, SizeFromFdtFileName(n)))
        .Where(f => f.Item2 > 0)
        .ToList();

    public static bool TryGetFaceName(AtkFontManager.Font* font, [NotNullWhen(true)] out string? faceName)
    {
        faceName = null;
        if (!TryGetManager(out var manager, out var count))
            return false;
        var index = (int)(font - manager->Fonts);
        if (index < 0 || index >= count)
            return false;
        string[]? lobbyNames = null;
        faceName = NameAt(manager, index, count, ref lobbyNames);
        return true;
    }

    /// <summary>Gets the font of the font manager of a face name; false if none is loaded.</summary>
    public static bool TryFindFont(string faceName, out Pointer<AtkFontManager.Font> font)
    {
        font = null;
        if (!TryGetManager(out var manager, out var count))
            return false;
        string[]? lobbyNames = null;
        for (var i = 0; i < count; i++)
        {
            if (string.Equals(NameAt(manager, i, count, ref lobbyNames), faceName, StringComparison.OrdinalIgnoreCase))
            {
                font = manager->Fonts + i;
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets the font manager and how many of its fonts the tables name; false if the tables weren't read or it has no fonts.</summary>
    private static bool TryGetManager(out AtkFontManager* manager, out int count)
    {
        manager = AtkStage.Instance()->AtkFontManager;
        count = Math.Min((int)manager->FontCount, AtkFontManager.FontTableEntry.Count);
        return _gameNames is not null && manager->Fonts is not null;
    }

    /// <summary>Gets the face name of a font of the manager: a lobby font's from the lobby table, found once.</summary>
    private static string NameAt(AtkFontManager* manager, int index, int count, ref string[]? lobbyNames) =>
        IsLobby(manager->Fonts + index) ? (lobbyNames ??= LobbyNames(manager, count))[index] : _gameNames![index];

    /// <summary>Gets the lobby table whose sizes match the loaded lobby fonts.</summary>
    private static string[] LobbyNames(AtkFontManager* manager, int count)
    {
        var a = 0;
        var b = 0;
        for (var i = 0; i < count; i++)
        {
            var f = manager->Fonts + i;
            if (!IsLobby(f))
                continue;
            a += SizeFromFdtFileName(_lobbyNamesA![i]) == f->Size ? 1 : 0;
            b += SizeFromFdtFileName(_lobbyNamesB![i]) == f->Size ? 1 : 0;
        }

        return b > a ? _lobbyNamesB! : _lobbyNamesA!;
    }

    private static bool IsLobby(AtkFontManager.Font* font)
    {
        var handle = (ResourceHandle*)font->TextureResourceHandles[0].Value;
        if (handle is null || handle->FileName.Length is 0 or > 4096)
            return false;
        var fileName = Encoding.UTF8.GetString(handle->FileName.AsSpan(0, (int)handle->FileName.Length));
        return fileName.Contains("font_lobby", StringComparison.OrdinalIgnoreCase);
    }

    private static float SizeFromFdtFileName(string name) => GameFonts.TryFind(name, out var font) ? font.Size : 0;

    private static string[] Read(AtkFontManager.FontTableEntry* table)
    {
        var names = new string[AtkFontManager.FontTableEntry.Count];
        for (var i = 0; i < AtkFontManager.FontTableEntry.Count; i++)
        {
            var name = table[i].FdtName.ToString();
            if (name.EndsWith(".fdt", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            if (name.EndsWith("_lobby", StringComparison.OrdinalIgnoreCase))
                name = name[..^6];
            names[i] = name;
        }

        return names;
    }
}
