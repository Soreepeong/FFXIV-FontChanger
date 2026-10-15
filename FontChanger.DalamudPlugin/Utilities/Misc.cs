using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static AtkFontManager.Glyph* FindGlyph(this ref AtkFontManager.Font font, Rune rune) =>
        !rune.IsBmp
        || font.GlyphMap is null
        || !font.Flags.HasFlag(AtkFontManager.FontFlags.Ready)
        || !font.GlyphMap->TryGetValue(GameUtf8.Pack(rune), out var found) || found.Value is null
            ? null
            : found.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetFileName(this in ResourceHandle handle) =>
        handle.FileName.Length is > 0 and <= 4096
            ? Encoding.UTF8.GetString(handle.FileName.AsSpan(0, (int)handle.FileName.Length))
            : string.Empty;

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

    public static bool TryFind<T, TContext>(this List<T> list, TContext context, Func<T, TContext, bool> predicate, [MaybeNullWhen(false)] out T found)
    {
        foreach (var item in list)
        {
            if (predicate(item, context))
            {
                found = item;
                return true;
            }
        }

        found = default;
        return false;
    }

    public static bool EndsWithAsciiIgnoreCase(this in StdString str, ReadOnlySpan<char> other) =>
        str.Length >= (ulong)other.Length && Ascii.EqualsIgnoreCase(str.AsSpan((long)str.Length - other.Length, other.Length), other);

    extension<T>(ComPtr<T> ptr) where T : unmanaged, IUnknown.Interface
    {
        public bool IsNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ptr.Get() is null;
        }
    }

    public static string Describe(this D3D11_BLEND_OP op) => op switch
    {
        D3D11_BLEND_OP.D3D11_BLEND_OP_ADD => "ADD",
        D3D11_BLEND_OP.D3D11_BLEND_OP_SUBTRACT => "SUBTRACT",
        D3D11_BLEND_OP.D3D11_BLEND_OP_REV_SUBTRACT => "REV_SUBTRACT",
        D3D11_BLEND_OP.D3D11_BLEND_OP_MIN => "MIN",
        D3D11_BLEND_OP.D3D11_BLEND_OP_MAX => "MAX",
        _ => $"unknown(0x{(uint)op:08X})",
    };

    public static string Describe(this D3D11_BLEND blend) => blend switch
    {
        D3D11_BLEND.D3D11_BLEND_ZERO => "ZERO",
        D3D11_BLEND.D3D11_BLEND_ONE => "ONE",
        D3D11_BLEND.D3D11_BLEND_SRC_COLOR => "SRC_COLOR",
        D3D11_BLEND.D3D11_BLEND_INV_SRC_COLOR => "INV_SRC_COLOR",
        D3D11_BLEND.D3D11_BLEND_SRC_ALPHA => "SRC_ALPHA",
        D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA => "INV_SRC_ALPHA",
        D3D11_BLEND.D3D11_BLEND_DEST_ALPHA => "DEST_ALPHA",
        D3D11_BLEND.D3D11_BLEND_INV_DEST_ALPHA => "INV_DEST_ALPHA",
        D3D11_BLEND.D3D11_BLEND_DEST_COLOR => "DEST_COLOR",
        D3D11_BLEND.D3D11_BLEND_INV_DEST_COLOR => "INV_DEST_COLOR",
        D3D11_BLEND.D3D11_BLEND_SRC_ALPHA_SAT => "SRC_ALPHA_SAT",
        D3D11_BLEND.D3D11_BLEND_BLEND_FACTOR => "BLEND_FACTOR",
        D3D11_BLEND.D3D11_BLEND_INV_BLEND_FACTOR => "INV_BLEND_FACTOR",
        D3D11_BLEND.D3D11_BLEND_SRC1_COLOR => "SRC1_COLOR",
        D3D11_BLEND.D3D11_BLEND_INV_SRC1_COLOR => "INV_SRC1_COLOR",
        D3D11_BLEND.D3D11_BLEND_SRC1_ALPHA => "SRC1_ALPHA",
        D3D11_BLEND.D3D11_BLEND_INV_SRC1_ALPHA => "INV_SRC1_ALPHA",
        _ => $"unknown(0x{(uint)blend:08X})",
    };

    extension(PackedBlendStateDesc desc)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ByteEquals(PackedBlendStateDesc other) =>
            Unsafe.As<PackedBlendStateDesc, uint>(ref Unsafe.AsRef(in desc))
            == Unsafe.As<PackedBlendStateDesc, uint>(ref Unsafe.AsRef(in other));

        public string Describe() =>
            $"BlendState({(desc.BlendEnable ? "enabled" : "disabled")}" +
            $", op={((D3D11_BLEND_OP)desc.BlendOp).Describe()}" +
            $"(src={((D3D11_BLEND)desc.SrcBlend).Describe()}" +
            $", desc={((D3D11_BLEND)desc.DestBlend).Describe()}" +
            $"), alpha={desc.BlendOpAlpha}" +
            $"(src={((D3D11_BLEND)desc.SrcBlendAlpha).Describe()}" +
            $", desc={((D3D11_BLEND)desc.DestBlendAlpha).Describe()}" +
            $"))";
    }
}
