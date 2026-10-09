using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CustomFonts;

/// <summary>
/// A signature of the game's code: a regex over its bytes (as Latin-1 characters), as XivAlexander writes them, which must
/// match exactly once. Named groups capture what the code says: call targets, RIP-relative addresses, structure offsets.
/// </summary>
/// <remarks>
/// The signatures are xivres.fontgen's data/game_font_signatures.json, which XivAlexander uses too, embedded. The code is
/// read from the game's executable rather than from memory, so hooks that other plugins placed don't hide it.
/// </remarks>
internal sealed class CodeSignature
{
    private static readonly Lazy<GameText> Text = new(GameText.Load);
    private static readonly Lazy<Dictionary<string, CodeSignature>> All = new(Load);

    private readonly Regex regex;

    private CodeSignature(string name, JsonElement json)
    {
        this.Name = name;
        this.Resolve = json.TryGetProperty("resolve", out var resolve) ? resolve.GetString()! : "match";
        var pattern = json.GetProperty("pattern");
        this.regex = new(
            pattern.ValueKind == JsonValueKind.Array ? string.Concat(pattern.EnumerateArray().Select(x => x.GetString())) : pattern.GetString()!,
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        // Captures are listed in group order: "captures": [{"name": "Wrapper.MaxUnits", "type": "offset"}, ...]. A name
        // given to several groups, in alternatives of the pattern, is what the one that matched captured.
        if (json.TryGetProperty("captures", out var captures))
        {
            var group = 1;
            foreach (var c in captures.EnumerateArray())
            {
                var captureName = c.GetProperty("name").GetString()!;
                if (!this.Groups.TryGetValue(captureName, out var groups))
                {
                    this.Groups[captureName] = groups = [];
                    this.Types[captureName] = c.GetProperty("type").GetString()!;
                }

                groups.Add(group++);
            }
        }
    }

    public string Name { get; }

    /// <summary>
    /// Gets what the signature resolves to: "match", its start; "function", the function containing it; "capture", its
    /// capture of type "position"; "none", nothing, its captures being all that is used.
    /// </summary>
    public string Resolve { get; }

    /// <summary>Gets the groups by the names of what they capture.</summary>
    public Dictionary<string, List<int>> Groups { get; } = [];

    /// <summary>
    /// Gets the types of the captures: "offset" of a field ("Structure.Field"), "size" of a structure, or "rel32".
    /// </summary>
    public Dictionary<string, string> Types { get; } = [];

    /// <summary>Gets every signature of game_font_signatures.json.</summary>
    public static IEnumerable<CodeSignature> Definitions => All.Value.Values;

    /// <summary>Finds the one match of a signature; throws if there is none, or more than one.</summary>
    public static CodeMatch Find(string name) =>
        (All.Value.TryGetValue(name, out var s) ? s : throw new KeyNotFoundException($"There is no signature {name}.")).Find();

    /// <summary>Finds the one match; throws if there is none, or more than one.</summary>
    public CodeMatch Find()
    {
        var text = Text.Value;
        var first = this.regex.Match(text.Bytes);
        if (!first.Success)
            throw new InvalidOperationException($"Signature {this.Name} wasn't found; the game's code changed.");

        // Matches may overlap; the next search starts a byte after the last match's start.
        var second = this.regex.Match(text.Bytes, first.Index + 1);
        if (second.Success)
            throw new InvalidOperationException($"Signature {this.Name} matched more than once ({text.Address(first.Index):X}, {text.Address(second.Index):X}).");
        return new(this, text, first);
    }

    private static Dictionary<string, CodeSignature> Load()
    {
        using var stream = typeof(CodeSignature).Assembly.GetManifestResourceStream("CustomFonts.game_font_signatures.json")
                           ?? throw new InvalidOperationException("The game's code signatures aren't embedded.");
        using var document = JsonDocument.Parse(stream, new() { CommentHandling = JsonCommentHandling.Skip });
        return document.RootElement.GetProperty("signatures").EnumerateObject().ToDictionary(p => p.Name, p => new CodeSignature(p.Name, p.Value));
    }

    /// <summary>The game's .text section: its bytes as Latin-1 characters, and the address of its first byte in memory.</summary>
    internal sealed class GameText(nint baseAddress, string bytes)
    {
        public string Bytes { get; } = bytes;

        public nint Address(int index) => baseAddress + index;

        public static GameText Load()
        {
            var module = Plugin.SigScanner.Module;
            var file = File.ReadAllBytes(module.FileName!);
            var (rva, raw, size) = FindText(file);
            return new(module.BaseAddress + rva, Encoding.Latin1.GetString(file, raw, size));
        }

        /// <summary>Gets the .text section's RVA, file offset and size from the PE headers.</summary>
        private static (int Rva, int Raw, int Size) FindText(byte[] file)
        {
            var pe = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x3C));
            var sections = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(pe + 6));
            var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(pe + 20));
            for (var i = 0; i < sections; i++)
            {
                var s = file.AsSpan(pe + 24 + optionalSize + (i * 40));
                if (s[..8].SequenceEqual(".text\0\0\0"u8))
                {
                    return (BinaryPrimitives.ReadInt32LittleEndian(s[12..]), BinaryPrimitives.ReadInt32LittleEndian(s[20..]),
                        BinaryPrimitives.ReadInt32LittleEndian(s[16..]));
                }
            }

            throw new InvalidDataException("The game has no .text section.");
        }
    }
}

/// <summary>A match of a <see cref="CodeSignature"/>: where it is, and what its groups captured.</summary>
internal sealed unsafe class CodeMatch(CodeSignature signature, CodeSignature.GameText text, Match match)
{
    public CodeSignature Signature => signature;

    /// <summary>
    /// Gets what the signature resolves to: the match's start, or the start of the function containing it.
    /// </summary>
    public nint Address => signature.Resolve switch
    {
        "match" => this.MatchAddress,
        "function" => FunctionContaining(this.MatchAddress, signature.Name),
        "capture" => this.CaptureAddress(signature.Types.First(t => t.Value == "position").Key),
        _ => throw new InvalidOperationException($"Signature {signature.Name} resolves to nothing; only its captures are used."),
    };

    /// <summary>Gets the address of the match's start.</summary>
    public nint MatchAddress => text.Address(match.Index);

    /// <summary>Gets the address of a capture's start.</summary>
    public nint CaptureAddress(string capture) => text.Address(this.Get(capture).Index);

    /// <summary>Gets a capture's bytes as a little-endian signed integer (1, 2 or 4 bytes).</summary>
    public int Int(string capture)
    {
        var g = this.Get(capture);
        var bytes = text.Bytes.AsSpan(g.Index, g.Length);
        return bytes.Length switch
        {
            1 => (sbyte)bytes[0],
            2 => (short)(bytes[0] | (bytes[1] << 8)),
            4 => bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24),
            _ => throw new InvalidOperationException($"Signature {signature.Name}: {capture} is {bytes.Length} bytes."),
        };
    }

    /// <summary>Gets the target of a rel32 capture (a call's, or a RIP-relative operand's that ends with it).</summary>
    public nint Target(string capture) => this.CaptureAddress(capture) + 4 + this.Int(capture);

    /// <summary>Gets whether a capture's group matched; some are optional.</summary>
    public bool Captured(string capture) => this.TryGet(capture) is not null;

    /// <summary>Gets the start of the function containing an address, from the unwind data of its module.</summary>
    private static nint FunctionContaining(nint address, string name)
    {
        var entry = RtlLookupFunctionEntry((ulong)address, out var imageBase, 0);
        if (entry == null)
            throw new InvalidOperationException($"Signature {name}: {address:X} is in no function.");

        // A function's later parts have chained unwind info, leading back to its first part.
        for (var i = 0; i < 32; i++)
        {
            var info = (byte*)(imageBase + entry->UnwindData);
            if (((info[0] >> 3) & UnwindFlagChainInfo) == 0)
                return (nint)(imageBase + entry->BeginAddress);
            entry = (RuntimeFunction*)(info + 4 + (((info[2] + 1) & ~1) * 2));
        }

        throw new InvalidOperationException($"Signature {name}: the unwind data of {address:X} chains too far.");
    }

    [DllImport("kernel32.dll")]
    private static extern RuntimeFunction* RtlLookupFunctionEntry(ulong controlPc, out ulong imageBase, nint historyTable);

    private const int UnwindFlagChainInfo = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct RuntimeFunction
    {
        public uint BeginAddress;
        public uint EndAddress;
        public uint UnwindData;
    }

    private Group? TryGet(string capture)
    {
        if (!signature.Groups.TryGetValue(capture, out var groups))
            throw new KeyNotFoundException($"Signature {signature.Name} captures no {capture}.");
        return groups.Select(g => match.Groups[g]).FirstOrDefault(g => g.Success);
    }

    private Group Get(string capture) =>
        this.TryGet(capture) ?? throw new InvalidOperationException($"Signature {signature.Name}: {capture} didn't capture.");
}
