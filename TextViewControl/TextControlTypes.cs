using System.Collections.Generic;
using System.Text.RegularExpressions;
using Windows.UI;

namespace TextViewControl;

public class SyntaxHighlightLanguage
{
    public string Name { get; set; } = "";
    public List<HighlightRule> Highlights { get; set; } = new();
}

public class HighlightRule
{
    public Regex regex { get; }

    public HighlightRule(string pattern)
    {
        regex = new Regex(pattern, RegexOptions.Multiline | RegexOptions.Compiled);
    }

    public Color ColorLight { get; set; }
    public Color ColorDark { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
}

// Public for an identical interface, not used by the control
public struct HighlightSpan
{
    public int Start { get; }
    public int Length { get; }
    public Color ColorLight { get; }
    public Color ColorDark { get; }
    public bool Bold { get; }
    public bool Italic { get; }
    public bool Underline { get; }

    public HighlightSpan(int start, int length, Color colorLight, Color colorDark, bool bold = false, bool italic = false, bool underline = false)
    {
        Start = start;
        Length = length;
        ColorLight = colorLight;
        ColorDark = colorDark;
        Bold = bold;
        Italic = italic;
        Underline = underline;
    }
}

public class TextControlDesign
{
    public Color TextColor { get; set; }
    public Color SelectionColor { get; set; }
    public Color CursorColor { get; set; }
    public Color LineNumberColor { get; set; }
    public Color SearchHighlightColor { get; set; }
    public Color SearchCurrentHighlightColor { get; set; }
}

// CurrentMatch: 1-based number of the match jumped to, 0 = none
public readonly record struct SearchStatus(bool SearchStarted, int Matches, int CurrentMatch);
