using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace CustomFonts;

/// <summary>
/// Names the game's fonts as presets do (<c>AXIS_12</c>, <c>TrumpGothic_184</c>).
/// </summary>
/// <remarks>
/// The fonts load from fixed tables: one for the game, and two for the lobby, picked by a module setting that isn't kept.
/// Lobby fonts (their textures are <c>font_lobby%d.tex</c>) map to the faces of the game's (FontChanger's
/// exportMapFontLobbyToFont); the lobby table is the one whose sizes match the fonts.
/// </remarks>
internal static unsafe class GameFontNames
{
    private static string[]? gameNames;
    private static string[]? lobbyNamesA;
    private static string[]? lobbyNamesB;

    /// <summary>Gets the game's font families (<c>AXIS</c>, <c>JupiterN</c>, ...) in the order of its font table; none if it wasn't read.</summary>
    public static IReadOnlyList<string> Families { get; private set; } = [];

    /// <summary>Reads the tables. Throws if they can't be found.</summary>
    public static void Initialize()
    {
        lobbyNamesA = Read(GameFontTables.LobbyTableA());
        lobbyNamesB = Read(GameFontTables.LobbyTableB());
        gameNames = Read(GameFontTables.GameTable());
        Families = gameNames.Select(Preset.FamilyOf).Where(f => f.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Gets the faces of a family in the game's font table, with the sizes their names stand for.</summary>
    public static IReadOnlyList<(string Name, float Size)> FacesOf(string family) =>
        (gameNames ?? [])
        .Where(n => string.Equals(Preset.FamilyOf(n), family, StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(n => (n, SizeOf(n)))
        .Where(f => f.Item2 > 0)
        .ToList();

    /// <summary>Gets the face name of a font of the font manager; null if it isn't one.</summary>
    public static string? GetFaceName(GameFont* font)
    {
        var manager = GameFontManager.Instance();
        if (gameNames is null || manager is null || manager->Fonts is null)
            return null;
        var index = manager->IndexOf(font);
        var count = Math.Min((int)manager->FontCount, GameFontTables.EntryCount);
        if (index < 0 || index >= count)
            return null;
        return IsLobby(font) ? LobbyNames(manager, count)[index] : gameNames[index];
    }

    /// <summary>Gets the font of the font manager of a face name; null if none is loaded.</summary>
    public static GameFont* FindFont(string faceName)
    {
        var manager = GameFontManager.Instance();
        if (gameNames is null || manager is null || manager->Fonts is null)
            return null;
        var count = Math.Min((int)manager->FontCount, GameFontTables.EntryCount);
        string[]? lobbyNames = null;
        for (var i = 0; i < count; i++)
        {
            var font = manager->Font(i);
            var lobby = IsLobby(font);
            if (lobby)
                lobbyNames ??= LobbyNames(manager, count);
            if (string.Equals((lobby ? lobbyNames! : gameNames)[i], faceName, StringComparison.OrdinalIgnoreCase))
                return font;
        }

        return null;
    }

    /// <summary>Gets the lobby table whose sizes match the loaded lobby fonts.</summary>
    private static string[] LobbyNames(GameFontManager* manager, int count)
    {
        var a = 0;
        var b = 0;
        for (var i = 0; i < count; i++)
        {
            var f = manager->Font(i);
            if (!IsLobby(f))
                continue;
            a += SizeOf(lobbyNamesA![i]) == f->Size ? 1 : 0;
            b += SizeOf(lobbyNamesB![i]) == f->Size ? 1 : 0;
        }

        return b > a ? lobbyNamesB! : lobbyNamesA!;
    }

    private static bool IsLobby(GameFont* font)
    {
        var handle = font->GetTextureResourceHandle(0);
        return handle != 0 && GameUi.GetFileName((ResourceHandle*)handle).Contains("font_lobby", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gets the size of a game font by its name (FontChanger.FixedSizeFont's data/game_fonts.json); 0 if it isn't one.</summary>
    private static float SizeOf(string name) => GameFonts.Find(name)?.Size ?? 0;

    /// <summary>Reads a table's FDT names as face names: without the extension, nor the lobby suffix.</summary>
    private static string[] Read(GameFontTableEntry* table)
    {
        var names = new string[GameFontTables.EntryCount];
        for (var i = 0; i < GameFontTables.EntryCount; i++)
        {
            var name = Marshal.PtrToStringUTF8((nint)table[i].FdtName) ?? string.Empty;
            if (name.EndsWith(".fdt", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            if (name.EndsWith("_lobby", StringComparison.OrdinalIgnoreCase))
                name = name[..^6];
            names[i] = name;
        }

        return names;
    }
}
