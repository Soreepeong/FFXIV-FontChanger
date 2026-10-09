using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace CustomFonts;

/// <summary>
/// What the game's code says about the structures the plugin uses: every signature of game_font_signatures.json found,
/// and their captured offsets, sizes and counts merged by name.
/// </summary>
/// <remarks>
/// <para>A value is known when at least one signature captured it and none disagree. Several signatures capture most
/// fields, so one of them no longer matching after a patch doesn't stop the plugin, and fields that move are read where
/// they are now.</para>
/// <para>Each part of the plugin resolves what it uses (<see cref="Resolve"/>), and fails alone if something of it isn't
/// known. Structures that kept their layout since 2020 stay C# structures, checked against the captures
/// (<see cref="CheckFixed"/>).</para>
/// </remarks>
internal static class GameLayout
{
    private static readonly Dictionary<string, CodeMatch> Matches = [];
    private static readonly Dictionary<string, string> Failures = [];
    private static readonly Dictionary<string, (int Value, string Source)> Values = [];
    private static readonly Dictionary<string, string> Conflicts = [];
    private static readonly List<string> Problems = [];
    private static bool loaded;

    /// <summary>
    /// Resolves what a part of the plugin uses (<paramref name="resolve"/> calls <see cref="Get"/> and the like). Throws,
    /// with everything that isn't known, if anything isn't.
    /// </summary>
    public static void Resolve(string part, Action resolve)
    {
        Load();
        Problems.Clear();
        resolve();
        if (Problems.Count == 0)
            return;

        var problems = string.Join("; ", Problems.Distinct());
        Problems.Clear();
        throw new InvalidOperationException(
            $"{part} can't work with this version of the game: {problems}. " +
            (Failures.Count == 0 ? "Every signature matched." : "Signatures not found: " + string.Join(", ", Failures.Keys) + "."));
    }

    /// <summary>
    /// Gets a captured value. A field at offset 0 has no displacement to capture: give <paramref name="implied"/> 0 for
    /// those. Records a problem and returns -1 if the value isn't known.
    /// </summary>
    public static int Get(string name, int? implied = null)
    {
        if (Conflicts.TryGetValue(name, out var conflict))
            Problems.Add(conflict);
        else if (Values.TryGetValue(name, out var v))
            return v.Value;
        else if (implied is { } i)
            return i;
        else
            Problems.Add($"no signature found says where {name} is");
        return -1;
    }

    /// <summary>Gets a captured value if any signature captured it (and they agree).</summary>
    public static int? TryGet(string name) =>
        !Conflicts.ContainsKey(name) && Values.TryGetValue(name, out var v) ? v.Value : null;

    /// <summary>
    /// Gets what a signature resolves to, or with <paramref name="rel32"/>, the target it captures. Records a problem and
    /// returns 0 if the signature wasn't found.
    /// </summary>
    public static nint Address(string signature, string? rel32 = null)
    {
        if (Matches.TryGetValue(signature, out var m))
            return rel32 is null ? m.Address : m.Target(rel32);
        Problems.Add(Failures.GetValueOrDefault(signature, $"Signature {signature} wasn't looked for"));
        return 0;
    }

    /// <summary>
    /// Gets the target of a rel32 that several signatures may capture (a global, a function), checking that they agree.
    /// Records a problem and returns 0 if none captured it.
    /// </summary>
    public static nint Target(string rel32)
    {
        var targets = Matches.Values
                             .Where(m => m.Signature.Types.GetValueOrDefault(rel32) == "rel32" && m.Captured(rel32))
                             .Select(m => (m.Signature.Name, Target: m.Target(rel32)))
                             .ToList();
        if (targets.Count == 0)
        {
            Problems.Add($"no signature found says where {rel32} is");
            return 0;
        }

        if (targets.Any(t => t.Target != targets[0].Target))
        {
            Problems.Add($"{rel32} differs by signature: " + string.Join(", ", targets.Select(t => $"{t.Target:X} by {t.Name}")));
            return 0;
        }

        return targets[0].Target;
    }

    /// <summary>Gets a signature's match, or null (with a problem recorded) if it wasn't found.</summary>
    public static CodeMatch? Match(string signature)
    {
        if (Matches.TryGetValue(signature, out var m))
            return m;
        Problems.Add(Failures.GetValueOrDefault(signature, $"Signature {signature} wasn't looked for"));
        return null;
    }

    /// <summary>
    /// Checks a C# structure's size and field offsets against what was captured of them, under <paramref name="prefix"/>
    /// (its size) and <c>prefix.Field</c>; records a problem for each difference.
    /// </summary>
    public static void CheckFixed(string prefix, Type type)
    {
        if (TryGet(prefix) is { } size && size != type.StructLayoutAttribute!.Size)
            Problems.Add($"{prefix} is 0x{size:X} bytes in the game, but 0x{type.StructLayoutAttribute.Size:X} in the plugin");

        foreach (var (name, (value, _)) in Values)
        {
            if (!name.StartsWith(prefix + ".", StringComparison.Ordinal) || type.GetField(name[(prefix.Length + 1)..]) is not { } field)
                continue;
            if (Conflicts.TryGetValue(name, out var conflict))
                Problems.Add(conflict);
            else if (field.GetCustomAttributes(typeof(FieldOffsetAttribute), false) is [FieldOffsetAttribute a] && a.Value != value)
                Problems.Add($"{name} is 0x{value:X} in the game, but 0x{a.Value:X} in the plugin");
        }
    }

    /// <summary>Finds every signature, and merges their captures.</summary>
    private static void Load()
    {
        if (loaded)
            return;
        loaded = true;

        var found = new ConcurrentDictionary<string, CodeMatch>();
        var failed = new ConcurrentDictionary<string, string>();
        Parallel.ForEach(CodeSignature.Definitions, s =>
        {
            try
            {
                found[s.Name] = s.Find();
            }
            catch (InvalidOperationException ex)
            {
                failed[s.Name] = ex.Message;
            }
        });

        foreach (var (name, message) in failed.OrderBy(x => x.Key))
        {
            Failures[name] = message;
            Plugin.Log.Warning("{Message}", message);
        }

        foreach (var match in found.Values.OrderBy(m => m.Signature.Name))
        {
            Matches[match.Signature.Name] = match;
            foreach (var (capture, type) in match.Signature.Types)
            {
                if (type is not ("offset" or "size" or "count") || !match.Captured(capture))
                    continue;
                var value = match.Int(capture);
                if (!Values.TryGetValue(capture, out var known))
                {
                    Values[capture] = (value, match.Signature.Name);
                }
                else if (known.Value != value)
                {
                    AddConflict(capture, $"{capture} is 0x{known.Value:X} by {known.Source} but 0x{value:X} by {match.Signature.Name}");
                }
            }
        }

        // A field captured relative to another ("Structure.Field-Structure.Other") is that field where the other is known,
        // and must agree with what captured the field itself.
        foreach (var (capture, (difference, source)) in Values.ToArray())
        {
            var dash = capture.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0 || Conflicts.ContainsKey(capture))
                continue;
            var (field, other) = (capture[..dash], capture[(dash + 1)..]);
            if (Conflicts.ContainsKey(other) || !Values.TryGetValue(other, out var basis))
                continue;
            var value = basis.Value + difference;
            if (!Values.TryGetValue(field, out var known))
                Values[field] = (value, $"{source} (relative to {other})");
            else if (known.Value != value)
                AddConflict(field, $"{field} is 0x{known.Value:X} by {known.Source} but 0x{value:X} by {source} (relative to {other})");
        }
    }

    private static void AddConflict(string name, string message)
    {
        if (!Conflicts.TryAdd(name, message))
            return;
        Plugin.Log.Warning("{Message}", message);
    }
}
