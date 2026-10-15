using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FontChanger.ShaderGen;

/// <summary>D3DCompile and D3DDisassemble from d3dcompiler_47.dll.</summary>
internal static unsafe partial class D3DCompiler
{
    private const uint OptimizationLevel3 = 1 << 15;

    public static bool Compile(byte[] source, string name, string entry, string target, out byte[] code, out string messages)
    {
        void* codeBlob = null;
        void* errorBlob = null;
        try
        {
            var hr = D3DCompile(source, (nuint)source.Length, name, 0, 0, entry, target, OptimizationLevel3, 0, &codeBlob, &errorBlob);
            messages = errorBlob is null ? string.Empty : Encoding.UTF8.GetString(BlobBytes(errorBlob)).TrimEnd('\0', '\r', '\n');
            code = hr >= 0 && codeBlob is not null ? BlobBytes(codeBlob) : [];
            return hr >= 0;
        }
        finally
        {
            Release(codeBlob);
            Release(errorBlob);
        }
    }

    public static string Disassemble(byte[] code)
    {
        void* blob = null;
        try
        {
            Marshal.ThrowExceptionForHR(D3DDisassemble(code, (nuint)code.Length, 0, null, &blob));
            return Encoding.UTF8.GetString(BlobBytes(blob)).TrimEnd('\0');
        }
        finally
        {
            Release(blob);
        }
    }

    // ID3DBlob: IUnknown (QueryInterface, AddRef, Release), GetBufferPointer, GetBufferSize.
    private static byte[] BlobBytes(void* blob)
    {
        var vtable = *(void***)blob;
        var pointer = ((delegate* unmanaged[Stdcall]<void*, void*>)vtable[3])(blob);
        var size = ((delegate* unmanaged[Stdcall]<void*, nuint>)vtable[4])(blob);
        return new ReadOnlySpan<byte>(pointer, checked((int)size)).ToArray();
    }

    private static void Release(void* unknown)
    {
        if (unknown is not null)
            ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)unknown)[2])(unknown);
    }

    [LibraryImport("d3dcompiler_47.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int D3DCompile(
        byte[] srcData, nuint srcDataSize, string sourceName, nint defines, nint include, string entrypoint, string target,
        uint flags1, uint flags2, void** code, void** errorMsgs);

    [LibraryImport("d3dcompiler_47.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int D3DDisassemble(byte[] srcData, nuint srcDataSize, uint flags, string? comments, void** disassembly);
}