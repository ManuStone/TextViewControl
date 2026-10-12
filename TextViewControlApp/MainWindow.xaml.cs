using System;
using System.IO;
using System.Collections.Generic;
using Windows.UI;
using Windows.Storage;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TextViewControl;

namespace TextViewControlApp;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Generiere Beispieldaten
        var myList = new List<string>();
		myList.Add(">>>>>>>>>>>>>>>>>>>>>>>>> START <<<<<<<<<<<<<<<<<<<<<<<<<");
		myList.InsertRange(1,GenerateLines(20));
		myList.Add(">>>>>>>>>>>>>>>>>>>>>>>>> ENDE <<<<<<<<<<<<<<<<<<<<<<<<<");

        // Callback Events
		MyTextControl.LineCountChanged += LineCountChanged;
		MyTextControl.SearchStatusChanged += SearchStatusChanged;

		// Set Complete Text
		MyTextControl.SetLines(myList);

        // Set Color Theme Check Box
        DarkModeCheckBox.IsChecked = (MyTextControl.ColorTheme == ElementTheme.Dark) ? true : false;

		// Set Color Theme Root
		if (Content is FrameworkElement rootElement)
        {
            rootElement.RequestedTheme = MyTextControl.ColorTheme;
        }

        // Set Highlighting Colors
        MyTextControl.HighlightLanguage = new()
        {
            Name = "LogFile",
            Highlights = new()
            {
                new HighlightRule(@"^\[[^\]]+\]") { ColorLight = Color.FromArgb(255,100,100,100), ColorDark = Color.FromArgb(255,150,150,150) },
                new HighlightRule(@"(?<=^\[)\d{4}-\d{2}-\d{2}(?:[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?)?") { ColorLight = Color.FromArgb(255,9,134,88), ColorDark = Color.FromArgb(255,78,201,176) },
                new HighlightRule(@"(?<=^\[)\d{2}:\d{2}:\d{2}(?:\.\d+)?") { ColorLight = Color.FromArgb(255,9,134,88), ColorDark = Color.FromArgb(255,78,201,176) },
                new HighlightRule(@"\b(ERROR|FATAL|CRITICAL)\b") {ColorLight = Color.FromArgb(255,205,49,49), ColorDark = Color.FromArgb(255,244,71,71), Bold = true },
                new HighlightRule(@"\b(WARN|WARNING)\b") { ColorLight = Color.FromArgb(255,179,102,0), ColorDark = Color.FromArgb(255,206,145,120), Bold = true },
                new HighlightRule(@"\b(INFO|INFORMATION)\b") { ColorLight = Color.FromArgb(255,0,150,200), ColorDark = Color.FromArgb(255,0,208,208), Bold = true },
                new HighlightRule(@"\b(DEBUG)\b") { ColorLight = Color.FromArgb(255,100,150,100), ColorDark = Color.FromArgb(255,150,206,150), Bold = true }
            }
        };
    }

    private async void LoadFile_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var openPicker = new FileOpenPicker();

        // Wichtig für WinUI 3: Den Picker mit dem aktuellen Fenster verknüpfen
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(openPicker, windowHandle);

        // Filter für Dateitypen definieren
        openPicker.ViewMode = PickerViewMode.List;
        openPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        openPicker.FileTypeFilter.Add(".log");
        openPicker.FileTypeFilter.Add(".txt");

        StorageFile file = await openPicker.PickSingleFileAsync();
        if (file != null)
        {
            try
            {
                // Übergibt die Zeilen an das TextControl
                await MyTextControl.LoadFileAsync(file.Path);

                // Scrollt automatisch nach ganz oben
                MyTextControl.ScrollTopIntoView();
            }
            catch (Exception ex)
            {
                // Erstellt den Fehlerdialog
                ContentDialog errorDialog = new ContentDialog
                {
                    Title = "Fehler beim Laden",
                    Content = $"Die Datei konnte nicht geöffnet werden.\n\nDetails: {ex.Message}",
                    CloseButtonText = "OK",
                    // XamlRoot ist in WinUI 3 Desktop-Apps zwingend erforderlich!
                    XamlRoot = this.Content.XamlRoot
                };

                // Zeigt den Dialog modal an
                await errorDialog.ShowAsync();
            }
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        MyTextControl.Clear();
    }

    private void AddLine_Click(object sender, RoutedEventArgs e)
    {
        MyTextControl.AddLine("Additional Line " + Guid.NewGuid().ToString());
        MyTextControl.ScrollBottomIntoView();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (MyTextControl.FontSize >= 40) return;
        MyTextControl.FontSize++;
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        if (MyTextControl.FontSize <= 12) return;
        MyTextControl.FontSize--;
    }

    public static IEnumerable<string> GenerateLines(int count)
    {
        var random = new Random();
        DateTime currentTime = DateTime.Now.AddDays(-30);
        for (int i = 0; i < count; i++)
        {
            currentTime = currentTime.AddMilliseconds(random.Next(100, 3500));
            string timestamp = currentTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int roll = random.Next(100);
            string level = roll < 10 ? "ERROR" : roll < 25 ? "WARN" : roll < 40 ? "DEBUG" : "INFO";
            string message = level switch
            {
                "ERROR" => "Connection timeout while reaching endpoint /api/v1/sync",
                "WARN" => "High memory allocation detected, GC latency spiked",
                "DEBUG" => "Cache invalidated for key 'session_token'",
                _ => "Task processed successfully"
            };
            yield return $"[{timestamp}] [{level}] Line{i}: {message}";
        }
    }

    private async void MaxLines_Click(object sender, RoutedEventArgs e)
    {
        NumberBox inputNumberBox = new NumberBox
        {
            Value = MyTextControl.MaxLines,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 1,
            LargeChange = 10,
            Minimum = 0,
            Maximum = 10000,
            Width = 200
        };

        ContentDialog dialog = new ContentDialog
        {
            Title = "MaxLines ( 0, 1..10000 )",
            Content = inputNumberBox,            
            PrimaryButtonText = "Ok",
            CloseButtonText = "Close",
            XamlRoot = this.Content.XamlRoot
        };

        ContentDialogResult result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary && !double.IsNaN(inputNumberBox.Value))
        {
            MyTextControl.MaxLines = Convert.ToInt32(inputNumberBox.Value);
        }
    }

    private async void LoadLines_Click(object sender, RoutedEventArgs e)
    {
        NumberBox inputNumberBox = new NumberBox
        {
            Value = 1000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 1,
            LargeChange = 10,
            Minimum = 0,
            Maximum = 10000000,
            Width = 200
        };

        ContentDialog dialog = new ContentDialog
        {
            Title = "Load Lines ( 1..10000000 )",
            Content = inputNumberBox,
            PrimaryButtonText = "Ok",
            CloseButtonText = "Close",
            XamlRoot = this.Content.XamlRoot
        };

        ContentDialogResult result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary && !double.IsNaN(inputNumberBox.Value))
        {
            MyTextControl.SetLines(GenerateLines(Convert.ToInt32(inputNumberBox.Value)));
        }
    }

	private void DarkMode_Click(object sender, RoutedEventArgs e)
	{
		ElementTheme Theme = (bool)DarkModeCheckBox.IsChecked! ? ElementTheme.Dark : ElementTheme.Light;

		if (Content is FrameworkElement root)
        {
            root.RequestedTheme = Theme;
        }

		MyTextControl.ColorTheme = Theme;

	}

	private void Search_Click(object sender, RoutedEventArgs e)
	{
		bool enabled = SearchCheckBox.IsChecked == true;
		SearchPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

		if (enabled)
		{
			SearchTextBox.Focus(FocusState.Programmatic);
			UpdateSearchHighlight(); // text may still be in the box from before
		}
		else MyTextControl.EndSearch();
	}

	// Ctrl+F: show the search row (if hidden) and put the focus into the search box with the text selected
	private void SearchAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
	{
		if (SearchCheckBox.IsChecked != true)
		{
			SearchCheckBox.IsChecked = true;
			Search_Click(SearchCheckBox, new RoutedEventArgs());
		}

		SearchTextBox.Focus(FocusState.Keyboard);
		SearchTextBox.SelectAll();
		args.Handled = true;
	}

	private void SearchTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
	{
		if (e.Key != Windows.System.VirtualKey.Enter) return;

		// Enter: same as Next
		SearchNext_Click(sender, e);
		e.Handled = true;
	}

	private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSearchHighlight();

	private void SearchOptions_Click(object sender, RoutedEventArgs e) => UpdateSearchHighlight();

	// Highlights the matches while typing, Next then counts them and jumps to the first one
	private void UpdateSearchHighlight()
	{
		MyTextControl.HighlightSearch(SearchTextBox.Text, WholeWordCheckBox.IsChecked == true, MatchCaseCheckBox.IsChecked == true);
	}

	// Buttons are always enabled, the TextControl does nothing if a click makes no sense
	// Stop ends the search (counting, jumping) but keeps the highlights, like while typing
	private void SearchStop_Click(object sender, RoutedEventArgs e) => UpdateSearchHighlight();

	// Without a running search the current text is taken first, then Next / Previous start the search themselves
	private void SearchPrevious_Click(object sender, RoutedEventArgs e)
	{
		if (!MyTextControl.SearchStatus.SearchStarted) UpdateSearchHighlight();
		MyTextControl.FindPrevious();
	}

	private void SearchNext_Click(object sender, RoutedEventArgs e)
	{
		if (!MyTextControl.SearchStatus.SearchStarted) UpdateSearchHighlight();
		MyTextControl.FindNext();
	}

	private void LineCountChanged(object? sender, EventArgs e)
	{
		LinesText.Text = $"Lines {MyTextControl.LineCount} / {MyTextControl.TotalCount}";
	}

	// Also called when lines are added or the filter changes, so the numbers stay up to date
	private void SearchStatusChanged(object? sender, EventArgs e)
	{
		var status = MyTextControl.SearchStatus;

		string text;
		if (!status.SearchStarted) text = "";
		else if (status.Matches == 0) text = "No Matches";
		else text = $"Matches {(status.CurrentMatch > 0 ? status.CurrentMatch.ToString() : "-")} / {status.Matches}";

		MatchesText.Text = text;
		MatchesBorder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
	}

	private void Monitoring_Click(object sender, RoutedEventArgs e)
	{
        if (MyTextControl.Monitoring)
        {
			MyTextControl.ScrollBottomIntoView();
		}
        else
        {
			MyTextControl.ScrollTopIntoView();
		}
    }
}
