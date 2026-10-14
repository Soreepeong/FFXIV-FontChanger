using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;

namespace FontChanger.DalamudPlugin.Rendering.Shaders;

/// <summary>
/// The shaders' bytecode: embedded in the plugin, with settings put in by replacing their constants, distinctive values
/// compiled as immediate operands, in the code chunk; the container's checksum is then computed again.
/// </summary>
/// <remarks>
/// Also compiled into FontChanger.ShaderGen, which checks this against compiling with the values written in; so this part
/// uses nothing but the runtime.
/// </remarks>
internal sealed partial class PixelShaderSwap
{
    private static readonly int[] Shifts =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    private static readonly uint[] Sines =
    [
        0xD76AA478, 0xE8C7B756, 0x242070DB, 0xC1BDCEEE, 0xF57C0FAF, 0x4787C62A, 0xA8304613, 0xFD469501,
        0x698098D8, 0x8B44F7AF, 0xFFFF5BB1, 0x895CD7BE, 0x6B901122, 0xFD987193, 0xA679438E, 0x49B40821,
        0xF61E2562, 0xC040B340, 0x265E5A51, 0xE9B6C7AA, 0xD62F105D, 0x02441453, 0xD8A1E681, 0xE7D3FBC8,
        0x21E1CDE6, 0xC33707D6, 0xF4D50D87, 0x455A14ED, 0xA9E3E905, 0xFCEFA3F8, 0x676F02D9, 0x8D2A4C8A,
        0xFFFA3942, 0x8771F681, 0x6D9D6122, 0xFDE5380C, 0xA4BEEA44, 0x4BDECFA9, 0xF6BB4B60, 0xBEBFBC70,
        0x289B7EC6, 0xEAA127FA, 0xD4EF3085, 0x04881D05, 0xD9D4D039, 0xE6DB99E5, 0x1FA27CF8, 0xC4AC5665,
        0xF4292244, 0x432AFF97, 0xAB9423A7, 0xFC93A039, 0x655B59C3, 0x8F0CCC92, 0xFFEFF47D, 0x85845DD1,
        0x6FA87E4F, 0xFE2CE6E0, 0xA3014314, 0x4E0811A1, 0xF7537E82, 0xBD3AF235, 0x2AD7D2BB, 0xEB86D391,
    ];

    /// <summary>Gets the bytecode of a shader embedded in the plugin.</summary>
    public static byte[] ReadShaderCode(string name)
    {
        using var stream = typeof(PixelShaderSwap).Assembly.GetManifestResourceStream(name)
                           ?? throw new FileNotFoundException($"{name} isn't embedded.");
        var code = new byte[stream.Length];
        stream.ReadExactly(code);
        return code;
    }

    /// <summary>
    /// Replaces every word of the code chunk (SHEX or SHDR) equal to a sentinel (a float's bits) with its value and sets
    /// the checksum. Returns how many were replaced; with none, <paramref name="dxbc"/> is left as it was.
    /// </summary>
    public static int PatchConstants(Span<byte> dxbc, ReadOnlySpan<(uint Sentinel, float Value)> values)
    {
        var (begin, end) = FindCode(dxbc);
        var count = 0;
        for (var i = begin; i + 4 <= end; i += 4)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(dxbc[i..]);
            foreach (var (sentinel, value) in values)
            {
                if (word != sentinel)
                    continue;
                BinaryPrimitives.WriteUInt32LittleEndian(dxbc[i..], BitConverter.SingleToUInt32Bits(value));
                count++;
                break;
            }
        }

        if (count != 0)
            ComputeChecksum(dxbc, dxbc.Slice(4, 16));
        return count;
    }

    /// <summary>
    /// The container's checksum, of everything after it: MD5's rounds, but the bit count goes in front of the last
    /// block's data and <c>(bits &gt;&gt; 2) | 1</c> at its end.
    /// </summary>
    public static void ComputeChecksum(ReadOnlySpan<byte> dxbc, Span<byte> checksum)
    {
        var data = dxbc[20..];
        var bits = unchecked((uint)data.Length * 8);
        Span<uint> state = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476];
        var full = data.Length / 64 * 64;
        for (var i = 0; i < full; i += 64)
            Transform(state, data.Slice(i, 64));

        var last = data[full..];
        Span<byte> block = stackalloc byte[64];
        block.Clear();
        if (last.Length >= 56)
        {
            last.CopyTo(block);
            block[last.Length] = 0x80;
            Transform(state, block);
            block.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(block, bits);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(block, bits);
            last.CopyTo(block[4..]);
            block[4 + last.Length] = 0x80;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(block[60..], (bits >> 2) | 1);
        Transform(state, block);
        for (var i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(checksum[(4 * i)..], state[i]);
    }

    /// <summary>The code chunk's tokens, as offsets in the container.</summary>
    private static (int Begin, int End) FindCode(ReadOnlySpan<byte> dxbc)
    {
        if (dxbc.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(dxbc) != 0x43425844)
            throw new InvalidDataException("Not a DXBC container.");
        var chunks = BinaryPrimitives.ReadInt32LittleEndian(dxbc[28..]);
        for (var i = 0; i < chunks; i++)
        {
            var at = BinaryPrimitives.ReadInt32LittleEndian(dxbc[(32 + (4 * i))..]);
            var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(dxbc[at..]);
            if (fourCc is 0x58454853 or 0x52444853)
                return (at + 8, Math.Min(dxbc.Length, at + 8 + BinaryPrimitives.ReadInt32LittleEndian(dxbc[(at + 4)..])));
        }

        throw new InvalidDataException("The shader has no code chunk.");
    }

    /// <summary>MD5's rounds over a 64-byte block.</summary>
    private static void Transform(Span<uint> state, ReadOnlySpan<byte> block)
    {
        Span<uint> m = stackalloc uint[16];
        for (var i = 0; i < 16; i++)
            m[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(4 * i)..]);

        uint a = state[0], b = state[1], c = state[2], d = state[3];
        for (var i = 0; i < 64; i++)
        {
            uint f;
            int g;
            switch (i >> 4)
            {
                case 0:
                    (f, g) = ((b & c) | (~b & d), i);
                    break;
                case 1:
                    (f, g) = ((d & b) | (~d & c), ((5 * i) + 1) & 15);
                    break;
                case 2:
                    (f, g) = (b ^ c ^ d, ((3 * i) + 5) & 15);
                    break;
                default:
                    (f, g) = (c ^ (b | ~d), (7 * i) & 15);
                    break;
            }

            f = unchecked(f + a + Sines[i] + m[g]);
            (a, d, c) = (d, c, b);
            b = unchecked(b + BitOperations.RotateLeft(f, Shifts[i]));
        }

        state[0] = unchecked(state[0] + a);
        state[1] = unchecked(state[1] + b);
        state[2] = unchecked(state[2] + c);
        state[3] = unchecked(state[3] + d);
    }
}
