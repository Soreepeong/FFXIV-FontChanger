using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

using TerraFX.Interop.Windows;

using static TerraFX.Interop.Windows.Windows;

namespace FontChanger.DalamudPlugin.Utilities;

/// <summary>Modified version of <see cref="ComPtr{T}"/> to clarify ownership.</summary>
/// <typeparam name="T"></typeparam>
[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
public unsafe struct MyComPtr<T> : IDisposable where T : unmanaged, IUnknown.Interface
{
    private T* _ptr;

    /// <summary>Creates a new <see cref="ComPtr{T}"/> instance from a raw pointer.</summary>
    /// <param name="other">The raw pointer to wrap.</param>
    /// <param name="addRef">Whether to increase the reference count.</param>
    public MyComPtr(T* other, bool addRef)
    {
        this._ptr = other;
        if (addRef)
            this.InternalAddRef();
    }

    /// <inheritdoc cref="ComPtr{T}.Get"/>
    public readonly T* Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => this._ptr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator bool(MyComPtr<T> self) => self._ptr is not null;

    /// <inheritdoc cref="ComPtr{T}.As{U}(ComPtr{U}*)"/>
    public readonly HRESULT As<U>(MyComPtr<U>* p) where U : unmanaged, IUnknown.Interface =>
        this._ptr->QueryInterface(__uuidof<U>(), (void**)p->ReleaseAndGetAddressOf());

    /// <inheritdoc cref="ComPtr{T}.As{U}(ref ComPtr{U})"/>
    public readonly HRESULT As<U>(ref MyComPtr<U> other)
        where U : unmanaged, IUnknown.Interface
    {
        U* ptr;
        var result = this._ptr->QueryInterface(__uuidof<U>(), (void**)&ptr);
        other.Attach(ptr);
        return result;
    }

    /// <inheritdoc cref="ComPtr{T}.AsIID(Guid*, ComPtr{IUnknown}*)"/>
    public readonly HRESULT AsIID(Guid* riid, MyComPtr<IUnknown>* other) =>
        this._ptr->QueryInterface(riid, (void**)other->ReleaseAndGetAddressOf());

    /// <inheritdoc cref="ComPtr{T}.AsIID(Guid*, ref ComPtr{IUnknown})"/>
    public readonly HRESULT AsIID(Guid* riid, ref MyComPtr<IUnknown> other)
    {
        IUnknown* ptr;
        var result = this._ptr->QueryInterface(riid, (void**)&ptr);

        other.Attach(ptr);
        return result;
    }

    /// <inheritdoc cref="ComPtr{T}.Attach(T*)"/>
    public void Attach(T* other)
    {
        if (this._ptr != null)
        {
            var @ref = this._ptr->Release();
            Debug.Assert((@ref != 0) || (this._ptr != other));
        }

        this._ptr = other;
    }

    /// <inheritdoc cref="ComPtr{T}.Detach"/>
    public T* Detach()
    {
        var ptr = this._ptr;
        this._ptr = null;
        return ptr;
    }

    /// <inheritdoc cref="ComPtr{T}.CopyTo(T**)"/>
    public readonly HRESULT CopyTo(T** ptr)
    {
        this.InternalAddRef();
        *ptr = this._ptr;
        return S.S_OK;
    }

    /// <inheritdoc cref="ComPtr{T}.CopyTo(ComPtr{T}*)"/>
    public readonly HRESULT CopyTo(MyComPtr<T>* p)
    {
        this.InternalAddRef();
        *p->ReleaseAndGetAddressOf() = this._ptr;
        return S.S_OK;
    }

    /// <inheritdoc cref="ComPtr{T}.CopyTo(ref ComPtr{T})"/>
    public readonly HRESULT CopyTo(ref MyComPtr<T> other)
    {
        this.InternalAddRef();
        other.Attach(this._ptr);
        return S.S_OK;
    }

    /// <inheritdoc cref="ComPtr{T}.CopyTo{U}(U**)"/>
    public readonly HRESULT CopyTo<U>(U** ptr) where U : unmanaged, IUnknown.Interface =>
        this._ptr->QueryInterface(__uuidof<U>(), (void**)ptr);

    /// <inheritdoc cref="ComPtr{T}.CopyTo{U}(ComPtr{U}*)"/>
    public readonly HRESULT CopyTo<U>(MyComPtr<U>* p) where U : unmanaged, IUnknown.Interface =>
        this._ptr->QueryInterface(__uuidof<U>(), (void**)p->ReleaseAndGetAddressOf());

    /// <inheritdoc cref="ComPtr{T}.CopyTo{U}(ref ComPtr{U})"/>
    public readonly HRESULT CopyTo<U>(ref MyComPtr<U> other)
        where U : unmanaged, IUnknown.Interface
    {
        U* ptr;
        var result = this._ptr->QueryInterface(__uuidof<U>(), (void**)&ptr);

        other.Attach(ptr);
        return result;
    }

    /// <inheritdoc cref="M:TerraFX.Interop.Windows.ComPtr`1.CopyTo(System.Guid*,System.Void**)"/>
    public readonly HRESULT CopyTo(Guid* riid, void** ptr) => this._ptr->QueryInterface(riid, ptr);

    /// <inheritdoc cref="ComPtr{T}.CopyTo(Guid*, ComPtr{IUnknown}*)"/>
    public readonly HRESULT CopyTo(Guid* riid, MyComPtr<IUnknown>* p) => this._ptr->QueryInterface(riid, (void**)p->ReleaseAndGetAddressOf());

    /// <inheritdoc cref="ComPtr{T}.CopyTo(Guid*, ref ComPtr{IUnknown})"/>
    public readonly HRESULT CopyTo(Guid* riid, ref MyComPtr<IUnknown> other)
    {
        IUnknown* ptr;
        var result = this._ptr->QueryInterface(riid, (void**)&ptr);

        other.Attach(ptr);
        return result;
    }

    /// <inheritdoc cref="ComPtr{T}.Dispose"/>
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

    /// <inheritdoc cref="ComPtr{T}.GetAddressOf"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly T** GetAddressOf() => (T**)Unsafe.AsPointer(ref Unsafe.AsRef(in this));

    /// <inheritdoc cref="ComPtr{T}.GetPinnableReference"/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [UnscopedRef]
    public readonly ref readonly T* GetPinnableReference() => ref this._ptr;

    /// <inheritdoc cref="ComPtr{T}.ReleaseAndGetAddressOf"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T** ReleaseAndGetAddressOf()
    {
        _ = this.InternalRelease();
        return this.GetAddressOf();
    }

    /// <inheritdoc cref="ComPtr{T}.Reset"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Reset() => this.InternalRelease();

    /// <inheritdoc cref="ComPtr{T}.Swap(ComPtr{T}*)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Swap(MyComPtr<T>* r)
    {
        var tmp = this._ptr;
        this._ptr = r->_ptr;
        r->_ptr = tmp;
    }

    /// <inheritdoc cref="ComPtr{T}.Swap(ref ComPtr{T})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Swap(ref MyComPtr<T> other)
    {
        var tmp = this._ptr;
        this._ptr = other._ptr;
        other._ptr = tmp;
    }

    // Increments the reference count for the current COM object, if any
    private readonly void InternalAddRef()
    {
        var temp = this._ptr;

        if (temp != null)
        {
            _ = temp->AddRef();
        }
    }

    // Decrements the reference count for the current COM object, if any
    private uint InternalRelease()
    {
        uint @ref = 0;
        var temp = this._ptr;

        if (temp != null)
        {
            this._ptr = null;
            @ref = temp->Release();
        }

        return @ref;
    }
}
