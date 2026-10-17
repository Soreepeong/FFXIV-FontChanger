using System;
using System.Linq;
using System.Runtime.InteropServices;

using FontChanger.DalamudPlugin.PluginInterface;
using FontChanger.DalamudPlugin.Utilities;
using FontChanger.Presets.Records;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Fonts.Text;

internal sealed unsafe class SystemFallbackFonts : IDisposable
{
    // For private-use characters, which the system's fallback would draw with Segoe Fluent Icons.
    private const string IconFamily = "XIV AXIS Std ATK";

    // For fonts that have no element drawn from an installed font.
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
        using var builder = default(MyComPtr<IDWriteFontFallbackBuilder>);
        services.Factory.CreateFontFallbackBuilder(builder.GetAddressOf()).ThrowOnError();

        if (IsInstalled(IconFamily))
            AddMapping(builder.Value, new() { first = 0xE000, last = 0xF8FF }, [IconFamily]);
        else
            Plugin.Log.Warning("{family} isn't installed; private-use characters are drawn with the system's fallback", IconFamily);

        var installed = UiFamilies.Where(IsInstalled).ToArray();
        if (installed.Length != 0)
            AddMapping(builder.Value, new() { first = 0, last = 0x10FFFF }, installed);

        using var systemFallback = default(MyComPtr<IDWriteFontFallback>);
        services.Factory.GetSystemFontFallback(systemFallback.GetAddressOf()).ThrowOnError();
        builder.Value->AddMappings(systemFallback.Value).ThrowOnError();
        using var created = default(MyComPtr<IDWriteFontFallback>);
        builder.Value->CreateFontFallback(created.GetAddressOf()).ThrowOnError();
        created.CopyTo(ref this._fallback).ThrowOnError();

        bool IsInstalled(string family)
        {
            if (!services.TryFindFont(LookupStruct.Of(family), out var font))
                return false;
            font.Dispose();
            return true;
        }
    }

    /// <summary><paramref name="baseFamily"/> is the family the match is kept close to; null if no font has the text's start.</summary>
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
                (IDWriteFontCollection*)this._services.SystemCollection.Pointer,
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
