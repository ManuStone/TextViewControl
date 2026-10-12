using System;
using System.Collections.Generic;

namespace TextViewControl;

// Gets told how the displayed lines change, so positions (scroll, cursor, selection, search) can follow
internal interface ILineModelObserver
{
    // The first 'count' displayed lines are about to disappear (they can still be read)
    void BeforeRemoveFront(int count);

    // The first 'count' displayed lines are gone, all displayed indices moved by 'count' to the front
    void AfterRemoveFront(int count);

    // 'count' displayed lines were added at the end, starting at displayed index 'from'
    void Appended(int from, int count);

    // The displayed lines were built again, displayed indices of before are no longer valid
    void Rebuilt();

    // Edits without rebuilding the displayed lines (no filter, or SetLine without color filter)
    void Changed(int index);
    void Inserted(int index, int count);
    void Deleted(int index);
}

// All lines (LineStore) and the displayed lines (LineView) with the rules for filter and buffer
internal sealed class LineModel
{
    private readonly ILineModelObserver Observer;
    private readonly Func<IReadOnlyList<HighlightRule>> GetRules;

    public LineStore Store { get; private set; } = new();
    public LineView View { get; } = new();

    public bool FilterHighlights { get; set; }
    public int MaxLines { get; set; }
    public bool Monitoring { get; set; }
    public int MaxTotalLines { get; set; }
    public int MaxTotalLinesPercent { get; set; } = 10;

    public LineModel(ILineModelObserver observer, Func<IReadOnlyList<HighlightRule>> getRules)
    {
        Observer = observer;
        GetRules = getRules;
    }

    // Longest displayed line (displayed index, -1 = all empty, and its columns), when Rebuild found it on the way.
    // null: not determined, the control searches itself.
    public (int Line, int Columns)? RebuiltLongest { get; private set; }

    public int DisplayCount => View.Count;
    public int TotalCount => Store.Count;
    public string GetDisplayed(int index) => Store[View.SourceIndex(index)];

    // Replaces the content, keeps only the newest MaxTotalLines and builds the displayed lines
    public void SetContent(LineStore store)
    {
        Store = store;
        if (MaxTotalLines > 0) Store.KeepLast(MaxTotalLines);
        Rebuild();
    }

    // Builds the displayed lines from all lines according to the filter
    public void Rebuild()
    {
        int total = Store.Count;
        RebuiltLongest = null;

        if (!FilterHighlights)
        {
            int count = (MaxLines > 0) ? Math.Min(MaxLines, total) : total;
            int start = (MaxLines > 0 && Monitoring) ? total - count : 0;
            View.SetRange(start, count);
        }
        else if (MaxLines > 0)
        {
            // The first or (Monitoring) the last MaxLines matching lines, stops as soon as there are enough
            var rules = GetRules();
            var indices = new int[Math.Min(MaxLines, Math.Max(total, 1))];
            int found = 0;
            if (Monitoring)
            {
                for (int i = total - 1; i >= 0 && found < MaxLines; i--)
                    if (HighlightMatcher.IsMatch(rules, Store[i])) indices[found++] = i;
                Array.Reverse(indices, 0, found);
            }
            else
            {
                for (int i = 0; i < total && found < MaxLines; i++)
                    if (HighlightMatcher.IsMatch(rules, Store[i])) indices[found++] = i;
            }
            View.SetIndices(indices, found);
        }
        else
        {
            int[] indices = HighlightMatcher.FindMatching(Store, GetRules(), 0, out int found, out int longest, out int longestColumns);
            View.SetIndices(indices, found);
            RebuiltLongest = (longest, longestColumns);
        }

        Observer.Rebuilt();
    }

    public void Append(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;

        int firstNew = Store.Count;
        Store.AddRange(lines);
        AppendToView(firstNew);
        LimitTotalLines(force: false);
    }

    // The lines from 'firstNew' on were added to the store: the displayed ones are added to the view
    private void AppendToView(int firstNew)
    {
        int oldCount = View.Count;
        int newLines = Store.Count - firstNew;

        if (!FilterHighlights)
        {
            int add = (MaxLines > 0 && !Monitoring) ? Math.Clamp(MaxLines - View.Count, 0, newLines) : newLines;
            View.ExtendRange(add);
        }
        else
        {
            var rules = GetRules();
            for (int i = firstNew; i < Store.Count; i++)
            {
                if (MaxLines > 0 && !Monitoring && View.Count >= MaxLines) break; // the first MaxLines stay
                if (HighlightMatcher.IsMatch(rules, Store[i])) View.AddIndex(i);
            }
        }

        if (View.Count > oldCount) Observer.Appended(oldCount, View.Count - oldCount);

        // Monitoring: only the newest MaxLines are displayed
        if (MaxLines > 0 && Monitoring && View.Count > MaxLines) RemoveDisplayedFront(View.Count - MaxLines);
    }

    private void RemoveDisplayedFront(int count)
    {
        if (count <= 0) return;
        Observer.BeforeRemoveFront(count);
        View.RemoveFirst(count);
        Observer.AfterRemoveFront(count);
    }

    // Removes the oldest lines if there are more than MaxTotalLines (+ percent), force: exactly down to the limit
    public void LimitTotalLines(bool force)
    {
        int remove = GetRemoveCount(Store.Count, MaxTotalLines, force ? 0 : MaxTotalLinesPercent);
        if (remove > 0) RemoveFirstLines(remove);
    }

    // Removes the oldest lines of the content, also hidden ones
    public void RemoveFirstLines(int count)
    {
        count = Math.Min(count, Store.Count);
        if (count <= 0) return;

        // The first MaxLines lines are displayed and the limit is reached: lines after the removed ones
        // move into the view, so the displayed lines are selected again
        if (MaxLines > 0 && !Monitoring && View.Count >= MaxLines)
        {
            Store.RemoveFirst(count);
            Rebuild();
            return;
        }

        RemoveDisplayedFront(View.CountBefore(count));
        Store.RemoveFirst(count);
        View.ShiftSource(count);
    }

    // Edits by displayed index (already checked by the caller). Returns true if the displayed lines were rebuilt.
    public bool SetLine(int index, string line)
    {
        Store[View.SourceIndex(index)] = line;
        if (!FilterHighlights)
        {
            Observer.Changed(index);
            return false;
        }

        Rebuild(); // the line may be hidden now
        return true;
    }

    public bool InsertLines(int index, IReadOnlyList<string> lines)
    {
        Store.InsertRange(View.SourceIndex(index), lines);
        bool rebuilt = HasFilter;
        if (rebuilt) Rebuild();
        else
        {
            View.ExtendRange(lines.Count);
            Observer.Inserted(index, lines.Count);
        }
        LimitTotalLines(force: false); // after the insert was reported, so the positions are right
        return rebuilt;
    }

    public bool DeleteLine(int index)
    {
        Store.RemoveAt(View.SourceIndex(index));
        if (HasFilter)
        {
            Rebuild();
            return true;
        }
        View.ExtendRange(-1);
        Observer.Deleted(index);
        return false;
    }

    public bool HasFilter => FilterHighlights || MaxLines > 0;

    // Width of a line in columns for finding the longest line (a tab counts as 6 characters)
    public static int GetColumns(string text) => text.Length + text.AsSpan().Count('\t') * 5;

    // Number of oldest entries to remove from 'count' entries: none up to 'limit' + 'percent' % of the limit,
    // then down to 'limit'. 0 without a limit.
    public static int GetRemoveCount(int count, int limit, int percent)
    {
        if (limit <= 0) return 0;
        long allowed = limit + (long)limit * Math.Max(0, percent) / 100;
        return (count > allowed) ? count - limit : 0;
    }
}
