using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TextViewControl;

// Checks lines against the highlight rules (color filter)
internal static class HighlightMatcher
{
    private const int BlockSize = 65536;

    public static bool IsMatch(IReadOnlyList<HighlightRule> rules, string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int i = 0; i < rules.Count; i++)
        {
            if (rules[i].regex.IsMatch(text)) return true;
        }
        return false;
    }

    // Indices of all matching lines in [from, count) of the store, ascending, written from index 0 of the result.
    // Large contents are checked on all cores, every thread with own Regex objects (a shared Regex is not faster in parallel).
    // Also the longest matching line (LineModel.GetColumns, the first one of equal length) as position in the result,
    // -1 if all matching lines are empty: the lines are read here anyway, a separate scan would read them again.
    public static int[] FindMatching(LineStore store, IReadOnlyList<HighlightRule> rules, int from, out int found,
        out int longest, out int longestColumns)
    {
        int count = store.Count - from;
        found = 0;
        longest = -1;
        longestColumns = 0;
        if (count <= 0 || rules.Count == 0) return Array.Empty<int>();

        if (count < 2 * BlockSize)
        {
            var result = new int[Math.Min(count, 1024)];
            for (int i = from; i < store.Count; i++)
            {
                string text = store[i];
                if (!IsMatch(rules, text)) continue;
                int columns = LineModel.GetColumns(text);
                if (columns > longestColumns)
                {
                    longestColumns = columns;
                    longest = found;
                }
                if (found == result.Length) Array.Resize(ref result, Math.Min(count, result.Length * 2));
                result[found++] = i;
            }
            return result;
        }

        var patterns = new List<(string Pattern, RegexOptions Options)>(rules.Count);
        foreach (var rule in rules) patterns.Add((rule.regex.ToString(), rule.regex.Options & ~RegexOptions.Compiled));

        int blocks = (count + BlockSize - 1) / BlockSize;
        var flags = new bool[count];
        var blockCounts = new int[blocks];
        var blockLongest = new int[blocks];        // position of the longest match within the block
        var blockLongestColumns = new int[blocks];

        Parallel.For(0, blocks,
            () => patterns.ConvertAll(p => new Regex(p.Pattern, p.Options)),
            (block, _, regexes) =>
            {
                int end = Math.Min(count, (block + 1) * BlockSize), matches = 0, bestColumns = 0, best = -1;
                for (int i = block * BlockSize; i < end; i++)
                {
                    string text = store[from + i];
                    if (string.IsNullOrEmpty(text)) continue;
                    foreach (var regex in regexes)
                    {
                        if (regex.IsMatch(text))
                        {
                            int columns = LineModel.GetColumns(text);
                            if (columns > bestColumns)
                            {
                                bestColumns = columns;
                                best = matches;
                            }
                            flags[i] = true;
                            matches++;
                            break;
                        }
                    }
                }
                blockCounts[block] = matches;
                blockLongest[block] = best;
                blockLongestColumns[block] = bestColumns;
                return regexes;
            },
            _ => { });

        var offsets = new int[blocks];
        for (int block = 0; block < blocks; block++)
        {
            offsets[block] = found;
            if (blockLongestColumns[block] > longestColumns) // ascending with '>': the first longest line wins
            {
                longestColumns = blockLongestColumns[block];
                longest = found + blockLongest[block];
            }
            found += blockCounts[block];
        }

        var indices = new int[Math.Max(found, 1)];
        Parallel.For(0, blocks, block =>
        {
            int end = Math.Min(count, (block + 1) * BlockSize), target = offsets[block];
            for (int i = block * BlockSize; i < end; i++)
                if (flags[i]) indices[target++] = from + i;
        });
        return indices;
    }
}
