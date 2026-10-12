using System;
using System.Threading;
using System.Threading.Tasks;

namespace TextViewControl;

// Plain text search (no regex) in a list of lines given by count and accessor
internal sealed class TextSearch
{
    private const int BlockSize = 65536;

    public string Text { get; }
    public bool WholeWord { get; }
    public StringComparison Comparison { get; }

    public TextSearch(string text, bool wholeWord, bool matchCase)
    {
        Text = text;
        WholeWord = wholeWord;
        Comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }

    public int Length => Text.Length;

    // First match starting at or after 'start', -1 if none
    public int FindInLine(string line, int start)
    {
        if (string.IsNullOrEmpty(line)) return -1;
        while (start >= 0 && start <= line.Length - Text.Length)
        {
            int index = line.IndexOf(Text, start, Comparison);
            if (index < 0) return -1;
            if (!WholeWord || IsWholeWordAt(line, index)) return index;
            start = index + 1;
        }
        return -1;
    }

    // Last match starting before 'before', -1 if none. Same non overlapping matches as forward and counting.
    public int FindLastInLine(string line, int before)
    {
        int last = -1;
        for (int index = FindInLine(line, 0); index >= 0 && index < before; index = FindInLine(line, index + Text.Length))
            last = index;
        return last;
    }

    // Non overlapping matches in a line
    public int CountInLine(string line)
    {
        int count = 0;
        for (int index = FindInLine(line, 0); index >= 0; index = FindInLine(line, index + Text.Length)) count++;
        return count;
    }

    private bool IsWholeWordAt(string line, int index)
    {
        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
        int end = index + Text.Length;
        return (index == 0 || !IsWordChar(line[index - 1])) && (end >= line.Length || !IsWordChar(line[end]));
    }

    // Matches in the lines [from, to), large ranges on all cores
    public int Count(Func<int, string> lineAt, int from, int to)
    {
        if (to - from < 2 * BlockSize)
        {
            int count = 0;
            for (int i = from; i < to; i++) count += CountInLine(lineAt(i));
            return count;
        }

        int total = 0;
        Parallel.For(0, (to - from + BlockSize - 1) / BlockSize, block =>
        {
            int start = from + block * BlockSize, end = Math.Min(to, start + BlockSize), count = 0;
            for (int i = start; i < end; i++) count += CountInLine(lineAt(i));
            Interlocked.Add(ref total, count);
        });
        return total;
    }

    // First match at or after (line, start). The next match is usually close: one block sequentially,
    // the rest in parallel blocks, the first block with a match wins.
    public bool FindForward(Func<int, string> lineAt, int lineCount, int line, int start, out int foundLine, out int foundIndex)
    {
        foundLine = -1;
        foundIndex = -1;
        line = Math.Max(0, line);

        int sequentialEnd = Math.Min(lineCount, line + BlockSize);
        for (; line < sequentialEnd; line++, start = 0)
        {
            int index = FindInLine(lineAt(line), start);
            if (index >= 0)
            {
                foundLine = line;
                foundIndex = index;
                return true;
            }
        }
        if (line >= lineCount) return false;

        int first = line;
        int blocks = (lineCount - first + BlockSize - 1) / BlockSize;
        var lines = new int[blocks];
        var indices = new int[blocks];
        Array.Fill(lines, -1); // blocks after a Break() may not run at all
        Parallel.For(0, blocks, (block, state) =>
        {
            int end = Math.Min(lineCount, first + (block + 1) * BlockSize);
            for (int i = first + block * BlockSize; i < end; i++)
            {
                int index = FindInLine(lineAt(i), 0);
                if (index >= 0)
                {
                    lines[block] = i;
                    indices[block] = index;
                    state.Break();
                    return;
                }
            }
        });

        for (int block = 0; block < blocks; block++)
        {
            if (lines[block] < 0) continue;
            foundLine = lines[block];
            foundIndex = indices[block];
            return true;
        }
        return false;
    }

    // Last match before (line, before), or the last match in an earlier line
    public bool FindBackward(Func<int, string> lineAt, int lineCount, int line, int before, out int foundLine, out int foundIndex)
    {
        foundLine = -1;
        foundIndex = -1;
        line = Math.Min(line, lineCount - 1);

        int sequentialEnd = Math.Max(-1, line - BlockSize);
        for (; line > sequentialEnd; line--, before = int.MaxValue)
        {
            int index = FindLastInLine(lineAt(line), before);
            if (index >= 0)
            {
                foundLine = line;
                foundIndex = index;
                return true;
            }
        }
        if (line < 0) return false;

        // Block 0 is the closest one to 'line'
        int last = line;
        int blocks = (last + BlockSize) / BlockSize;
        var lines = new int[blocks];
        var indices = new int[blocks];
        Array.Fill(lines, -1);
        Parallel.For(0, blocks, (block, state) =>
        {
            int end = Math.Max(-1, last - (block + 1) * BlockSize);
            for (int i = last - block * BlockSize; i > end; i--)
            {
                int index = FindLastInLine(lineAt(i), int.MaxValue);
                if (index >= 0)
                {
                    lines[block] = i;
                    indices[block] = index;
                    state.Break();
                    return;
                }
            }
        });

        for (int block = 0; block < blocks; block++)
        {
            if (lines[block] < 0) continue;
            foundLine = lines[block];
            foundIndex = indices[block];
            return true;
        }
        return false;
    }
}
