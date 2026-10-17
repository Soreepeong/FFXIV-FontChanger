using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.STD;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace FontChanger.DalamudPlugin.Utilities;

public static unsafe class Misc
{
    [Conditional("DEBUG")]
    public static void DebugAssertMainThread() => ThreadSafety.AssertMainThread();

    [Conditional("DEBUG")]
    public static void DebugAssertLockHeld(this Lock lockObj)
    {
        if (!lockObj.IsHeldByCurrentThread)
            throw new InvalidOperationException("Lock was expected to be held by current thread, but was not.");
    }

    public static float StdRound(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AtkFontManager.Glyph* FindGlyph(this ref AtkFontManager.Font font, Rune rune) =>
        !rune.IsBmp
        || font.GlyphMap is null
        || !font.Flags.HasFlag(AtkFontManager.FontFlags.Ready)
        || !font.GlyphMap->TryGetValue(GameUtf8.Pack(rune), out var found) || found.Value is null
            ? null
            : found.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static nint FieldOffsetOf(this Type type, string fieldName) =>
        type.GetField(fieldName)?.GetCustomAttribute<FieldOffsetAttribute>()?.Value
        ?? throw new InvalidOperationException($"{type.Name}.{fieldName} has no FieldOffset attribute.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ThrowOnError(this HRESULT hr)
    {
        if (hr.FAILED)
            Marshal.ThrowExceptionForHR(hr.Value);
    }

    public static bool EndsWithAsciiIgnoreCase(this in StdString str, ReadOnlySpan<char> other) =>
        str.Length >= (ulong)other.Length && Ascii.EqualsIgnoreCase(str.AsSpan((long)str.Length - other.Length, other.Length), other);

    public static HRESULT CreateFontFace(this ref IDWriteFont3 font, IDWriteFontFace5** face)
    {
        using var face3 = default(MyComPtr<IDWriteFontFace3>);
        var hr = font.CreateFontFace(face3.GetAddressOf());
        return hr.FAILED ? hr : face3.CopyTo(face);
    }

    public static HRESULT GetFirstMatchingFont(
        this ref IDWriteFontFamily2 family,
        DWRITE_FONT_WEIGHT weight,
        DWRITE_FONT_STRETCH stretch,
        DWRITE_FONT_STYLE style,
        IDWriteFont3** font)
    {
        using var matched = default(MyComPtr<IDWriteFont>);
        var hr = family.GetFirstMatchingFont(weight, stretch, style, matched.GetAddressOf());
        return hr.FAILED ? hr : matched.CopyTo(font);
    }

    extension<T>(ref T child) where T : unmanaged, ID3D11DeviceChild.Interface
    {
        public MyComPtr<ID3D11Device> GetDevice()
        {
            var device = default(MyComPtr<ID3D11Device>);
            child.GetDevice(device.GetAddressOf());
            return device;
        }

        public MyComPtr<ID3D11DeviceContext> GetImmediateContext()
        {
            using var device = child.GetDevice();
            var context = default(MyComPtr<ID3D11DeviceContext>);
            device.Value->GetImmediateContext(context.GetAddressOf());
            return context;
        }
    }
}
