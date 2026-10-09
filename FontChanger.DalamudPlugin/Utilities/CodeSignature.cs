using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CustomFonts;

/// <summary>
/// A signature of the game's code: a regex over its bytes (as Latin-1 characters), as XivAlexander writes them, which must
/// match exactly once. Named groups capture what the code says: call targets, RIP-relative addresses, structure offsets.
/// </summary>
/// <remarks>
/// The signatures are xivres's data/game_font_signatures.json, which XivAlexander uses too, embedded. The code is
/// read from the game's executable rather than from memory, so hooks that other plugins placed don't hide it.
/// </remarks>
internal sealed class CodeSignature
{
    private CodeSignature(string name, JsonElement json)
    {
        this.Name = name;
        this.Resolve = json.TryGetProperty("resolve", out var resolve) ? resolve.GetString()! : "match";
        var pattern = json.GetProperty("pattern");
        this.Pattern = pattern.ValueKind == JsonValueKind.Array ? string.Concat(pattern.EnumerateArray().Select(x => x.GetString())) : pattern.GetString()!;

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

    /// <summary>
    /// Gets the types of the captures: "offset" of a field ("Structure.Field"), "size" of a structure, or "rel32".
    /// </summary>
    public Dictionary<string, string> Types { get; } = [];

    private string Pattern { get; }

    // The groups by the names of what they capture.
    private Dictionary<string, List<int>> Groups { get; } = [];

    /// <summary>
    /// Finds every signature of game_font_signatures.json in the game's code, in parallel: the one match of each, or why
    /// there is none (no match, or more than one). The code and the regexes are let go of afterwards.
    /// </summary>
    public static (IReadOnlyList<CodeMatch> Found, IReadOnlyDictionary<string, string> Failed) FindAll()
    {
        var text = TextSection.Load();
        var found = new ConcurrentBag<CodeMatch>();
        var failed = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(Load(), s =>
        {
            try
            {
                found.Add(s.Find(text));
            }
            catch (InvalidOperationException ex)
            {
                failed[s.Name] = ex.Message;
            }
        });
        return (found.ToList(), failed);
    }

    /// <summary>
    /// Gets the constants of game_font_signatures.json: facts about the game that no signature captures, by name; each an
    /// int, a string or an int array.
    /// </summary>
    public static Dictionary<string, object> LoadConstants()
    {
        using var document = OpenData();
        var constants = new Dictionary<string, object>();
        foreach (var p in document.RootElement.GetProperty("constants").EnumerateObject())
        {
            var value = p.Value.GetProperty("value");
            constants[p.Name] = value.ValueKind switch
            {
                JsonValueKind.Number => value.GetInt32(),
                JsonValueKind.String => value.GetString()!,
                JsonValueKind.Array => value.EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                _ => throw new InvalidDataException($"Constant {p.Name} is neither a number, a string nor an array."),
            };
        }

        return constants;
    }

    private static List<CodeSignature> Load()
    {
        using var document = OpenData();
        return document.RootElement.GetProperty("signatures").EnumerateObject().Select(p => new CodeSignature(p.Name, p.Value)).ToList();
    }

    private static JsonDocument OpenData()
    {
        using var stream = typeof(CodeSignature).Assembly.GetManifestResourceStream("CustomFonts.game_font_signatures.json")
                           ?? throw new InvalidOperationException("The game's code signatures aren't embedded.");
        return JsonDocument.Parse(stream, new() { CommentHandling = JsonCommentHandling.Skip });
    }

    /// <summary>Finds the one match; throws if there is none, or more than one.</summary>
    private CodeMatch Find(TextSection text)
    {
        var regex = new Regex(this.Pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant);
        var first = regex.Match(text.Bytes);
        if (!first.Success)
            throw new InvalidOperationException($"Signature {this.Name} wasn't found; the game's code changed.");

        // Matches may overlap; the next search starts a byte after the last match's start.
        var second = regex.Match(text.Bytes, first.Index + 1);
        if (second.Success)
            throw new InvalidOperationException($"Signature {this.Name} matched more than once ({text.Address(first.Index):X}, {text.Address(second.Index):X}).");

        // What each capture took: where it is, and its bytes as a little-endian signed integer (if 1, 2 or 4 bytes).
        var captures = new Dictionary<string, (nint Address, int? Value)>();
        foreach (var (name, groups) in this.Groups)
        {
            if (groups.Select(g => first.Groups[g]).FirstOrDefault(g => g.Success) is not { } g)
                continue;
            var bytes = text.Bytes.AsSpan(g.Index, g.Length);
            int? value = bytes.Length switch
            {
                1 => (sbyte)bytes[0],
                2 => (short)(bytes[0] | (bytes[1] << 8)),
                4 => bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24),
                _ => null,
            };
            captures[name] = (text.Address(g.Index), value);
        }

        return new(this, text.Address(first.Index), captures);
    }

    /// <summary>The game's .text section: its bytes as Latin-1 characters, and the address of its first byte in memory.</summary>
    private sealed class TextSection(nint baseAddress, string bytes)
    {
        public string Bytes { get; } = bytes;

        public nint Address(int index) => baseAddress + index;

        /// <summary>Reads the .text section from the game's executable, as the PE headers say where it is.</summary>
        public static TextSection Load()
        {
            using var file = File.OpenRead(Host.Current.GameFileName);
            var headers = new byte[0x1000];
            file.ReadExactly(headers);
            var pe = BinaryPrimitives.ReadInt32LittleEndian(headers.AsSpan(0x3C));
            var sections = BinaryPrimitives.ReadUInt16LittleEndian(headers.AsSpan(pe + 6));
            var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(headers.AsSpan(pe + 20));
            for (var i = 0; i < sections; i++)
            {
                var s = headers.AsSpan(pe + 24 + optionalSize + (i * 40));
                if (!s[..8].SequenceEqual(".text\0\0\0"u8))
                    continue;
                var rva = BinaryPrimitives.ReadInt32LittleEndian(s[12..]);
                var size = BinaryPrimitives.ReadInt32LittleEndian(s[16..]);
                var raw = BinaryPrimitives.ReadInt32LittleEndian(s[20..]);
                var text = new byte[size];
                file.Position = raw;
                file.ReadExactly(text);
                return new(Host.Current.GameBaseAddress + rva, Encoding.Latin1.GetString(text));
            }

            throw new InvalidDataException("The game has no .text section.");
        }
    }
}

/// <summary>A match of a <see cref="CodeSignature"/>: where it is, and what its groups captured.</summary>
internal sealed unsafe class CodeMatch(CodeSignature signature, nint matchAddress, Dictionary<string, (nint Address, int? Value)> captures)
{
    private const int UnwindFlagChainInfo = 4;

    public CodeSignature Signature => signature;

    /// <summary>
    /// Gets what the signature resolves to: the match's start, or the start of the function containing it.
    /// </summary>
    public nint Address => signature.Resolve switch
    {
        "match" => matchAddress,
        "function" => FunctionContaining(matchAddress, signature.Name),
        "capture" => this.Get(signature.Types.First(t => t.Value == "position").Key).Address,
        _ => throw new InvalidOperationException($"Signature {signature.Name} resolves to nothing; only its captures are used."),
    };

    /// <summary>Gets a capture's bytes as a little-endian signed integer (1, 2 or 4 bytes).</summary>
    public int Int(string capture) =>
        this.Get(capture).Value ?? throw new InvalidOperationException($"Signature {signature.Name}: {capture} isn't 1, 2 or 4 bytes.");

    /// <summary>Gets the target of a rel32 capture (a call's, or a RIP-relative operand's that ends with it).</summary>
    public nint Target(string capture) => this.Get(capture).Address + 4 + this.Int(capture);

    /// <summary>Gets whether a capture's group matched; some are optional.</summary>
    public bool Captured(string capture)
    {
        if (!signature.Types.ContainsKey(capture))
            throw new KeyNotFoundException($"Signature {signature.Name} captures no {capture}.");
        return captures.ContainsKey(capture);
    }

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

    private (nint Address, int? Value) Get(string capture) =>
        this.Captured(capture) ? captures[capture] : throw new InvalidOperationException($"Signature {signature.Name}: {capture} didn't capture.");

    [StructLayout(LayoutKind.Sequential)]
    private struct RuntimeFunction
    {
        public uint BeginAddress;
        public uint EndAddress;
        public uint UnwindData;
    }
}
