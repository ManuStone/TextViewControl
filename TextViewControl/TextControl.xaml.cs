using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;

namespace TextViewControl;

// Read only text display for very many lines. Behavior: see Claude/TextControl-API.md, internals: Claude/TextControl-Intern.md.
//
// Scroll position: the top displayed line (TopLine) and how far it is scrolled out at the top (TopOffset, pixels).
// Without wrap every line has the same height, with wrap the height of a line is only measured for the lines
// that are drawn or passed while scrolling. Nothing depends on the height of the whole content.
public sealed partial class TextControl : UserControl, ILineModelObserver
{
    private readonly LineModel Model;
    private readonly DisplayedLines LinesView;

    public TextControl()
    {
        Model = new LineModel(this, () => highlightLanguage.Highlights);
        LinesView = new DisplayedLines(this);

        InitializeComponent();

        canvasLineNumbers.Visibility = showLineNumbers ? Visibility.Visible : Visibility.Collapsed;

        CursorBlinkTimer = DispatcherQueue.CreateTimer();
        CursorBlinkTimer.Interval = TimeSpan.FromMilliseconds(500);
        CursorBlinkTimer.IsRepeating = true;
        CursorBlinkTimer.Tick += (s, e) =>
        {
            CursorVisible = !CursorVisible;
            canvasText.Invalidate();
        };

        canvasText.IsTabStop = true;
        canvasText.PointerPressed += CanvasText_PointerPressed;
        canvasText.PointerMoved += CanvasText_PointerMoved;
        canvasText.PointerReleased += CanvasText_PointerReleased;
        canvasText.PointerCaptureLost += (s, e) => IsSelecting = false;
        canvasText.KeyDown += CanvasText_KeyDown;
        canvasText.SizeChanged += CanvasText_SizeChanged;

        Loaded += (s, e) => Invalidate();
        Unloaded += (s, e) => ReleaseResources();
    }

    #region Content

    public IReadOnlyList<string> Lines => LinesView;

    public int LineCount { get; private set; }

    public int TotalCount { get; private set; }

    public event EventHandler? LineCountChanged;

    public void SetLines(IEnumerable<string> items)
    {
        if (items == null)
        {
            Clear();
            return;
        }

        // A finished collection (List, array, also Lines of this control) is copied first: its strings already exist,
        // the copy only costs the references. Anything else (File.ReadLines, generators) is read after Clear,
        // so the old and the new strings are not in memory at the same time.
        LineStore? copy = (items is ICollection<string> || items is IReadOnlyCollection<string>) ? LineStore.FromEnumerable(items) : null;
        Clear();
        Model.SetContent(copy ?? LineStore.FromEnumerable(items));
        AfterContentReplaced();
    }

    public async Task LoadFileAsync(string path, Encoding? encoding = null)
    {
        Clear();
        var store = await Task.Run(() => LineStore.FromEnumerable(File.ReadLines(path, encoding ?? Encoding.UTF8)));
        Model.SetContent(store);
        AfterContentReplaced();
    }

    public void Clear()
    {
        bool large = Model.TotalCount >= 1_000_000;

        Model.SetContent(new LineStore());
        AfterContentReplaced();

        // Without a collection the memory of a large content stays in use for a long time
        if (large)
        {
            ClearLayoutCache();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }

    private void AfterContentReplaced()
    {
        ClearCursor();
        ClearSelection();
        ClickedLine = -1;
        PendingScroll = null;
        TopLine = 0;
        TopOffset = 0;
        HorizontalScrollbar.Value = 0;
        UpdateCount();
        Invalidate();
    }

    public void AddLine(string line)
    {
        if (line == null) return;
        Model.Append(new[] { line });
        UpdateCount();
        Invalidate();
    }

    public void AddLines(string[] lines)
    {
        if (lines == null) return;
        Model.Append(lines);
        UpdateCount();
        Invalidate();
    }

    public void SetLine(int index, string line)
    {
        if (line == null || Model.DisplayCount == 0) return;
        index = Math.Clamp(index, 0, Model.DisplayCount - 1);

        Model.SetLine(index, line);
        UpdateCount();
        Invalidate();
    }

    public void InsertLine(int index, string line)
    {
        if (line == null) return;
        InsertLines(index, new[] { line });
    }

    public void InsertLines(int index, string[] lines)
    {
        if (lines == null) return;
        index = Math.Clamp(index, 0, Model.DisplayCount);
        if (index == Model.DisplayCount)
        {
            AddLines(lines);
            return;
        }
        if (lines.Length == 0) return;

        Model.InsertLines(index, lines);
        UpdateCount();
        Invalidate();
    }

    public void DeleteLine(int index)
    {
        if (Model.DisplayCount == 0) return;
        index = Math.Clamp(index, 0, Model.DisplayCount - 1);

        Model.DeleteLine(index);
        UpdateCount();
        Invalidate();
    }

    public void RemoveFirstLines(int count)
    {
        Model.RemoveFirstLines(count);
        UpdateCount();
        Invalidate();
    }

    private void UpdateCount()
    {
        int lineCount = Model.DisplayCount;
        int totalCount = Model.TotalCount;
        if (lineCount == LineCount && totalCount == TotalCount) return;

        LineCount = lineCount;
        TotalCount = totalCount;
        LineCountChanged?.Invoke(this, EventArgs.Empty);
    }

    // The displayed lines as read only list
    private sealed class DisplayedLines(TextControl owner) : IReadOnlyList<string>
    {
        public int Count => owner.Model.DisplayCount;
        public string this[int index] => (uint)index < (uint)Count ? owner.Model.GetDisplayed(index) : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<string> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return owner.Model.GetDisplayed(i);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    #endregion

    #region Model observer (positions follow the displayed lines)

    void ILineModelObserver.BeforeRemoveFront(int count)
    {
        // The search counts its matches in the lines that disappear
        if (ActiveSearch != null && SearchStatus.SearchStarted)
        {
            int removed = ActiveSearch.Count(Model.GetDisplayed, 0, count);
            int current = (SearchLine >= count && SearchStatus.CurrentMatch > 0) ? SearchStatus.CurrentMatch - removed : 0;
            SetSearchStatus(new SearchStatus(true, Math.Max(0, SearchStatus.Matches - removed), Math.Max(0, current)));
        }
    }

    void ILineModelObserver.AfterRemoveFront(int count)
    {
        bool atBottom = IsAtBottom;

        SearchLine = (SearchLine >= count) ? SearchLine - count : -1;
        ShiftPositions(0, -count);

        if (LongestLine >= count) LongestLine -= count;
        else UpdateLongestLine();

        // The same lines stay visible, at the bottom it stays at the bottom
        if (TopLine >= count) TopLine -= count;
        else
        {
            TopLine = 0;
            TopOffset = 0;
        }
        if (atBottom) RunScroll(ScrollToBottomAction);
    }

    void ILineModelObserver.Appended(int from, int count)
    {
        if (ActiveSearch != null && SearchStatus.SearchStarted)
            SetSearchStatus(SearchStatus with { Matches = SearchStatus.Matches + ActiveSearch.Count(Model.GetDisplayed, from, from + count) });
        UpdateLongestLine(from, from + count);
    }

    void ILineModelObserver.Rebuilt()
    {
        // Displayed indices of before are not valid any more
        ClearSelection();
        ClearCursor();
        ClickedLine = -1;
        SearchLine = -1;
        if (ActiveSearch != null && SearchStatus.SearchStarted)
            SetSearchStatus(new SearchStatus(true, ActiveSearch.Count(Model.GetDisplayed, 0, Model.DisplayCount), 0));
        if (Model.RebuiltLongest is { } longest) // found while filtering, no second scan over all lines
        {
            LongestLine = longest.Line;
            LongestColumns = longest.Columns;
            LongestLineWidth = -1;
        }
        else UpdateLongestLine();

        // Vertical position kept as far as the content reaches (clamped when drawing), horizontal to the start
        if (TopLine >= Model.DisplayCount)
        {
            TopLine = Math.Max(0, Model.DisplayCount - 1);
            TopOffset = 0;
        }
        HorizontalScrollbar.Value = 0;
    }

    void ILineModelObserver.Changed(int index)
    {
        RecountSearch();
        if (index == LongestLine) UpdateLongestLine(); // may be shorter now
        else UpdateLongestLine(index, index + 1);
    }

    void ILineModelObserver.Inserted(int index, int count)
    {
        // The same lines stay visible, positions after the inserted lines move down
        ShiftPositions(index, count);
        if (TopLine >= index) TopLine += count;
        RecountSearch();
        if (LongestLine >= index) LongestLine += count;
        UpdateLongestLine(index, index + count);
    }

    void ILineModelObserver.Deleted(int index)
    {
        ShiftPositions(index, -1);
        if (TopLine > index) TopLine--;
        RecountSearch();
        if (index == LongestLine) UpdateLongestLine();
        else if (index < LongestLine) LongestLine--;
    }

    // Displayed lines from 'index' on moved by 'delta' (insert > 0, delete < 0 or removed at the start).
    // Positions in removed lines are dropped.
    private void ShiftPositions(int index, int delta)
    {
        bool Removed(int line) => delta < 0 && line >= index && line < index - delta;
        int Shift(int line) => line >= index ? line + delta : line;

        if (HasCursor)
        {
            if (Removed(CursorPosition.Line)) ClearCursor();
            else CursorPosition = new TextPosition(Shift(CursorPosition.Line), CursorPosition.Character);
        }

        if (SelectionAnchor.HasValue && SelectionActive.HasValue)
        {
            if (Removed(SelectionAnchor.Value.Line) || Removed(SelectionActive.Value.Line)) ClearSelection();
            else
            {
                SelectionAnchor = new TextPosition(Shift(SelectionAnchor.Value.Line), SelectionAnchor.Value.Character);
                SelectionActive = new TextPosition(Shift(SelectionActive.Value.Line), SelectionActive.Value.Character);
            }
        }

        if (ClickedLine >= 0) ClickedLine = Removed(ClickedLine) ? -1 : Shift(ClickedLine);
    }

    #endregion

    #region Filter and buffer

    private bool filterHighlights = false;
    public bool FilterHighlights
    {
        get => filterHighlights;
        set
        {
            if (filterHighlights == value) return;
            filterHighlights = value;
            Model.FilterHighlights = value;
            ApplyFilter();
        }
    }

    private int maxLines = 0;
    public int MaxLines
    {
        get => maxLines;
        set
        {
            if (maxLines == value) return;
            maxLines = value;
            Model.MaxLines = Math.Max(0, value);
            ApplyFilter();
        }
    }

    private bool monitoring = false;
    public bool Monitoring
    {
        get => monitoring;
        set
        {
            if (monitoring == value) return;
            monitoring = value;
            Model.Monitoring = value;
            if (MaxLines > 0) ApplyFilter(); // only selects the first or last MaxLines
        }
    }

    public bool HasFilter => FilterHighlights || MaxLines > 0;

    private void ApplyFilter()
    {
        Model.Rebuild();
        UpdateCount();
        Invalidate();
    }

    private int maxTotalLines = 0;
    public int MaxTotalLines
    {
        get => maxTotalLines;
        set
        {
            if (maxTotalLines == value) return;
            maxTotalLines = value;
            Model.MaxTotalLines = Math.Max(0, value);
            Model.LimitTotalLines(force: true);
            UpdateCount();
            Invalidate();
        }
    }

    private int maxTotalLinesPercent = 10;
    public int MaxTotalLinesPercent
    {
        get => maxTotalLinesPercent;
        set
        {
            if (maxTotalLinesPercent == value) return;
            maxTotalLinesPercent = value;
            Model.MaxTotalLinesPercent = value;
            Model.LimitTotalLines(force: false);
            UpdateCount();
            Invalidate();
        }
    }

    // Number of oldest entries to remove from 'count' entries: none up to 'limit' + 'percent' % of the limit,
    // then down to 'limit'. 0 without a limit.
    public static int GetRemoveCount(int count, int limit, int percent) => LineModel.GetRemoveCount(count, limit, percent);

    #endregion

    #region Appearance

    private double fontSize = 18;
    public new double FontSize
    {
        get => fontSize;
        set
        {
            if (fontSize == value) return;
            fontSize = value;
            FormatChanged();
        }
    }

    private FontFamily fontFamily = new FontFamily("Consolas");
    public new FontFamily FontFamily
    {
        get => fontFamily;
        set
        {
            if (fontFamily == value) return;
            fontFamily = value;
            FormatChanged();
        }
    }

    private float lineSpacing = 2f;
    public float LineSpacing
    {
        get => lineSpacing;
        set
        {
            if (lineSpacing == value) return;
            lineSpacing = value;
            FormatChanged();
        }
    }

    private bool textWrapEnabled = false;
    public bool TextWrapEnabled
    {
        get => textWrapEnabled;
        set
        {
            if (textWrapEnabled == value) return;
            bool atBottom = IsAtBottom;
            textWrapEnabled = value;
            HorizontalScrollbar.Value = 0;
            KeepPositionAfterLayoutChange(atBottom);
        }
    }

    private bool showLineNumbers = true;
    public bool ShowLineNumbers
    {
        get => showLineNumbers;
        set
        {
            if (showLineNumbers == value) return;
            showLineNumbers = value;
            canvasLineNumbers.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private float lineNumberGap = 16;
    public float LineNumberGap
    {
        get => lineNumberGap;
        set
        {
            if (lineNumberGap == value) return;
            lineNumberGap = value;
            Invalidate();
        }
    }

    private float lineNumberSpace = 6;
    public float LineNumberSpace
    {
        get => lineNumberSpace;
        set
        {
            if (lineNumberSpace == value) return;
            lineNumberSpace = value;
            Invalidate();
        }
    }

    private bool highlightsEnabled = true;
    public bool HighlightsEnabled
    {
        get => highlightsEnabled;
        set
        {
            if (highlightsEnabled == value) return;
            bool atBottom = IsAtBottom;
            highlightsEnabled = value;
            KeepPositionAfterLayoutChange(atBottom); // bold text can wrap differently
        }
    }

    private SyntaxHighlightLanguage highlightLanguage = new();
    public SyntaxHighlightLanguage HighlightLanguage
    {
        get => highlightLanguage;
        set
        {
            if (highlightLanguage == value) return;
            highlightLanguage = value ?? new();
            ClearLayoutCache();
            ApplyFilter();
        }
    }

    private ElementTheme colorTheme = ElementTheme.Dark;
    public ElementTheme ColorTheme
    {
        get => colorTheme;
        set
        {
            if (colorTheme == value) return;
            colorTheme = value;
            ClearColorResources();
            ClearLayoutCache();
            Invalidate();
        }
    }

    public TextControlDesign LightDesign = new()
    {
        TextColor = Color.FromArgb(255, 50, 50, 50),
        SelectionColor = Color.FromArgb(100, 0, 100, 255),
        CursorColor = Color.FromArgb(255, 0, 0, 0),
        LineNumberColor = Color.FromArgb(255, 180, 180, 180),
        SearchHighlightColor = Color.FromArgb(100, 0, 100, 255),
        SearchCurrentHighlightColor = Color.FromArgb(220, 255, 150, 0)
    };

    public TextControlDesign DarkDesign = new()
    {
        TextColor = Color.FromArgb(255, 255, 255, 255),
        SelectionColor = Color.FromArgb(100, 0, 100, 255),
        CursorColor = Color.FromArgb(255, 255, 255, 255),
        LineNumberColor = Color.FromArgb(255, 100, 100, 100),
        SearchHighlightColor = Color.FromArgb(100, 0, 100, 255),
        SearchCurrentHighlightColor = Color.FromArgb(220, 210, 110, 0)
    };

    private TextControlDesign Design => (ColorTheme == ElementTheme.Light) ? LightDesign : DarkDesign;

    private FlyoutBase? cachedContextFlyout = null;
    private bool flyoutMenuEnabled = true;
    public bool FlyoutMenuEnabled
    {
        get => flyoutMenuEnabled;
        set
        {
            if (flyoutMenuEnabled == value) return;
            flyoutMenuEnabled = value;
            if (!value)
            {
                cachedContextFlyout = canvasText.ContextFlyout;
                canvasText.ContextFlyout = null;
            }
            else if (cachedContextFlyout != null)
            {
                canvasText.ContextFlyout = cachedContextFlyout;
                cachedContextFlyout = null;
            }
        }
    }

    private void FormatChanged()
    {
        bool atBottom = IsAtBottom;
        TextFormat?.Dispose();
        TextFormat = null;
        WrapTextFormat?.Dispose();
        WrapTextFormat = null;
        LineNumberTextFormat?.Dispose();
        LineNumberTextFormat = null;
        LongestLineWidth = -1;
        KeepPositionAfterLayoutChange(atBottom);
    }

    // Line heights or widths changed: the top line stays at the top, at the bottom it stays at the bottom
    private void KeepPositionAfterLayoutChange(bool atBottom)
    {
        ClearLayoutCache();
        TopOffset = 0;
        if (atBottom) RunScroll(ScrollToBottomAction);
        Invalidate();
    }

    #endregion

    #region Text formats and layouts

    private CanvasTextFormat? TextFormat;       // without wrap
    private CanvasTextFormat? WrapTextFormat;   // with wrap
    private CanvasTextFormat? LineNumberTextFormat;
    private CanvasSolidColorBrush? TextBrush;
    private CanvasSolidColorBrush? LineNumberBrush;

    private const float NoWrapLayoutWidth = 1_000_000;

    private float LineHeight => (float)Math.Max(1, fontSize) + lineSpacing;

    private void EnsureFormats()
    {
        if (TextFormat != null) return;

        float size = (float)Math.Max(1, fontSize);
        TextFormat = CreateTextFormat(size, CanvasHorizontalAlignment.Left, CanvasWordWrapping.NoWrap);
        WrapTextFormat = CreateTextFormat(size, CanvasHorizontalAlignment.Left, CanvasWordWrapping.Wrap);
        LineNumberTextFormat = CreateTextFormat(size, CanvasHorizontalAlignment.Right, CanvasWordWrapping.NoWrap);
    }

    private CanvasTextFormat CreateTextFormat(float size, CanvasHorizontalAlignment alignment, CanvasWordWrapping wrapping)
    {
        float height = Math.Max(1, size + lineSpacing);
        var format = new CanvasTextFormat
        {
            FontSize = size,
            FontFamily = fontFamily.Source,
            HorizontalAlignment = alignment,
            VerticalAlignment = CanvasVerticalAlignment.Top,
            WordWrapping = wrapping,
            LineSpacing = height,
            LineSpacingBaseline = GetBaseline(size, height),
            IncrementalTabStop = (float)Math.Round(size * 3f),
            TrimmingGranularity = CanvasTextTrimmingGranularity.None,
            TrimmingSign = CanvasTrimmingSign.None
        };
        return format;
    }

    // Baseline that centers ascent and descent of the font in the line height, the same for every line.
    // (CanvasTextLayout.LineMetrics can not be used: its array can not be marshalled with AOT.)
    private float GetBaseline(float size, float height)
    {
        var device = CanvasDevice.GetSharedDevice();

        using var naturalFormat = new CanvasTextFormat { FontSize = size, FontFamily = fontFamily.Source };
        using var naturalLayout = new CanvasTextLayout(device, "M", naturalFormat, 1000, 1000);
        float natural = (float)naturalLayout.LayoutBounds.Height;

        using var ascentFormat = new CanvasTextFormat { FontSize = size, FontFamily = fontFamily.Source, LineSpacing = height, LineSpacingBaseline = 0 };
        using var ascentLayout = new CanvasTextLayout(device, "M", ascentFormat, 1000, 1000);
        float ascent = -ascentLayout.GetCaretPosition(0, false).Y;

        return ascent + (height - natural) / 2;
    }

    // Layouts of lines, keyed by the line string (same string, same layout). Kept while they are drawn.
    private readonly Dictionary<string, (CanvasTextLayout Layout, int DrawCount)> LayoutCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> RowCache = new(ReferenceEqualityComparer.Instance);
    private int DrawCount; // number of the current frame, layouts used in it are kept

    private bool CanMeasure => canvasText.ReadyToDraw && canvasText.ActualWidth > 0 && canvasText.ActualHeight > 0;

    private CanvasTextLayout GetLayout(string text)
    {
        if (LayoutCache.TryGetValue(text, out var entry))
        {
            if (entry.DrawCount != DrawCount) LayoutCache[text] = (entry.Layout, DrawCount);
            return entry.Layout;
        }

        EnsureFormats();
        float width = textWrapEnabled ? (float)Math.Max(1, canvasText.ActualWidth) : NoWrapLayoutWidth;
        var layout = new CanvasTextLayout(canvasText, text, textWrapEnabled ? WrapTextFormat : TextFormat, width, LineHeight);
        layout.Options = CanvasDrawTextOptions.EnableColorFont;
        ApplySyntaxHighlighting(text, layout);

        LayoutCache[text] = (layout, DrawCount);
        return layout;
    }

    private void ApplySyntaxHighlighting(string text, CanvasTextLayout layout)
    {
        if (!highlightsEnabled) return;
        foreach (var rule in highlightLanguage.Highlights)
        {
            Color color = (colorTheme == ElementTheme.Light) ? rule.ColorLight : rule.ColorDark;
            foreach (var match in rule.regex.EnumerateMatches(text))
            {
                if (match.Length <= 0) continue;
                layout.SetColor(match.Index, match.Length, color);
                if (rule.Bold) layout.SetFontWeight(match.Index, match.Length, new FontWeight(600));
                if (rule.Italic) layout.SetFontStyle(match.Index, match.Length, FontStyle.Italic);
                if (rule.Underline) layout.SetUnderline(match.Index, match.Length, true);
            }
        }
    }

    // Number of rows of a displayed line (1 without wrap)
    private int GetRows(int line)
    {
        if (!textWrapEnabled) return 1;
        string text = Model.GetDisplayed(line);
        if (RowCache.TryGetValue(text, out int rows)) return rows;

        rows = Math.Max(1, GetLayout(text).LineCount);
        if (RowCache.Count > 200_000) RowCache.Clear();
        RowCache[text] = rows;
        return rows;
    }

    private double GetLineHeight(int line) => GetRows(line) * (double)LineHeight;

    private void ClearLayoutCache()
    {
        VisibleLines.Clear(); // refers to the layouts, filled again with the next draw
        foreach (var entry in LayoutCache.Values) entry.Layout.Dispose();
        LayoutCache.Clear();
        RowCache.Clear();
        LongestLineWidth = -1;
    }

    // Removes layouts that were not drawn in the last frame
    private void TrimLayoutCache()
    {
        if (LayoutCache.Count <= 512) return;
        var remove = new List<string>();
        foreach (var pair in LayoutCache)
            if (pair.Value.DrawCount != DrawCount) remove.Add(pair.Key);
        foreach (var key in remove)
        {
            LayoutCache[key].Layout.Dispose();
            LayoutCache.Remove(key);
        }
    }

    private void ClearColorResources()
    {
        TextBrush?.Dispose();
        TextBrush = null;
        LineNumberBrush?.Dispose();
        LineNumberBrush = null;
    }

    private void ReleaseResources()
    {
        CursorBlinkTimer.Stop();
        ClearLayoutCache();
        ClearColorResources();
        TextFormat?.Dispose();
        TextFormat = null;
        WrapTextFormat?.Dispose();
        WrapTextFormat = null;
        LineNumberTextFormat?.Dispose();
        LineNumberTextFormat = null;
    }

    private void Invalidate()
    {
        canvasText.Invalidate();
        canvasLineNumbers.Invalidate();
    }

    #endregion

    #region Drawing

    private readonly struct VisibleLine(int index, float y, float height, CanvasTextLayout layout)
    {
        public readonly int Index = index;
        public readonly float Y = y;
        public readonly float Height = height;
        public readonly CanvasTextLayout Layout = layout;
    }

    private readonly List<VisibleLine> VisibleLines = new();
    private bool LastFrameAtBottom = true;

    private void Canvas_Text_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        DrawCount++;
        EnsureFormats();
        TextBrush ??= new CanvasSolidColorBrush(sender, Design.TextColor);

        var pending = PendingScroll;
        PendingScroll = null;
        pending?.Invoke();
        ClampTop();

        var ds = args.DrawingSession;
        VisibleLines.Clear();

        int count = Model.DisplayCount;
        double viewport = sender.ActualHeight;
        float x = textWrapEnabled ? 0 : -(float)HorizontalScrollbar.Value;
        double y = -TopOffset;
        int line = TopLine;

        for (; line < count && y < viewport; line++)
        {
            string text = Model.GetDisplayed(line);
            var layout = GetLayout(text);
            float height = (float)GetLineHeight(line);
            float top = (float)y;

            DrawSearchHighlights(ds, layout, text, line, x, top);
            DrawSelection(ds, layout, text, line, x, top);
            ds.DrawTextLayout(layout, x, top, TextBrush);

            VisibleLines.Add(new VisibleLine(line, top, height, layout));
            y += height;
        }

        LastFrameAtBottom = count == 0 || (line >= count && y <= viewport + 0.5);

        DrawCursor(ds, x);
        UpdateScrollbars(viewport);
        TrimLayoutCache();
        canvasLineNumbers.Invalidate();
    }

    private void DrawCursor(CanvasDrawingSession ds, float x)
    {
        if (!HasCursor || !CursorVisible) return;
        foreach (var visible in VisibleLines)
        {
            if (visible.Index != CursorPosition.Line) continue;
            var (caretX, rowY) = GetCaret(visible.Layout, Model.GetDisplayed(visible.Index), CursorPosition.Character);
            ds.DrawLine(x + caretX, visible.Y + rowY, x + caretX, visible.Y + rowY + LineHeight, Design.CursorColor, 1.5f);
            return;
        }
    }

    // Caret x and the top of its row inside the line
    private static (float X, float RowY) GetCaret(CanvasTextLayout layout, string text, int character)
    {
        character = Math.Clamp(character, 0, text.Length);
        Vector2 first = layout.GetCaretPosition(0, false);
        Vector2 caret = layout.GetCaretPosition(character, false);
        return (caret.X, caret.Y - first.Y);
    }

    // Fills the characters [from, to) of a line, one rectangle per row
    private void FillRange(CanvasDrawingSession ds, CanvasTextLayout layout, string text, int from, int to, float x, float y, Color color, bool lineEnd = false)
    {
        from = Math.Clamp(from, 0, text.Length);
        to = Math.Clamp(to, 0, text.Length);
        float h = LineHeight;
        float endWidth = lineEnd ? h / 3 : 0; // shows a selected line end

        if (to <= from)
        {
            if (endWidth > 0)
            {
                var (cx, cy) = GetCaret(layout, text, to);
                ds.FillRectangle(x + cx, y + cy, endWidth, h, color);
            }
            return;
        }

        var (startX, startRow) = GetCaret(layout, text, from);
        var (endX, endRow) = GetCaret(layout, text, to);
        if (Math.Abs(endRow - startRow) < 0.5f)
        {
            ds.FillRectangle(x + startX, y + startRow, Math.Max(1, endX - startX) + endWidth, h, color);
            return;
        }

        // Over several wrapped rows: find the row changes
        Vector2 first = layout.GetCaretPosition(0, false);
        Vector2 previous = layout.GetCaretPosition(from, false);
        float segmentX = previous.X;
        for (int i = from + 1; i <= to; i++)
        {
            Vector2 current = layout.GetCaretPosition(i, false);
            if (Math.Abs(current.Y - previous.Y) > 0.5f)
            {
                ds.FillRectangle(x + segmentX, y + previous.Y - first.Y, Math.Max(1, previous.X - segmentX), h, color);
                segmentX = current.X;
            }
            previous = current;
        }
        ds.FillRectangle(x + segmentX, y + previous.Y - first.Y, Math.Max(1, previous.X - segmentX) + endWidth, h, color);
    }

    private void DrawSelection(CanvasDrawingSession ds, CanvasTextLayout layout, string text, int line, float x, float y)
    {
        if (!HasSelection || IsSelectionCurrentMatch()) return;
        var (start, end) = GetNormalizedSelection();
        if (line < start.Line || line > end.Line) return;

        int from = (line == start.Line) ? start.Character : 0;
        int to = (line == end.Line) ? end.Character : text.Length;
        FillRange(ds, layout, text, from, to, x, y, Design.SelectionColor, lineEnd: line < end.Line);
    }

    private void DrawSearchHighlights(CanvasDrawingSession ds, CanvasTextLayout layout, string text, int line, float x, float y)
    {
        if (ActiveSearch == null) return;
        for (int index = ActiveSearch.FindInLine(text, 0); index >= 0; index = ActiveSearch.FindInLine(text, index + ActiveSearch.Length))
        {
            bool current = line == SearchLine && index == SearchIndex;
            FillRange(ds, layout, text, index, index + ActiveSearch.Length, x, y, current ? Design.SearchCurrentHighlightColor : Design.SearchHighlightColor);
        }
    }

    private void Canvas_LineNumber_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        EnsureFormats();
        LineNumberBrush ??= new CanvasSolidColorBrush(sender, Design.LineNumberColor);

        // Width for the number of digits of the line count, so it does not change with every line
        string widest = new string('8', Math.Max(1, Model.DisplayCount).ToString().Length);
        using (var measure = new CanvasTextLayout(sender, widest, TextFormat, NoWrapLayoutWidth, LineHeight))
        {
            double width = Math.Ceiling(measure.LayoutBounds.Width) + lineNumberSpace + lineNumberGap;
            if (Math.Abs(sender.Width - width) > 0.5) sender.Width = width;
        }

        float right = (float)Math.Max(1, sender.Width - lineNumberGap - lineNumberSpace);
        foreach (var visible in VisibleLines)
        {
            using var layout = new CanvasTextLayout(sender, (visible.Index + 1).ToString(), LineNumberTextFormat, right, LineHeight);
            args.DrawingSession.DrawTextLayout(layout, lineNumberSpace, visible.Y, LineNumberBrush);
        }
    }

    #endregion

    #region Scroll position

    private int TopLine;
    private double TopOffset;

    // A scroll request that needs line heights: run now if possible, else at the start of the next draw
    private Action? PendingScroll;

    private void RunScroll(Action scroll)
    {
        if (CanMeasure)
        {
            PendingScroll = null;
            scroll();
        }
        else PendingScroll = scroll;
        Invalidate();
    }

    private bool IsAtBottom => (PendingScroll == ScrollToBottomAction) || (LastFrameAtBottom && !(TopLine == 0 && TopOffset <= 0) && Model.DisplayCount > 0);

    private Action? scrollToBottomAction;
    private Action ScrollToBottomAction => scrollToBottomAction ??= ScrollToBottom;

    // The position where the last line ends at the bottom edge (top line and offset)
    private (int Line, double Offset) GetBottomPosition()
    {
        int count = Model.DisplayCount;
        double viewport = canvasText.ActualHeight;
        if (count == 0) return (0, 0);

        if (!textWrapEnabled)
        {
            double h = LineHeight;
            double max = Math.Max(0, count * h - viewport);
            int line = (int)Math.Min(count - 1, Math.Floor(max / h));
            return (line, max - line * h);
        }

        double sum = 0;
        for (int line = count - 1; line >= 0; line--)
        {
            sum += GetLineHeight(line);
            if (sum >= viewport) return (line, sum - viewport);
        }
        return (0, 0);
    }

    // Keeps the position inside the content, the end can not be scrolled above the bottom edge
    private void ClampTop()
    {
        int count = Model.DisplayCount;
        if (count == 0)
        {
            TopLine = 0;
            TopOffset = 0;
            return;
        }

        TopLine = Math.Clamp(TopLine, 0, count - 1);
        TopOffset = Math.Clamp(TopOffset, 0, Math.Max(0, GetLineHeight(TopLine) - 0.001));

        var (bottomLine, bottomOffset) = GetBottomPosition();
        if (TopLine > bottomLine || (TopLine == bottomLine && TopOffset > bottomOffset))
        {
            TopLine = bottomLine;
            TopOffset = bottomOffset;
        }
    }

    private void ScrollToBottom()
    {
        (TopLine, TopOffset) = GetBottomPosition();
    }

    // Moves the view by 'pixels' (positive: down)
    private void ScrollByPixels(double pixels)
    {
        if (Model.DisplayCount == 0) return;
        TopOffset += pixels;
        while (TopOffset < 0 && TopLine > 0)
        {
            TopLine--;
            TopOffset += GetLineHeight(TopLine);
        }
        if (TopOffset < 0) TopOffset = 0;
        while (TopLine < Model.DisplayCount - 1 && TopOffset >= GetLineHeight(TopLine))
        {
            TopOffset -= GetLineHeight(TopLine);
            TopLine++;
        }
        ClampTop();
    }

    // Distance of the top of a displayed line from the top edge, null if it is far below the view
    private double? GetLineScreenY(int line)
    {
        if (line < TopLine)
        {
            double up = -TopOffset;
            for (int i = TopLine - 1; i >= line; i--)
            {
                up -= GetLineHeight(i);
                if (up < -canvasText.ActualHeight * 2) return up;
            }
            return up;
        }

        double y = -TopOffset;
        for (int i = TopLine; i < line; i++)
        {
            y += GetLineHeight(i);
            if (y > canvasText.ActualHeight * 2) return null;
        }
        return y;
    }

    // Puts the point 'rowY' inside a displayed line at 'screenY' from the top edge (as far as the content allows)
    private void PlaceLine(int line, double rowY, double screenY)
    {
        double need = screenY - rowY;
        int top = line;
        while (need > 0 && top > 0)
        {
            top--;
            need -= GetLineHeight(top);
        }

        if (need > 0)
        {
            TopLine = 0;
            TopOffset = 0;
        }
        else
        {
            TopLine = top;
            TopOffset = -need; // smaller than the height of 'top': need was > 0 before its height was subtracted
        }
        ClampTop();
    }

    // Vertical: so little as possible to show the row (rowY) of a line, centered or at an edge
    private enum Placement { Minimal, Top, Bottom, Center }

    private void ShowRow(int line, double rowY, Placement placement)
    {
        double viewport = canvasText.ActualHeight;
        double h = LineHeight;

        switch (placement)
        {
            case Placement.Top: PlaceLine(line, rowY, 0); return;
            case Placement.Bottom: PlaceLine(line, rowY, viewport - h); return;
        }

        double? screen = GetLineScreenY(line);
        double y = (screen ?? double.MaxValue) + rowY;
        bool visible = y >= 0 && y + h <= viewport;
        if (visible) return;

        if (placement == Placement.Center) PlaceLine(line, rowY, (viewport - h) / 2);
        else if (y < 0) PlaceLine(line, rowY, 0);
        else PlaceLine(line, rowY, viewport - h);
    }

    // Without wrap: so little as possible horizontally to show [x1, x2]
    private void ShowColumns(double x1, double x2, bool center)
    {
        if (textWrapEnabled) return;
        double width = canvasText.ActualWidth;
        double margin = LineHeight;
        double current = HorizontalScrollbar.Value;

        double target = current;
        if (x1 < current + margin || x2 > current + width - margin)
            target = center ? x1 - width / 3 : (x1 < current + margin ? x1 - margin : x2 - width + margin);
        if (target == current) return;

        HorizontalScrollbar.Maximum = Math.Max(HorizontalScrollbar.Maximum, x2 + margin - width);
        HorizontalScrollbar.Value = Math.Clamp(target, 0, HorizontalScrollbar.Maximum);
    }

    private void UpdateScrollbars(double viewport)
    {
        int count = Model.DisplayCount;
        if (count == 0)
        {
            VerticalScrollbar.Maximum = 0;
            VerticalScrollbar.Value = 0;
            HorizontalScrollbar.Maximum = 0;
            HorizontalScrollbar.Value = 0;
            return;
        }

        var (bottomLine, bottomOffset) = GetBottomPosition();
        if (!textWrapEnabled)
        {
            double h = LineHeight;
            VerticalScrollbar.ViewportSize = viewport;
            VerticalScrollbar.SmallChange = h;
            VerticalScrollbar.LargeChange = viewport;
            VerticalScrollbar.Maximum = bottomLine * h + bottomOffset;
            VerticalScrollbar.Value = Math.Min(VerticalScrollbar.Maximum, TopLine * h + TopOffset);

            double width = canvasText.ActualWidth;
            HorizontalScrollbar.ViewportSize = width;
            HorizontalScrollbar.SmallChange = h;
            HorizontalScrollbar.LargeChange = width;
            HorizontalScrollbar.Maximum = Math.Max(0, GetLongestLineWidth() + h - width);
            HorizontalScrollbar.Value = Math.Min(HorizontalScrollbar.Value, HorizontalScrollbar.Maximum);
        }
        else
        {
            // With wrap the scrollbar counts lines (a fraction is the part of the line scrolled out)
            int visibleLines = Math.Max(1, VisibleLines.Count);
            VerticalScrollbar.ViewportSize = visibleLines;
            VerticalScrollbar.SmallChange = 1;
            VerticalScrollbar.LargeChange = visibleLines;
            VerticalScrollbar.Maximum = bottomLine + bottomOffset / GetLineHeight(bottomLine);
            VerticalScrollbar.Value = Math.Min(VerticalScrollbar.Maximum, TopLine + TopOffset / GetLineHeight(TopLine));

            HorizontalScrollbar.Maximum = 0;
            HorizontalScrollbar.Value = 0;
        }
    }

    private void VerticalScrollbar_Scroll(object sender, ScrollEventArgs e)
    {
        double value = e.NewValue;
        RunScroll(() =>
        {
            if (Model.DisplayCount == 0) return;
            if (!textWrapEnabled)
            {
                double h = LineHeight;
                TopLine = (int)Math.Floor(value / h);
                TopOffset = value - TopLine * h;
            }
            else
            {
                TopLine = Math.Clamp((int)Math.Floor(value), 0, Model.DisplayCount - 1);
                TopOffset = (value - TopLine) * GetLineHeight(TopLine);
            }
            ClampTop();
        });
    }

    private void HorizontalScrollbar_Scroll(object sender, ScrollEventArgs e) => canvasText.Invalidate();

    private void canvasText_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(canvasText).Properties;
        int delta = properties.MouseWheelDelta;

        if (properties.IsHorizontalMouseWheel || IsKeyDown(VirtualKey.Shift))
        {
            if (!textWrapEnabled)
            {
                int direction = properties.IsHorizontalMouseWheel ? 1 : -1;
                HorizontalScrollbar.Value = Math.Clamp(HorizontalScrollbar.Value + direction * delta, 0, HorizontalScrollbar.Maximum);
                canvasText.Invalidate();
            }
        }
        else RunScroll(() => ScrollByPixels(-delta));

        e.Handled = true;
    }

    private void CanvasText_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (textWrapEnabled && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
        {
            // Rows of wrapped lines change with the width
            bool atBottom = IsAtBottom;
            KeepPositionAfterLayoutChange(atBottom);
        }
        else if (LastFrameAtBottom && !(TopLine == 0 && TopOffset <= 0)) RunScroll(ScrollToBottomAction);
        Invalidate();
    }

    public void ScrollTopIntoView()
    {
        PendingScroll = null;
        TopLine = 0;
        TopOffset = 0;
        Invalidate();
    }

    public void ScrollBottomIntoView() => RunScroll(ScrollToBottomAction);

    public void ScrollPageUp() => RunScroll(() => ScrollByPixels(-canvasText.ActualHeight));

    public void ScrollPageDown() => RunScroll(() => ScrollByPixels(canvasText.ActualHeight));

    public void ScrollCursorIntoView() => ScrollCursor(Placement.Minimal);

    public void ScrollCursorToTop() => ScrollCursor(Placement.Top);

    public void ScrollCursorToBottom() => ScrollCursor(Placement.Bottom);

    private void ScrollCursor(Placement placement)
    {
        if (!HasCursor || Model.DisplayCount == 0) return;
        RunScroll(() =>
        {
            if (!HasCursor || Model.DisplayCount == 0) return;
            int line = Math.Clamp(CursorPosition.Line, 0, Model.DisplayCount - 1);
            string text = Model.GetDisplayed(line);
            var (caretX, rowY) = GetCaret(GetLayout(text), text, CursorPosition.Character);
            ShowRow(line, rowY, placement);
            ShowColumns(caretX, caretX, center: false);
        });
    }

    // Longest displayed line (by columns, a tab counts 6) for the horizontal scrollbar, its width is measured once
    private int LongestLine = -1;
    private int LongestColumns;
    private double LongestLineWidth = -1;

    private static int GetColumns(string text) => LineModel.GetColumns(text);

    private void UpdateLongestLine()
    {
        LongestLine = -1;
        LongestColumns = 0;
        LongestLineWidth = -1;

        int count = Model.DisplayCount;
        const int blockSize = 65536;
        if (count < 2 * blockSize)
        {
            UpdateLongestLine(0, count);
            return;
        }

        // Large contents on all cores, the first longest line wins like in the sequential scan
        object gate = new();
        Parallel.For(0, (count + blockSize - 1) / blockSize, block =>
        {
            int end = Math.Min(count, (block + 1) * blockSize), bestColumns = 0, bestLine = -1;
            for (int i = block * blockSize; i < end; i++)
            {
                int columns = GetColumns(Model.GetDisplayed(i));
                if (columns > bestColumns)
                {
                    bestColumns = columns;
                    bestLine = i;
                }
            }
            if (bestLine < 0) return;
            lock (gate)
            {
                if (bestColumns > LongestColumns || (bestColumns == LongestColumns && bestLine < LongestLine))
                {
                    LongestColumns = bestColumns;
                    LongestLine = bestLine;
                }
            }
        });
    }

    private void UpdateLongestLine(int from, int to)
    {
        for (int i = from; i < to; i++)
        {
            int columns = GetColumns(Model.GetDisplayed(i));
            if (columns <= LongestColumns) continue;
            LongestColumns = columns;
            LongestLine = i;
            LongestLineWidth = -1;
        }
    }

    private double GetLongestLineWidth()
    {
        if (LongestLine < 0 || LongestLine >= Model.DisplayCount) return 0;
        if (LongestLineWidth < 0)
        {
            EnsureFormats();
            using var layout = new CanvasTextLayout(canvasText, Model.GetDisplayed(LongestLine), TextFormat, NoWrapLayoutWidth, LineHeight);
            LongestLineWidth = layout.LayoutBounds.Width;
        }
        return LongestLineWidth;
    }

    #endregion

    #region Cursor and selection

    private readonly struct TextPosition(int line, int character)
    {
        public readonly int Line = line;
        public readonly int Character = character;
        public bool Equals(TextPosition other) => Line == other.Line && Character == other.Character;
    }

    private TextPosition CursorPosition;
    private TextPosition? SelectionAnchor;
    private TextPosition? SelectionActive;
    private bool IsSelecting;
    private bool CursorVisible;
    private int ClickedLine = -1;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer CursorBlinkTimer;

    public bool HasCursor { get; private set; }

    public bool HasSelection => SelectionAnchor.HasValue && SelectionActive.HasValue && !SelectionAnchor.Value.Equals(SelectionActive.Value);

    public int ClickedLineIndex => (ClickedLine >= 0 && ClickedLine < Model.DisplayCount) ? ClickedLine : (HasCursor ? CursorPosition.Line : -1);

    public void SetCursor(int line, int character)
    {
        if (Model.DisplayCount == 0) return;
        SetCursorPosition(ClampPosition(line, character));
        ScrollCursorIntoView();
        Invalidate();
    }

    public void ClearCursor()
    {
        HasCursor = false;
        CursorVisible = false;
        CursorBlinkTimer.Stop();
        canvasText.Invalidate();
    }

    public void SelectAll()
    {
        int count = Model.DisplayCount;
        if (count == 0) return;
        SelectionAnchor = new TextPosition(0, 0);
        SelectionActive = new TextPosition(count - 1, Model.GetDisplayed(count - 1).Length);
        SetCursorPosition(SelectionActive.Value);
        Invalidate();
    }

    public void SelectLine(int LineIndex)
    {
        if (LineIndex < 0 || LineIndex >= Model.DisplayCount) return;
        SelectionAnchor = new TextPosition(LineIndex, 0);
        SelectionActive = new TextPosition(LineIndex, Model.GetDisplayed(LineIndex).Length);
        SetCursorPosition(SelectionActive.Value);
        ScrollCursorIntoView();
        Invalidate();
    }

    public void ClearSelection()
    {
        SelectionAnchor = null;
        SelectionActive = null;
        IsSelecting = false;
        canvasText.Invalidate();
    }

    public void CopySelection()
    {
        if (!HasSelection) return;
        var (start, end) = GetNormalizedSelection();
        var sb = new StringBuilder();
        for (int line = start.Line; line <= end.Line; line++)
        {
            string text = Model.GetDisplayed(line);
            int from = (line == start.Line) ? Math.Clamp(start.Character, 0, text.Length) : 0;
            int to = (line == end.Line) ? Math.Clamp(end.Character, 0, text.Length) : text.Length;
            if (to > from) sb.Append(text, from, to - from);
            if (line < end.Line) sb.Append("\r\n");
        }
        SetClipboard(sb.ToString());
    }

    public void CopyAll()
    {
        int count = Model.DisplayCount;
        if (count == 0) return;
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++) sb.Append(Model.GetDisplayed(i)).Append("\r\n");
        SetClipboard(sb.ToString());
    }

    private static void SetClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    public void SetFocus() => canvasText.Focus(FocusState.Programmatic);

    private TextPosition ClampPosition(int line, int character)
    {
        line = Math.Clamp(line, 0, Math.Max(0, Model.DisplayCount - 1));
        int length = Model.DisplayCount > 0 ? Model.GetDisplayed(line).Length : 0;
        return new TextPosition(line, Math.Clamp(character, 0, length));
    }

    private void SetCursorPosition(TextPosition position)
    {
        CursorPosition = position;
        HasCursor = true;
        CursorVisible = true;
        CursorBlinkTimer.Stop();
        CursorBlinkTimer.Start();
        canvasText.Invalidate();
    }

    private (TextPosition Start, TextPosition End) GetNormalizedSelection()
    {
        var a = SelectionAnchor!.Value;
        var b = SelectionActive!.Value;
        bool ordered = a.Line < b.Line || (a.Line == b.Line && a.Character <= b.Character);
        return ordered ? (a, b) : (b, a);
    }

    // Moves the cursor, with 'extend' the selection grows from where the cursor was
    private void MoveCursorTo(TextPosition position, bool extend)
    {
        if (extend)
        {
            if (!SelectionAnchor.HasValue || !SelectionActive.HasValue) SelectionAnchor = HasCursor ? CursorPosition : position;
            SelectionActive = position;
        }
        else ClearSelection();

        SetCursorPosition(position);
        ScrollCursorIntoView();
    }

    #endregion

    #region Mouse and keyboard

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    // Displayed position under a point of the text canvas (from the last drawn frame)
    private TextPosition? HitTestPosition(Point point)
    {
        if (VisibleLines.Count == 0) return null;

        VisibleLine target = VisibleLines[^1];
        foreach (var visible in VisibleLines)
        {
            if (point.Y < visible.Y + visible.Height)
            {
                target = visible;
                break;
            }
        }

        string text = Model.GetDisplayed(target.Index);
        float x = (float)point.X + (textWrapEnabled ? 0 : (float)HorizontalScrollbar.Value);
        float y = Math.Clamp((float)point.Y - target.Y, 0, target.Height - 1);
        target.Layout.HitTest(x, y, out CanvasTextLayoutRegion region, out bool trailing);

        int character = region.CharacterIndex + (trailing ? 1 : 0);
        return new TextPosition(target.Index, Math.Clamp(character, 0, text.Length));
    }

    private void CanvasText_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(canvasText);
        if (point.Properties.IsRightButtonPressed)
        {
            // Remembered for ClickedLineIndex, not handled so the context menu still opens
            ClickedLine = HitTestPosition(point.Position)?.Line ?? -1;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        canvasText.Focus(FocusState.Pointer);
        ClickedLine = -1;

        var position = HitTestPosition(point.Position);
        if (!position.HasValue) return;

        bool extend = IsKeyDown(VirtualKey.Shift) && HasCursor;
        if (extend)
        {
            if (!SelectionAnchor.HasValue) SelectionAnchor = CursorPosition;
        }
        else SelectionAnchor = position.Value;
        SelectionActive = position.Value;

        SetCursorPosition(position.Value);
        IsSelecting = true;
        canvasText.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void CanvasText_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsSelecting) return;
        var position = HitTestPosition(e.GetCurrentPoint(canvasText).Position);
        if (!position.HasValue) return;

        SelectionActive = position.Value;
        SetCursorPosition(position.Value);
        ScrollCursorIntoView(); // dragging over the edge scrolls
        e.Handled = true;
    }

    private void CanvasText_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!IsSelecting) return;
        IsSelecting = false;
        canvasText.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    // All shortcuts are handled here once, the context menu only shows them
    private void CanvasText_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = IsKeyDown(VirtualKey.Control);
        bool shift = IsKeyDown(VirtualKey.Shift);
        e.Handled = true;

        if (control)
        {
            switch (e.Key)
            {
                case VirtualKey.A: SelectAll(); return;
                case VirtualKey.C: CopySelection(); return;
                case VirtualKey.X: CopyAll(); return;
                case VirtualKey.L: SelectLine(ClickedLineIndex); return;
                case VirtualKey.Home: ScrollTopIntoView(); return;
                case VirtualKey.End: ScrollBottomIntoView(); return;
            }
        }

        switch (e.Key)
        {
            case VirtualKey.Escape:
            case VirtualKey.Delete:
                ClearSelection();
                ClearCursor();
                return;
            case VirtualKey.PageUp: ScrollPageUp(); return;
            case VirtualKey.PageDown: ScrollPageDown(); return;
            case VirtualKey.Home:
            case VirtualKey.End:
            case VirtualKey.Left:
            case VirtualKey.Right:
            case VirtualKey.Up:
            case VirtualKey.Down:
                MoveCursorWithKey(e.Key, shift);
                return;
        }

        e.Handled = false;
    }

    private void MoveCursorWithKey(VirtualKey key, bool extend)
    {
        int count = Model.DisplayCount;
        if (count == 0) return;

        var position = HasCursor ? ClampPosition(CursorPosition.Line, CursorPosition.Character)
            : new TextPosition(Math.Clamp(TopLine, 0, count - 1), 0);
        int line = position.Line, character = position.Character;
        int length = Model.GetDisplayed(line).Length;

        switch (key)
        {
            case VirtualKey.Home: character = 0; break;
            case VirtualKey.End: character = length; break;
            case VirtualKey.Left:
                if (character > 0) character--;
                else if (line > 0) { line--; character = Model.GetDisplayed(line).Length; }
                break;
            case VirtualKey.Right:
                if (character < length) character++;
                else if (line < count - 1) { line++; character = 0; }
                break;
            case VirtualKey.Up: if (line > 0) line--; break;
            case VirtualKey.Down: if (line < count - 1) line++; break;
        }

        MoveCursorTo(ClampPosition(line, character), extend);
    }

    private void ContextMenu_CopyAll_Click(object sender, RoutedEventArgs e) => CopyAll();
    private void ContextMenu_CopySelection_Click(object sender, RoutedEventArgs e) => CopySelection();
    private void ContextMenu_SelectAll_Click(object sender, RoutedEventArgs e) => SelectAll();
    private void ContextMenu_SelectLine_Click(object sender, RoutedEventArgs e) => SelectLine(ClickedLineIndex);
    private void ContextMenu_ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        ClearSelection();
        ClearCursor();
    }

    #endregion

    #region Search

    private TextSearch? ActiveSearch;
    private int SearchLine = -1; // current match, -1 = none
    private int SearchIndex;

    public bool SearchWrapAround { get; set; } = true;

    public bool SearchCursorEnabled { get; set; } = false;

    public SearchStatus SearchStatus { get; private set; }

    public event EventHandler? SearchStatusChanged;

    private void SetSearchStatus(SearchStatus status)
    {
        if (SearchStatus == status) return;
        SearchStatus = status;
        SearchStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool BeginSearch(string word, bool wholeWord, bool matchCase)
    {
        HighlightSearch(word, wholeWord, matchCase);
        if (ActiveSearch == null) return false;

        StartSearchIfNeeded();
        if (!ActiveSearch.FindForward(Model.GetDisplayed, Model.DisplayCount, 0, 0, out int line, out int index)) return false;

        JumpToMatch(line, index, 1);
        return true;
    }

    public void HighlightSearch(string word, bool wholeWord, bool matchCase)
    {
        EndSearch();
        if (string.IsNullOrEmpty(word)) return;
        ActiveSearch = new TextSearch(word, wholeWord, matchCase);
        canvasText.Invalidate();
    }

    public void EndSearch()
    {
        if (IsSelectionCurrentMatch()) ClearSelection();
        ActiveSearch = null;
        SearchLine = -1;
        SetSearchStatus(default);
        canvasText.Invalidate();
    }

    public bool FindNext()
    {
        if (ActiveSearch == null) return false;
        StartSearchIfNeeded();
        if (SearchLine >= Model.DisplayCount) SearchLine = -1;

        int fromLine = SearchLine < 0 ? 0 : SearchLine;
        int fromIndex = SearchLine < 0 ? 0 : SearchIndex + ActiveSearch.Length;
        int number;
        if (ActiveSearch.FindForward(Model.GetDisplayed, Model.DisplayCount, fromLine, fromIndex, out int line, out int index))
            number = (SearchLine < 0) ? 1 : SearchStatus.CurrentMatch + 1;
        else if (SearchWrapAround && ActiveSearch.FindForward(Model.GetDisplayed, Model.DisplayCount, 0, 0, out line, out index))
            number = 1;
        else return false;

        JumpToMatch(line, index, number);
        return HasFurtherMatch(forward: true);
    }

    public bool FindPrevious()
    {
        if (ActiveSearch == null) return false;
        StartSearchIfNeeded();
        if (SearchLine >= Model.DisplayCount) SearchLine = -1;

        int line = -1, index = -1, number;
        if (SearchLine >= 0 && ActiveSearch.FindBackward(Model.GetDisplayed, Model.DisplayCount, SearchLine, SearchIndex, out line, out index))
            number = SearchStatus.CurrentMatch - 1;
        else if ((SearchLine < 0 || SearchWrapAround) && ActiveSearch.FindBackward(Model.GetDisplayed, Model.DisplayCount, Model.DisplayCount - 1, int.MaxValue, out line, out index))
            number = SearchStatus.Matches; // without a current match the search starts at the end
        else return false;

        JumpToMatch(line, index, number);
        return HasFurtherMatch(forward: false);
    }

    // Known from the counted matches and the number of the current one
    private bool HasFurtherMatch(bool forward)
    {
        var status = SearchStatus;
        if (SearchWrapAround) return status.Matches > 1;
        return forward ? status.CurrentMatch < status.Matches : status.CurrentMatch > 1;
    }

    private void StartSearchIfNeeded()
    {
        if (ActiveSearch == null || SearchStatus.SearchStarted) return;
        SetSearchStatus(new SearchStatus(true, ActiveSearch.Count(Model.GetDisplayed, 0, Model.DisplayCount), 0));
    }

    // After lines changed in an unknown way: count again, no current match
    private void RecountSearch()
    {
        SearchLine = -1;
        if (ActiveSearch != null && SearchStatus.SearchStarted)
            SetSearchStatus(new SearchStatus(true, ActiveSearch.Count(Model.GetDisplayed, 0, Model.DisplayCount), 0));
    }

    private void JumpToMatch(int line, int index, int number)
    {
        SearchLine = line;
        SearchIndex = index;
        SetSearchStatus(SearchStatus with { CurrentMatch = Math.Clamp(number, 1, Math.Max(1, SearchStatus.Matches)) });

        int length = ActiveSearch!.Length;
        SelectionAnchor = new TextPosition(line, index);
        SelectionActive = new TextPosition(line, index + length);
        if (SearchCursorEnabled) SetCursorPosition(SelectionActive.Value);
        else ClearCursor();

        RunScroll(() =>
        {
            if (line >= Model.DisplayCount) return;
            string text = Model.GetDisplayed(line);
            var layout = GetLayout(text);
            var (x1, rowY) = GetCaret(layout, text, index);
            var (x2, _) = GetCaret(layout, text, index + length);
            ShowRow(line, rowY, Placement.Center);
            ShowColumns(x1, x2, center: true);
        });
    }

    private bool IsSelectionCurrentMatch()
    {
        if (ActiveSearch == null || SearchLine < 0 || !SelectionAnchor.HasValue || !SelectionActive.HasValue) return false;
        var (start, end) = GetNormalizedSelection();
        return start.Line == SearchLine && end.Line == SearchLine
            && start.Character == SearchIndex && end.Character == SearchIndex + ActiveSearch.Length;
    }

    #endregion
}
