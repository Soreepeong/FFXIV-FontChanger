using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace CustomFonts;

/// <summary>
/// Names the game's fonts as presets do (<c>AXIS_12</c>, <c>TrumpGothic_184</c>).
/// </summary>
/// <remarks>
/// The fonts load from fixed tables of 41 entries { u64 texture count, char* texture name format, char* FDT name }
/// (the FontTables signature): one for the game, and two for the lobby, picked by a module setting that isn't kept.
/// Lobby fonts (their textures are <c>font_lobby%d.tex</c>) map to the faces of the game's (FontChanger's
/// exportMapFontLobbyToFont); the lobby table is the one whose sizes match the fonts.
/// </remarks>
internal static unsafe class GameFontNames
{
    private static int entryCount;
    private static string[]? gameNames;
    private static string[]? lobbyNamesA;
    private static string[]? lobbyNamesB;

    /// <summary>Gets the game's font families (<c>AXIS</c>, <c>JupiterN</c>, ...) in the order of its font table; none if it wasn't read.</summary>
    public static IReadOnlyList<string> Families { get; private set; } = [];

    /// <summary>Reads the tables. Throws if they can't be found.</summary>
    public static void Initialize()
    {
        nint lobbyA = 0, lobbyB = 0, game = 0;
        int entrySize = 0, fdtName = 0;
        GameLayout.Resolve("Naming the game's fonts", () =>
        {
            entryCount = GameLayout.Get("FontTable.Count");

            // Before 6.30, entries are indexed as i * 3 * 8.
            entrySize = GameLayout.Get("FontTableEntry");
            fdtName = GameLayout.Get("FontTableEntry.FdtName");
            lobbyA = GameLayout.Address("FontTables", "LobbyTableA");
            lobbyB = GameLayout.Address("FontTables", "LobbyTableB");
            game = GameLayout.Address("FontTables", "GameTable");
            GameUi.ResolveResourceHandles();
        });

        lobbyNamesA = Read(lobbyA, entrySize, fdtName);
        lobbyNamesB = Read(lobbyB, entrySize, fdtName);
        gameNames = Read(game, entrySize, fdtName);
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
        var count = Math.Min((int)manager->FontCount, entryCount);
        if (index < 0 || index >= count)
            return null;
        if (!IsLobby(font))
            return gameNames[index];

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

        return (b > a ? lobbyNamesB! : lobbyNamesA!)[index];
    }

    private static bool IsLobby(GameFont* font)
    {
        var handle = font->GetTextureResourceHandle(0);
        return handle != 0 && GameUi.GetFileName(handle).Contains("font_lobby", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gets the size of a game font by its name (FontChanger.FixedSizeFont's data/game_fonts.json); 0 if it isn't one.</summary>
    private static float SizeOf(string name) => GameFonts.Find(name)?.Size ?? 0;

    /// <summary>Reads a table's FDT names as face names: without the extension, nor the lobby suffix.</summary>
    private static string[] Read(nint table, int entrySize, int fdtNameOffset)
    {
        var names = new string[entryCount];
        for (var i = 0; i < entryCount; i++)
        {
            var name = Marshal.PtrToStringUTF8(*(nint*)(table + (i * entrySize) + fdtNameOffset)) ?? string.Empty;
            if (name.EndsWith(".fdt", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            if (name.EndsWith("_lobby", StringComparison.OrdinalIgnoreCase))
                name = name[..^6];
            names[i] = name;
        }

        return names;
    }
}
