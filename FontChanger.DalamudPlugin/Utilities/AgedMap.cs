using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using FontChanger.DalamudPlugin.Fonts;

namespace FontChanger.DalamudPlugin.Utilities;

/// <summary>A cache that, once it holds more than its capacity, lets go of the entries used least recently.</summary>
/// <remarks>
/// <ul>
/// <li>Those are what the game is least likely to lay out again, such as a chat log's oldest lines or damage numbers gone from the screen.</li>
/// <li>A sweep goes down to <see cref="AgedMap.KeptShare"/> of the capacity, so that it scans the entries only now and then.</li>
/// </ul>
/// </remarks>
/// <typeparam name="TKey">The key.</typeparam>
/// <typeparam name="TValue">The value.</typeparam>
/// <param name="state">What gives the frame entries are marked used in.</param>
/// <param name="capacity">Entries kept however long ago they were used.</param>
internal sealed class AgedMap<TKey, TValue>(FrameState state, int capacity = AgedMap.DefaultCapacity)
    where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> _entries = [];

    // Where the next sweep starts, so that entries that can't go aren't scanned every frame.
    private readonly int _capacity = capacity;
    private int _sweepAt = capacity;

    public int Count => this._entries.Count;

    /// <summary>Marks the entry used in this frame.</summary>
    public bool TryGetValue(TKey key, out TValue value)
    {
        ref var entry = ref CollectionsMarshal.GetValueRefOrNullRef(this._entries, key);
        if (Unsafe.IsNullRef(ref entry))
        {
            value = default!;
            return false;
        }

        entry.Used = state.Frame;
        value = entry.Value;
        return true;
    }

    /// <summary>The key must not be in the map yet.</summary>
    public TValue Add(TKey key, TValue value)
    {
        this._entries.Add(key, new(value, state.Frame));
        return value;
    }

    /// <summary>Calls <paramref name="drop"/> for the value if the key was in the map.</summary>
    public void Remove(TKey key, Action<TValue>? drop)
    {
        if (this._entries.Remove(key, out var e))
            drop?.Invoke(e.Value);
    }

    /// <summary>Calls <paramref name="drop"/> for each value, then empties the map.</summary>
    public void Clear(Action<TValue>? drop)
    {
        if (drop is not null)
        {
            foreach (var e in this._entries.Values)
                drop(e.Value);
        }

        this._entries.Clear();
        this._sweepAt = this._capacity;
    }

    /// <summary>Entries used least recently go, if <paramref name="canDrop"/> holds for them, calling <paramref name="drop"/> for each.</summary>
    public void Sweep(Func<TValue, bool>? canDrop, Action<TValue>? drop)
    {
        if (this._entries.Count <= this._sweepAt)
            return;

        var used = new List<long>(this._entries.Count);
        this.CollectUsed(used);
        this.DropUsedBefore(AgedMap.Threshold(used, this._capacity, state.Frame), canDrop, drop);
        this._sweepAt = AgedMap.NextSweepAt(this._entries.Count, this._capacity);
    }

    /// <summary>Adds when each entry was last used to <paramref name="used"/>, for maps sharing a capacity.</summary>
    public void CollectUsed(List<long> used)
    {
        foreach (var e in this._entries.Values)
            used.Add(e.Used);
    }

    /// <summary>Entries last used before <paramref name="frame"/> go, if <paramref name="canDrop"/> holds for them, calling <paramref name="drop"/> for each.</summary>
    public void DropUsedBefore(long frame, Func<TValue, bool>? canDrop, Action<TValue>? drop)
    {
        foreach (var (key, e) in this._entries)
        {
            if (e.Used >= frame || !(canDrop?.Invoke(e.Value) ?? true))
                continue;
            drop?.Invoke(e.Value);
            this._entries.Remove(key);
        }
    }

    private struct Entry(TValue value, long used)
    {
        public readonly TValue Value = value;

        // The frame it was last used in.
        public long Used = used;
    }
}

/// <summary>What the maps share, and what maps sharing a capacity sweep with.</summary>
internal static class AgedMap
{
    public const int DefaultCapacity = 8192;

    /// <summary>The share of the capacity a sweep keeps.</summary>
    public const float KeptShare = 0.75f;

    /// <summary>Entries last used before the returned frame go to bring <paramref name="used"/> down to <see cref="KeptShare"/> of <paramref name="capacity"/>.</summary>
    /// <remarks>Never this frame's, as their glyphs may still be handed out.</remarks>
    public static long Threshold(List<long> used, int capacity, long frame)
    {
        var excess = used.Count - (int)(capacity * KeptShare);
        if (excess <= 0)
            return long.MinValue;
        if (excess >= used.Count)
            return frame;
        var span = CollectionsMarshal.AsSpan(used);
        span.Sort();
        return Math.Min(span[excess], frame);
    }

    /// <summary>Above the capacity, and above what is left by a quarter of it, as what is left couldn't go.</summary>
    public static int NextSweepAt(int count, int capacity) => Math.Max(capacity, count + (capacity / 4));
}
