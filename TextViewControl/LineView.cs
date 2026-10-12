using System;

namespace TextViewControl;

// The displayed lines as positions in the LineStore: either a range (no color filter) or a list of
// ascending indices (color filter). Removing the first displayed entries does not move the others.
internal sealed class LineView
{
    private bool IsRange = true;
    private int RangeStart;

    private int[] Indices = Array.Empty<int>();
    private int Head;

    public int Count { get; private set; }

    public int SourceIndex(int displayed) => IsRange ? RangeStart + displayed : Indices[Head + displayed];

    public void SetRange(int start, int count)
    {
        IsRange = true;
        Indices = Array.Empty<int>();
        Head = 0;
        RangeStart = start;
        Count = count;
    }

    // Takes over 'count' ascending indices from index 0 of the array
    public void SetIndices(int[] indices, int count)
    {
        IsRange = false;
        Indices = indices;
        Head = 0;
        Count = count;
    }

    // Range: the displayed lines grow by 'count' at the end
    public void ExtendRange(int count) => Count += count;

    public void AddIndex(int sourceIndex)
    {
        if (Head + Count == Indices.Length)
        {
            if (Head > 0 && Count <= Indices.Length / 2)
            {
                Array.Copy(Indices, Head, Indices, 0, Count);
                Head = 0;
            }
            else
            {
                var indices = new int[Math.Max(256, Indices.Length * 2)];
                Array.Copy(Indices, Head, indices, 0, Count);
                Indices = indices;
                Head = 0;
            }
        }
        Indices[Head + Count] = sourceIndex;
        Count++;
    }

    // Removes the first 'count' displayed lines
    public void RemoveFirst(int count)
    {
        count = Math.Min(count, Count);
        if (IsRange) RangeStart += count;
        else Head += count;
        Count -= count;
        if (!IsRange && Count == 0) Head = 0;
    }

    // Number of displayed lines among the first 'count' lines of the store
    public int CountBefore(int count)
    {
        if (IsRange) return Math.Clamp(count - RangeStart, 0, Count);

        // Ascending indices: binary search for the first index >= count
        int low = 0, high = Count;
        while (low < high)
        {
            int mid = (low + high) >> 1;
            if (Indices[Head + mid] < count) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    // The first 'count' lines of the store were removed: all positions move to the front
    public void ShiftSource(int count)
    {
        if (IsRange)
        {
            RangeStart = Math.Max(0, RangeStart - count);
            return;
        }
        for (int i = Head; i < Head + Count; i++) Indices[i] -= count;
    }
}
