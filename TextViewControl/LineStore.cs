using System;
using System.Collections.Generic;

namespace TextViewControl;

// All lines of the content. An array with a start offset: removing the oldest lines (buffer, monitoring)
// only moves the start, nothing is copied. Indices are always relative to the current first line.
internal sealed class LineStore
{
    private string[] Items = Array.Empty<string>();
    private int Head;

    public int Count { get; private set; }

    public string this[int index]
    {
        get => Items[Head + index];
        set => Items[Head + index] = value;
    }

    // Takes over an array filled from index 0 with 'count' lines (no copy)
    public static LineStore FromArray(string[] items, int count)
    {
        return new LineStore { Items = items, Count = count };
    }

    // Reads all lines into a new store, usable on a background thread
    public static LineStore FromEnumerable(IEnumerable<string> lines)
    {
        string[] items = lines switch
        {
            ICollection<string> collection => new string[collection.Count],
            IReadOnlyCollection<string> collection => new string[collection.Count],
            _ => new string[1024]
        };
        int count = 0;
        foreach (string line in lines)
        {
            if (count == items.Length) Array.Resize(ref items, (int)Math.Min(Array.MaxLength, Math.Max(1024L, items.Length * 2L)));
            items[count++] = line ?? string.Empty;
        }
        return FromArray(items, count);
    }

    public void Add(string line)
    {
        EnsureCapacity(Count + 1);
        Items[Head + Count] = line;
        Count++;
    }

    public void AddRange(IReadOnlyList<string> lines)
    {
        EnsureCapacity(Count + lines.Count);
        for (int i = 0; i < lines.Count; i++) Items[Head + Count + i] = lines[i] ?? string.Empty;
        Count += lines.Count;
    }

    public void Insert(int index, string line) => InsertRange(index, new[] { line });

    public void InsertRange(int index, IReadOnlyList<string> lines)
    {
        EnsureCapacity(Count + lines.Count);
        Array.Copy(Items, Head + index, Items, Head + index + lines.Count, Count - index);
        for (int i = 0; i < lines.Count; i++) Items[Head + index + i] = lines[i] ?? string.Empty;
        Count += lines.Count;
    }

    public void RemoveAt(int index)
    {
        Array.Copy(Items, Head + index + 1, Items, Head + index, Count - index - 1);
        Count--;
        Items[Head + Count] = null!;
    }

    // Removes the oldest lines without moving the others
    public void RemoveFirst(int count)
    {
        count = Math.Min(count, Count);
        if (count <= 0) return;

        Array.Clear(Items, Head, count);
        Head += count;
        Count -= count;
        if (Count == 0) Head = 0;
    }

    // Keeps only the newest 'count' lines
    public void KeepLast(int count)
    {
        if (Count > count) RemoveFirst(Count - count);
    }

    private void EnsureCapacity(int needed)
    {
        if (Head + needed <= Items.Length) return;

        // Enough room without the removed lines at the start: move to the front instead of growing
        if (needed <= Items.Length && Head >= Items.Length / 4)
        {
            Array.Copy(Items, Head, Items, 0, Count);
            Array.Clear(Items, Count, Head);
            Head = 0;
            return;
        }

        long capacity = Math.Max(needed, Math.Max(1024L, Items.Length * 2L));
        var items = new string[(int)Math.Min(Array.MaxLength, capacity)];
        Array.Copy(Items, Head, items, 0, Count);
        Items = items;
        Head = 0;
    }
}
