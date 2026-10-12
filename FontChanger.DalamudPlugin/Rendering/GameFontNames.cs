using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Component.GUI;

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
        lobbyNamesA = Read(AtkFontManager.LobbyFontTableStandardResolution());
        lobbyNamesB = Read(AtkFontManager.LobbyFontTableHighResolution());
        gameNames = Read(AtkFontManager.GameFontTable());
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
    public static string? GetFaceName(Font* font)
    {
        var manager = (AtkFontManager*)AtkStage.Instance()->AtkFontManager;
        if (gameNames is null || manager->Fonts is null)
            return null;
        var index = font - manager->Fonts;
        var count = Math.Min((int)manager->FontCount, FontTableEntry.Count);
        if (index < 0 || index >= count)
            return null;
        return IsLobby(font) ? LobbyNames(manager, count)[index] : gameNames[index];
    }

    /// <summary>Gets the font of the font manager of a face name; null if none is loaded.</summary>
    public static Font* FindFont(string faceName)
    {
        var manager = (AtkFontManager*)AtkStage.Instance()->AtkFontManager;
        if (gameNames is null || manager->Fonts is null)
            return null;
        var count = Math.Min((int)manager->FontCount, FontTableEntry.Count);
        string[]? lobbyNames = null;
        for (var i = 0; i < count; i++)
        {
            var font = manager->Fonts + i;
            var lobby = IsLobby(font);
            if (lobby)
                lobbyNames ??= LobbyNames(manager, count);
            if (string.Equals((lobby ? lobbyNames! : gameNames)[i], faceName, StringComparison.OrdinalIgnoreCase))
                return font;
        }

        return null;
    }

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
            a += SizeOf(lobbyNamesA![i]) == f->Size ? 1 : 0;
            b += SizeOf(lobbyNamesB![i]) == f->Size ? 1 : 0;
        }

        return b > a ? lobbyNamesB! : lobbyNamesA!;
    }

    private static bool IsLobby(Font* font)
    {
        var handle = font->TextureResourceHandles[0].Value;
        return handle is not null && FileName((ResourceHandle*)handle).Contains("font_lobby", StringComparison.OrdinalIgnoreCase);
    }

    // A loaded resource's file name, from the game's std::string; a length out of all reason reads as none.
    private static string FileName(ResourceHandle* handle)
    {
        var length = handle->FileName.Length;
        return length is > 0 and <= 4096
            ? Encoding.UTF8.GetString(handle->FileName.AsSpan(0, (int)length))
            : string.Empty;
    }

    /// <summary>Gets the size of a game font by its name (FontChanger.FixedSizeFont's data/game_fonts.json); 0 if it isn't one.</summary>
    private static float SizeOf(string name) => GameFonts.Find(name)?.Size ?? 0;

    /// <summary>Reads a table's FDT names as face names: without the extension, nor the lobby suffix.</summary>
    private static string[] Read(FontTableEntry* table)
    {
        var names = new string[FontTableEntry.Count];
        for (var i = 0; i < FontTableEntry.Count; i++)
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
