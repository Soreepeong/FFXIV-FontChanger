using System;
using System.Collections.Generic;

namespace FontChanger.DalamudPlugin.Utilities;

/// <summary>A cache that, while it holds more than its capacity, lets go of the entries used least recently.</summary>
/// <remarks>
/// <ul>
/// <li>Those are what the game is least likely to lay out again, such as a chat log's oldest lines or damage numbers gone from the screen.</li>
/// <li>Each <see cref="Sweep"/> lets go of a bounded number.</li>
/// </ul>
/// </remarks>
/// <typeparam name="TKey">The key.</typeparam>
/// <typeparam name="TValue">The value.</typeparam>
/// <param name="capacity">Entries kept however long ago they were used.</param>
internal sealed class AgedMap<TKey, TValue>(int capacity = AgedMap<TKey, TValue>.DefaultCapacity)
    where TKey : notnull
{
    public const int DefaultCapacity = 8192;

    // Let go of per Sweep at most, besides twice the entries added since the one before.
    private const int SweepEntries = 32;

    // Most recently used first.
    private readonly LinkedList<Entry> _order = [];
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _entries = [];
    private int _added;

    public int Count => this._entries.Count;

    public IEnumerable<TKey> Keys => this._entries.Keys;

    /// <summary>Marks the entry used in <paramref name="frame"/>.</summary>
    public bool TryGetValue(TKey key, long frame, out TValue value)
    {
        if (!this._entries.TryGetValue(key, out var node))
        {
            value = default!;
            return false;
        }

        this._order.Remove(node);
        this._order.AddFirst(node);
        node.Value.Used = frame;
        value = node.Value.Value;
        return true;
    }

    /// <summary>The key must not be in the map yet.</summary>
    public TValue Add(TKey key, TValue value, long frame)
    {
        this._entries.Add(key, this._order.AddFirst(new Entry(key, value, frame)));
        this._added++;
        return value;
    }

    /// <summary>Calls <paramref name="drop"/> for each value, then empties the map.</summary>
    public void Clear(Action<TValue>? drop)
    {
        if (drop is not null)
        {
            foreach (var e in this._order)
                drop(e.Value);
        }

        this._order.Clear();
        this._entries.Clear();
        this._added = 0;
    }

    /// <summary>
    /// Entries used least recently, before <paramref name="frame"/>, go if <paramref name="canDrop"/> holds for them, calling
    /// <paramref name="drop"/> for each. One that can't is treated as used.
    /// </summary>
    public void Sweep(long frame, Func<TValue, bool>? canDrop, Action<TValue>? drop)
    {
        var budget = SweepEntries + (2 * this._added);
        this._added = 0;
        for (; budget > 0 && this._entries.Count > capacity; budget--)
        {
            var node = this._order.Last!;
            if (node.Value.Used == frame)
                break;

            this._order.RemoveLast();
            if (canDrop?.Invoke(node.Value.Value) ?? true)
            {
                drop?.Invoke(node.Value.Value);
                this._entries.Remove(node.Value.Key);
            }
            else
            {
                this._order.AddFirst(node);
            }
        }
    }

    private sealed class Entry(TKey key, TValue value, long used)
    {
        public TKey Key { get; } = key;

        public TValue Value { get; } = value;

        /// <summary>Gets or sets the frame it was last used in.</summary>
        public long Used { get; set; } = used;
    }
}
