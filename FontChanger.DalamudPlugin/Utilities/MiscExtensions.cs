using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Component.GUI;

using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Utilities;

public static  unsafe class ClientStructExtensions
{
    public static AtkFontManager.Glyph* FindGlyph(this ref AtkFontManager.Font font, Rune rune) =>
        !rune.IsBmp
        || font.GlyphMap is null
        || !font.Flags.HasFlag(AtkFontManager.FontFlags.Ready)
        || !font.GlyphMap->TryGetValue(GameUtf8.Pack(rune), out var found) || found.Value is null
            ? null
            : found.Value;

    public static string GetFileName(this in ResourceHandle handle) =>
        handle.FileName.Length is > 0 and <= 4096
            ? Encoding.UTF8.GetString(handle.FileName.AsSpan(0, (int)handle.FileName.Length))
            : string.Empty;

    public static nint FieldOffsetOf(this Type type, string fieldName) =>
        type.GetField(fieldName)?.GetCustomAttribute<FieldOffsetAttribute>()?.Value
        ?? throw new InvalidOperationException($"{type.Name}.{fieldName} has no FieldOffset attribute.");

    public static void ThrowOnError(this HRESULT hr)
    {
        if (hr.FAILED)
            Marshal.ThrowExceptionForHR(hr.Value);
    }

    extension<T>(ComPtr<T> ptr)
        where T : unmanaged, IUnknown.Interface
    {
        public bool IsNull => ptr.Get() is null;
    }
}
