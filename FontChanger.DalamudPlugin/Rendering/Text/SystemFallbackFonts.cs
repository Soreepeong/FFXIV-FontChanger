using System;
using System.Linq;
using System.Runtime.InteropServices;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Rendering.Text;

/// <summary>
/// Finds the system font text no face element has is drawn with: private-use characters in the game's icon font, then the
/// UI fonts of Windows for each script in order, then the system's fallback.
/// </summary>
internal sealed unsafe class SystemFallbackFonts : IDisposable
{
    /// <summary>The game's icon font, for private-use characters before the system's fallback (which would pick Segoe Fluent Icons).</summary>
    private const string IconFamily = "XIV AXIS Std ATK";

    /// <summary>The family fallback fonts are matched against when a face has no font of its own.</summary>
    public const string DefaultBaseFamily = "Segoe UI";

    // Segoe UI, then fonts for scripts it lacks.
    private static readonly string[] UiFamilies =
    [
        "Segoe UI", "Yu Gothic UI", "Malgun Gothic", "Microsoft YaHei UI", "Microsoft JhengHei UI", "Nirmala UI", "Leelawadee UI",
        "Segoe UI Symbol", "Segoe UI Emoji",
    ];

    private readonly FontServices _services;
    private MyComPtr<IDWriteFontFallback1> _fallback;

    public SystemFallbackFonts(FontServices services)
    {
        this._services = services;
        var factory = services.Factory;
        using var builder = default(MyComPtr<IDWriteFontFallbackBuilder>);
        factory->CreateFontFallbackBuilder(builder.GetAddressOf()).ThrowOnError();

        if (services.TryFindFont(LookupStruct.Of(IconFamily), out var icons))
        {
            icons.Dispose();
            var pua = new DWRITE_UNICODE_RANGE { first = 0xE000, last = 0xF8FF };
            AddMapping(builder.Value, pua, [IconFamily]);
        }
        else
        {
            Plugin.Log.Warning("{family} isn't installed; private-use characters are drawn with the system's fallback", IconFamily);
        }

        var installed = UiFamilies.Where(f => {
            if (!services.TryFindFont(LookupStruct.Of(f), out var font))
                return false;
            font.Dispose();
            return true;
        }).ToArray();
        if (installed.Length != 0)
            AddMapping(builder.Value, new() { first = 0, last = 0x10FFFF }, installed);

        using var systemFallback = default(MyComPtr<IDWriteFontFallback>);
        factory->GetSystemFontFallback(systemFallback.GetAddressOf()).ThrowOnError();
        builder.Value->AddMappings(systemFallback.Value).ThrowOnError();
        using var created = default(MyComPtr<IDWriteFontFallback>);
        builder.Value->CreateFontFallback(created.GetAddressOf()).ThrowOnError();
        created.CopyTo(ref this._fallback).ThrowOnError();
    }

    /// <summary>
    /// Finds the font the start of a text is drawn with, matched against a family (the face's font), and the scale of its
    /// size; null if no font has it.
    /// </summary>
    public (LookupStruct Lookup, float Scale)? Find(ReadOnlySpan<char> text, string baseFamily)
    {
        if (text.IsEmpty)
            return null;

        uint mapped;
        float scale;
        using var face = default(MyComPtr<IDWriteFontFace5>);
        fixed (char* t = text)
        fixed (char* family = baseFamily)
        {
            var source = new TextAnalysisSource(t, (uint)text.Length);
            this._fallback.Value->MapCharacters(
                source.Get(),
                0,
                (uint)text.Length,
                (IDWriteFontCollection*)this._services.SystemCollection,
                family,
                null,
                0,
                &mapped,
                &scale,
                face.GetAddressOf()).ThrowOnError();
        }

        if (face.Value == null || mapped == 0 || GetFamilyName(face.Value) is not { } name)
            return null;

        var lookup = LookupStruct.Of(name) with
        {
            Weight = (int)face.Value->GetWeight(),
            Stretch = (int)face.Value->GetStretch(),
            Style = (int)face.Value->GetStyle(),
        };
        return (lookup, scale > 0 ? scale : 1);
    }

    public void Dispose() => this._fallback.Dispose();

    private static void AddMapping(IDWriteFontFallbackBuilder* builder, DWRITE_UNICODE_RANGE range, string[] families)
    {
        var handles = families.Select(f => GCHandle.Alloc(f, GCHandleType.Pinned)).ToArray();
        try
        {
            var names = stackalloc char*[families.Length];
            for (var i = 0; i < families.Length; i++)
                names[i] = (char*)handles[i].AddrOfPinnedObject();
            builder->AddMapping(&range, 1, names, (uint)families.Length, null, null, null, 1).ThrowOnError();
        }
        finally
        {
            foreach (var h in handles)
                h.Free();
        }
    }

    /// <summary>Gets a face's family name: its English one, else its first; null if it has none.</summary>
    private static string? GetFamilyName(IDWriteFontFace5* face)
    {
        using var names = default(MyComPtr<IDWriteLocalizedStrings>);
        if (face->GetFamilyNames(names.GetAddressOf()).FAILED || names.Value->GetCount() == 0)
            return null;

        uint index;
        BOOL exists;
        fixed (char* english = "en-us")
        {
            if (names.Value->FindLocaleName(english, &index, &exists).FAILED || !exists)
                index = 0;
        }

        uint length;
        if (names.Value->GetStringLength(index, &length).FAILED)
            return null;
        var buffer = stackalloc char[(int)length + 1];
        return names.Value->GetString(index, buffer, length + 1).SUCCEEDED ? new(buffer, 0, (int)length) : null;
    }
}
