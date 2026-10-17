using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using TerraFX.Interop.Windows;

using static TerraFX.Interop.Windows.Windows;

namespace FontChanger.DalamudPlugin.Utilities;

/// <summary>A <see cref="ComPtr{T}"/> whose constructor makes taking ownership of a raw pointer explicit.</summary>
public unsafe struct MyComPtr<T> : IDisposable where T : unmanaged, IUnknown.Interface
{
    private T* _ptr;

    public MyComPtr(T* other, bool addRef)
    {
        this._ptr = other;
        if (addRef && other != null)
            _ = other->AddRef();
    }

    public readonly T* Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => this._ptr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator bool(MyComPtr<T> self) => self._ptr is not null;

    public void Attach(T* other)
    {
        if (this._ptr != null)
        {
            var @ref = this._ptr->Release();
            Debug.Assert((@ref != 0) || (this._ptr != other));
        }

        this._ptr = other;
    }

    public T* Detach()
    {
        var ptr = this._ptr;
        this._ptr = null;
        return ptr;
    }

    public readonly HRESULT CopyTo<TOther>(TOther** ptr) where TOther : unmanaged, IUnknown.Interface =>
        this._ptr->QueryInterface(__uuidof<TOther>(), (void**)ptr);

    public readonly HRESULT CopyTo<TOther>(ref MyComPtr<TOther> other) where TOther : unmanaged, IUnknown.Interface
    {
        TOther* ptr;
        var result = this._ptr->QueryInterface(__uuidof<TOther>(), (void**)&ptr);
        other.Attach(ptr);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        var pointer = this._ptr;
        if (pointer != null)
        {
            this._ptr = null;
            _ = pointer->Release();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly T** GetAddressOf() => (T**)Unsafe.AsPointer(ref Unsafe.AsRef(in this));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [UnscopedRef]
    public readonly ref readonly T* GetPinnableReference() => ref this._ptr;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T** ReleaseAndGetAddressOf()
    {
        this.Dispose();
        return this.GetAddressOf();
    }
}
