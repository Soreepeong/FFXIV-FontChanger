using System;
using System.Globalization;
using System.Runtime.InteropServices;

using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace CustomFonts;

/// <summary>
/// Names the game's fonts as presets do (<c>AXIS_12</c>, <c>TrumpGothic_184</c>).
/// </summary>
/// <remarks>
/// The fonts load from fixed tables of 41 entries { u64 texture count, char* texture name format, char* FDT name }
/// (FUN_1406ADBF0, AtkModule vf43): one for the game, and two for the lobby, picked by a module setting that isn't kept.
/// Lobby fonts (their textures are <c>font_lobby%d.tex</c>) map to the faces of the game's (FontChanger's
/// exportMapFontLobbyToFont); the lobby table is the one whose sizes match the fonts.
/// </remarks>
internal static unsafe class GameFontNames
{
    // In AtkModule vf43: CMP [R13+0x280], 1; LEA RSI, lobbyTableA; LEA RAX, lobbyTableB; CMOVNZ RSI, RAX; JMP; LEA RSI, table.
    // Unique in 7.56h.
    private const string TablesSignature =
        "41 83 BD 80 02 00 00 01 48 8D 35 ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ?? 48 0F 45 F0 EB 07 48 8D 35 ?? ?? ?? ??";

    private const int EntryCount = 41;
    private const int EntrySize = 0x18;
    private const int FdtNameOffset = 0x10;

    private static string[]? gameNames;
    private static string[]? lobbyNamesA;
    private static string[]? lobbyNamesB;

    /// <summary>Reads the tables. Throws if they can't be found.</summary>
    public static void Initialize()
    {
        var p = Plugin.SigScanner.ScanText(TablesSignature);
        lobbyNamesA = Read(p + 15 + *(int*)(p + 11));
        lobbyNamesB = Read(p + 22 + *(int*)(p + 18));
        gameNames = Read(p + 35 + *(int*)(p + 31));
    }

    /// <summary>Gets the face name of a font of the font manager; null if it isn't one.</summary>
    public static string? GetFaceName(GameFont* font)
    {
        var manager = AtkFontManagerExtras.Instance();
        if (gameNames is null || manager is null || manager->Fonts is null)
            return null;
        var index = font - manager->Fonts;
        var count = Math.Min((int)manager->FontCount, EntryCount);
        if (index < 0 || index >= count)
            return null;
        if (!IsLobby(font))
            return gameNames[index];

        var a = 0;
        var b = 0;
        for (var i = 0; i < count; i++)
        {
            var f = &manager->Fonts[i];
            if (!IsLobby(f))
                continue;
            a += SizeOf(lobbyNamesA![i]) == f->Size ? 1 : 0;
            b += SizeOf(lobbyNamesB![i]) == f->Size ? 1 : 0;
        }

        return (b > a ? lobbyNamesB! : lobbyNamesA!)[index];
    }

    private static bool IsLobby(GameFont* font)
    {
        var handle = (ResourceHandle*)font->TextureResourceHandles[0].Value;
        return handle is not null && handle->FileName.ToString().Contains("font_lobby", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gets the size a font name stands for: the number after the last underscore, in tenths for 96 and 184.</summary>
    private static float SizeOf(string name)
    {
        var i = name.LastIndexOf('_');
        if (i < 0 || !int.TryParse(name.AsSpan(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return 0;
        return n is 96 or 184 ? n / 10f : n;
    }

    /// <summary>Reads a table's FDT names as face names: without the extension, nor the lobby suffix.</summary>
    private static string[] Read(nint table)
    {
        var names = new string[EntryCount];
        for (var i = 0; i < EntryCount; i++)
        {
            var name = Marshal.PtrToStringUTF8(*(nint*)(table + (i * EntrySize) + FdtNameOffset)) ?? string.Empty;
            if (name.EndsWith(".fdt", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            if (name.EndsWith("_lobby", StringComparison.OrdinalIgnoreCase))
                name = name[..^6];
            names[i] = name;
        }

        return names;
    }
}
