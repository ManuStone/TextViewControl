# TextViewControl

A fast, read-only text viewer control for **WinUI 3**, drawn with **Win2D**, built for very large log files (millions of lines, 1 GB and more).

## Features

- Millions of lines: loading 1 GB / 14 million lines takes a few seconds, scrolling and zooming stay instant
- Word wrap: only the visible lines are measured
- Syntax highlighting with regular expressions (color, bold, italic, underline), separate colors for light and dark theme
- Color filter: show only the lines that match a highlight rule
- Live data: `AddLine`/`AddLines`, `MaxLines` + `Monitoring` (show the newest lines), scrollback buffer (`MaxTotalLines`)
- Search with match count, next/previous, whole word, match case
- Selection with mouse and keyboard, copy, context menu, line numbers
- Native AOT compatible: no trim or AOT warnings, works in apps published with `<PublishAot>true</PublishAot>`

## Requirements

- Windows App SDK 1.8 or later, Win2D 1.4, .NET 8 or later, Windows 10 1809 (17763) or later
- The app must be built for a platform (x64, x86 or ARM64), not AnyCPU, so that the native Win2D library is found

## Usage

XAML:

```xml
<Window
    xmlns:tv="using:TextViewControl">
    <tv:TextControl x:Name="LogView" />
</Window>
```

C#:

```csharp
using System.Collections.Generic;
using TextViewControl;
using Windows.UI;

// Content
var lines = new List<string>
{
    "[12:00:00] [INFO] Application started",
    "[12:00:01] [WARN] Configuration file not found, using defaults",
    "[12:00:02] [INFO] Connected to server"
};
LogView.SetLines(lines);
LogView.AddLine("[12:00:03] [ERROR] Something failed");

// Highlighting
LogView.HighlightLanguage = new SyntaxHighlightLanguage
{
    Name = "Log",
    Highlights =
    {
        new HighlightRule(@"\bERROR\b") { ColorLight = Color.FromArgb(255, 205, 49, 49), ColorDark = Color.FromArgb(255, 244, 71, 71), Bold = true },
        new HighlightRule(@"\bWARN(ING)?\b") { ColorLight = Color.FromArgb(255, 179, 102, 0), ColorDark = Color.FromArgb(255, 206, 145, 120) }
    }
};

// Show only highlighted lines, follow new lines
LogView.FilterHighlights = true;
LogView.ScrollBottomIntoView();

// Search: highlights all matches and jumps to the first one, then FindNext / FindPrevious
bool found = LogView.BeginSearch("timeout", wholeWord: false, matchCase: false);
LogView.FindNext();
```

## Credits

Based on ideas from [TextControlBox.WinUI.JuliusKirsch](https://www.nuget.org/packages/TextControlBox.WinUI.JuliusKirsch).

## License

MIT
