using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using EveryDownload.Models;
using EveryDownload.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace EveryDownload;

public sealed partial class MainWindow : Window
{
    private readonly AppSettings settings = AppSettings.Load();
    private readonly DownloadManager manager;
    private readonly ObservableCollection<DownloadItem> visible = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer timer;
    private readonly TrayIcon? tray;
    private TaskbarProgress? taskbar;
    private string filter = "all";
    private string search = "";
    private string railSignature = "";
    private bool loading = true;
    private bool closeConfirmed;
    private bool offscreen;
    private uint lastClipboard;
    private string lastOffered = "";
    private int tickCount;
    private readonly SemaphoreSlim dialogGate = new(1, 1);
    private readonly Dictionary<string, TextBlock> detailValues = new();

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainWindow(LaunchRequest first, bool background)
    {
        InitializeComponent();
        Title = "EveryDownload";
        manager = new DownloadManager(settings) { IsMetered = SystemActions.IsMetered };
        DownloadList.ItemsSource = visible;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        PlaceWindow();
        if (background)
        {
            offscreen = true; // the window must open once for its content to load; keep that out of sight
            AppWindow.Move(new PointInt32(-32000, -32000));
        }

        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        try
        {
            if (File.Exists(Path.Combine(assets, "AppIcon.ico"))) AppWindow.SetIcon(Path.Combine(assets, "AppIcon.ico"));
            if (File.Exists(Path.Combine(assets, "AppIcon.png"))) TitleIcon.Source = new BitmapImage(new Uri(Path.Combine(assets, "AppIcon.png")));
        }
        catch (Exception ex) { Log.Write("Could not load the icon", ex); }

        BuildDetailPanel();

        // Closing the window leaves EveryDownload in the tray, where it keeps catching downloads from the browser.
        try
        {
            tray = new TrayIcon(Path.Combine(assets, "AppIcon.ico"), () => DispatcherQueue.TryEnqueue(BringToFront), new (string, Action)[]
            {
                ("Open EveryDownload", () => DispatcherQueue.TryEnqueue(BringToFront)),
                ("Add link from clipboard", () => DispatcherQueue.TryEnqueue(() => _ = AddFromClipboardAsync())),
                ("", () => { }),
                ("Pause all", () => DispatcherQueue.TryEnqueue(manager.PauseAll)),
                ("Resume all", () => DispatcherQueue.TryEnqueue(manager.StartAll)),
                ("", () => { }),
                ("Exit", () => DispatcherQueue.TryEnqueue(() => _ = ExitAsync())),
            });
        }
        catch (Exception ex) { Log.Write("The tray icon could not start", ex); }

        manager.ListChanged += () => DispatcherQueue.TryEnqueue(SyncList);
        manager.ItemCompleted += item => DispatcherQueue.TryEnqueue(() =>
        {
            if (settings.Notify) tray?.Notify("Download complete", item.FileName);
        });
        manager.ItemFailed += item => DispatcherQueue.TryEnqueue(() =>
        {
            if (settings.Notify) tray?.Notify("Download failed", $"{item.FileName}: {item.Error}");
        });
        manager.QueueFinished += () => DispatcherQueue.TryEnqueue(() => _ = OnQueueFinishedAsync());

        Root.ActualThemeChanged += (_, _) => ApplyThemeChrome();
        Root.Loaded += (_, _) =>
        {
            ApplyTheme();
            taskbar = new TaskbarProgress(WindowNative.GetWindowHandle(this));
            lastClipboard = SystemActions.ClipboardSequence; // whatever was copied before we started is not an offer
            AddShortcut(VirtualKey.N, () => _ = ShowAddDialogAsync(null));
            AddShortcut(VirtualKey.V, () => _ = AddFromClipboardAsync());
            SyncList();
            Handle(first);
            _ = FirstRunAsync();
        };

        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.Tick += (_, _) => Tick();
        timer.Start();

        AppWindow.Closing += OnClosing;
        loading = false;
    }

    private void AddShortcut(VirtualKey key, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, e) =>
        {
            // Ctrl+V inside a text box means paste, not "add the clipboard as a download".
            if (key == VirtualKey.V && FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox) return;
            e.Handled = true;
            action();
        };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    // ---- launch requests (browser, magnet links, second instance) ----------------------------------------------

    public void Handle(LaunchRequest request)
    {
        if (request.Show) BringToFront();
        if (request.GrabUrl != null)
        {
            BringToFront();
            _ = ShowGrabberAsync(request.GrabUrl);
        }
        foreach (var add in request.Adds)
        {
            if (add.FromBrowser && (add.Silent || !settings.AskForBrowserDownloads))
            {
                var items = manager.Add(add);
                if (items.Count > 0) ShowNotice($"Added {items[0].FileName}", InfoBarSeverity.Success);
                if (settings.Notify) tray?.Notify("Download added", items.FirstOrDefault()?.FileName ?? add.Urls[0]);
            }
            else
            {
                BringToFront();
                _ = ShowAddDialogAsync(add);
            }
        }
    }

    private void BringToFront()
    {
        try
        {
            if (offscreen) { offscreen = false; PlaceWindow(); }
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
            AppWindow.Show();
            Activate();
            _ = FirstRunAsync(); // a hidden start waits until the window is really shown
        }
        catch (Exception ex) { Log.Write("Could not bring the window forward", ex); }
    }

    // ---- window, theme -----------------------------------------------------------------------------------------

    private void PlaceWindow()
    {
        double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        int w = (int)(1220 * scale), h = (int)(760 * scale);
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        w = Math.Min(w, work.Width); h = Math.Min(h, work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = settings.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        ApplyThemeChrome();
    }

    private void ApplyThemeChrome()
    {
        bool light = Root.ActualTheme == ElementTheme.Light;
        Palette.Apply(light);
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = light ? Colors.Black : Colors.White;
        bar.ButtonInactiveForegroundColor = Colors.Gray;
        bar.ButtonHoverBackgroundColor = light ? ColorHelper.FromArgb(0x14, 0, 0, 0) : ColorHelper.FromArgb(0x24, 255, 255, 255);
        bar.ButtonHoverForegroundColor = light ? Colors.Black : Colors.White;
    }

    // ---- the list ----------------------------------------------------------------------------------------------

    private static readonly (string Key, string Title, string Glyph)[] MainFilters =
    {
        ("all", "All downloads", ""),
        ("active", "Downloading", ""),
        ("waiting", "Queued and paused", ""),
        ("done", "Completed", ""),
        ("failed", "Failed", ""),
    };

    private bool InFilter(DownloadItem i, string key) => key switch
    {
        "all" => true,
        "active" => i.IsActive,
        "waiting" => i.Status is DownloadStatus.Queued or DownloadStatus.Paused,
        "done" => i.IsFinished,
        "failed" => i.Status == DownloadStatus.Failed,
        _ when key.StartsWith("cat:") => i.Category == key[4..],
        _ => true,
    };

    private bool Matches(DownloadItem i) =>
        InFilter(i, filter) && (search.Length == 0 || i.FileName.Contains(search, StringComparison.OrdinalIgnoreCase) || i.Url.Contains(search, StringComparison.OrdinalIgnoreCase));

    /// <summary>Brings the visible list in line with the manager without disturbing the selection.</summary>
    private void SyncList()
    {
        var wanted = manager.Snapshot().Where(Matches).OrderByDescending(i => i.AddedAt).ToList();
        for (int i = visible.Count - 1; i >= 0; i--)
            if (!wanted.Contains(visible[i])) visible.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < visible.Count && ReferenceEquals(visible[i], wanted[i])) continue;
            int existing = visible.IndexOf(wanted[i]);
            if (existing >= 0) visible.Move(existing, i);
            else visible.Insert(i, wanted[i]);
        }
        EmptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (visible.Count == 0)
        {
            bool anything = manager.Snapshot().Count > 0;
            EmptyTitle.Text = anything ? "No downloads match" : "Nothing here yet";
            EmptyHint.Text = anything ? "Try another filter or clear the search." : "Drop a link, a magnet or a .torrent file anywhere,\nor press Add link.";
        }
    }

    private void BuildRail()
    {
        var all = manager.Snapshot();
        var entries = MainFilters.Select(f => (f.Key, f.Title, f.Glyph, Count: all.Count(i => InFilter(i, f.Key)))).ToList();
        var cats = Categories.All.Select(c => (Key: "cat:" + c, Title: c, Glyph: Categories.Glyph(c), Count: all.Count(i => i.Category == c))).Where(c => c.Count > 0).ToList();
        string signature = filter + "|" + string.Join(",", entries.Select(e => e.Count)) + "|" + string.Join(",", cats.Select(c => c.Key + c.Count));
        if (signature == railSignature) return;
        railSignature = signature;

        RailPanel.Children.Clear();
        RailPanel.Children.Add(new TextBlock { Text = "DOWNLOADS", Style = (Style)Application.Current.Resources["PanelHeader"], Margin = new Thickness(10, 0, 0, 6) });
        foreach (var e in entries) RailPanel.Children.Add(RailButton(e.Key, e.Title, e.Glyph, e.Count));
        if (cats.Count > 0)
        {
            RailPanel.Children.Add(new TextBlock { Text = "CATEGORIES", Style = (Style)Application.Current.Resources["PanelHeader"], Margin = new Thickness(10, 16, 0, 6) });
            foreach (var c in cats) RailPanel.Children.Add(RailButton(c.Key, c.Title, c.Glyph, c.Count));
        }
    }

    private Button RailButton(string key, string title, string glyph, int count)
    {
        bool on = key == filter;
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = on ? (Brush)Application.Current.Resources["AccentBrush"] : (Brush)Application.Current.Resources["TextMutedBrush"] });
        var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        var badge = new TextBlock { Text = count > 0 ? count.ToString() : "", Style = (Style)Application.Current.Resources["MutedText"], VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["GhostButton"],
            Content = grid,
            Tag = key,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 0, 10, 0),
            MinHeight = 34,
        };
        if (on) button.Background = (Brush)Application.Current.Resources["AccentTintBrush"];
        button.Click += (_, _) =>
        {
            filter = key;
            railSignature = "";
            BuildRail();
            SyncList();
        };
        return button;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        search = SearchBox.Text.Trim();
        SyncList();
    }

    private List<DownloadItem> Selected() => DownloadList.SelectedItems.Cast<DownloadItem>().ToList();

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetails();

    private void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: DownloadItem item }) Primary(item);
    }

    private void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete) { e.Handled = true; Remove_Click(sender, e); }
        else if (e.Key == VirtualKey.Enter && Selected().FirstOrDefault() is { } item) { e.Handled = true; Primary(item); }
    }

    private void RowPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is DownloadItem item) Primary(item);
    }

    private void RowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is DownloadItem item) SystemActions.ShowInFolder(item.IsFinished ? item.FilePath : item.PartPath);
    }

    /// <summary>The row's main action: pause while running, resume when stopped, open when done.</summary>
    private void Primary(DownloadItem item)
    {
        if (item.IsFinished) SystemActions.OpenFile(item.FilePath);
        else if (item.IsActive && item.Status != DownloadStatus.Downloading) return;
        else if (item.CanPause) manager.Pause(item);
        else manager.Start(item);
        item.Refresh();
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Selected();
        if (chosen.Count == 0) manager.StartAll();
        else foreach (var i in chosen.Where(i => i.CanResume)) manager.Start(i);
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Selected();
        if (chosen.Count == 0) manager.PauseAll();
        else foreach (var i in chosen.Where(i => i.CanPause)) manager.Pause(i);
    }

    /// <summary>Toggles the selected downloads between high priority (first in line, may take a running download's slot) and normal.</summary>
    private void Prioritize_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Selected().Where(i => !i.IsFinished).ToList();
        if (chosen.Count == 0) { ShowNotice("Select a download that is not finished yet.", InfoBarSeverity.Informational); return; }
        var target = chosen.All(i => i.Priority == DownloadPriority.High) ? DownloadPriority.Normal : DownloadPriority.High;
        foreach (var i in chosen) manager.SetPriority(i, target);
        ShowNotice(target == DownloadPriority.High ? "High priority: these go first." : "Back to normal priority.", InfoBarSeverity.Success);
    }

    private void ClearDone_Click(object sender, RoutedEventArgs e) => manager.RemoveFinished();

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Selected();
        if (chosen.Count == 0) return;
        bool anyFile = chosen.Any(i => i.IsFinished || i.DoneBytes > 0);
        string what = chosen.Count == 1 ? $"“{chosen[0].FileName}”" : $"{chosen.Count} downloads";
        var choice = await AskAsync($"Remove {what}?", anyFile ? "You can keep the files that were already downloaded, or delete them too." : "It has not downloaded anything yet.",
            "Remove from list", anyFile ? "Remove and delete files" : null);
        if (choice == ContentDialogResult.None) return;
        foreach (var i in chosen) manager.Remove(i, deleteFile: choice == ContentDialogResult.Secondary);
    }

    // ---- context menu -----------------------------------------------------------------------------------------

    private void List_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: DownloadItem item }) return;
        if (!DownloadList.SelectedItems.Contains(item)) { DownloadList.SelectedItems.Clear(); DownloadList.SelectedItems.Add(item); }

        var menu = new MenuFlyout();
        MenuFlyoutItem Entry(string text, string glyph, Action act, bool enabled = true)
        {
            var mi = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            mi.Click += (_, _) => act();
            return mi;
        }
        if (item.IsFinished) menu.Items.Add(Entry("Open", "", () => SystemActions.OpenFile(item.FilePath)));
        menu.Items.Add(Entry("Show in folder", "", () => SystemActions.ShowInFolder(item.IsFinished ? item.FilePath : item.PartPath)));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (item.CanPause) menu.Items.Add(Entry("Pause", "", () => manager.Pause(item)));
        if (item.CanResume) menu.Items.Add(Entry("Resume", "", () => manager.Start(item)));
        if (!item.IsFinished)
        {
            var priority = new MenuFlyoutSubItem { Text = "Priority", Icon = new FontIcon { Glyph = "" } };
            foreach (var (label, value) in new[] { ("High (goes first)", DownloadPriority.High), ("Normal", DownloadPriority.Normal), ("Low (goes last)", DownloadPriority.Low) })
            {
                var choice = new ToggleMenuFlyoutItem { Text = label, IsChecked = item.Priority == value };
                choice.Click += (_, _) => { foreach (var i in Selected().Where(i => !i.IsFinished)) manager.SetPriority(i, value); };
                priority.Items.Add(choice);
            }
            menu.Items.Add(priority);
        }
        menu.Items.Add(Entry("Download again", "", () => manager.Restart(item)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Entry("Copy address", "", () => CopyAddress(item)));
        if (!item.IsFinished)
        {
            menu.Items.Add(Entry("Refresh link…", "", () => _ = RefreshLinkAsync(item)));
            if (item.Kind == DownloadKind.Http) menu.Items.Add(Entry("Add mirror…", "", () => _ = AddMirrorAsync(item)));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Entry("Remove from list", "", () => Remove_Click(this, new RoutedEventArgs())));
        menu.ShowAt(DownloadList, e.GetPosition(DownloadList));
        e.Handled = true;
    }

    private void CopyAddress(DownloadItem item)
    {
        var data = new DataPackage();
        data.SetText(item.Url);
        lastOffered = item.Url; // copying our own address must not come back as an offer
        Clipboard.SetContent(data);
        ShowNotice("Address copied", InfoBarSeverity.Informational);
    }

    // ---- details panel ----------------------------------------------------------------------------------------

    private static readonly string[] DetailLabels = { "Size", "Downloaded", "Speed", "Time left", "Connections", "Resume", "Saved to", "Address", "Added", "Checksum" };

    private void BuildDetailPanel()
    {
        foreach (var label in DetailLabels)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["MutedText"] });
            var value = new TextBlock { TextWrapping = TextWrapping.WrapWholeWords, IsTextSelectionEnabled = true, FontSize = 12 };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            DetRows.Children.Add(row);
            detailValues[label] = value;
        }

        Button Action(string text, string glyph, Action<DownloadItem> act)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["TileButton"], HorizontalContentAlignment = HorizontalAlignment.Left };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
            content.Children.Add(new TextBlock { Text = text });
            b.Content = content;
            b.Click += (_, _) => { if (Selected().FirstOrDefault() is { } item) act(item); };
            return b;
        }
        DetActions.Children.Add(Action("Open file", "", i => { if (i.IsFinished) SystemActions.OpenFile(i.FilePath); }));
        DetActions.Children.Add(Action("Show in folder", "", i => SystemActions.ShowInFolder(i.IsFinished ? i.FilePath : i.PartPath)));
        DetActions.Children.Add(Action("Copy address", "", CopyAddress));
        DetActions.Children.Add(Action("Refresh link…", "", i => _ = RefreshLinkAsync(i)));
        DetActions.Children.Add(Action("Add mirror…", "", i => _ = AddMirrorAsync(i)));
        DetActions.Children.Add(Action("Download again", "", manager.Restart));
        UpdateDetails();
    }

    private void UpdateDetails()
    {
        var item = Selected().FirstOrDefault();
        DetSegments.Show(item);
        if (item == null)
        {
            DetName.Text = "Select a download to see its details.";
            DetStatus.Text = DetSegmentsHint.Text = "";
            DetGraph.Samples = null;
            foreach (var v in detailValues.Values) v.Text = "";
            DetActions.Visibility = Visibility.Collapsed;
            return;
        }
        DetActions.Visibility = Visibility.Visible;
        DetName.Text = item.FileName;
        DetStatus.Text = item.StatusText;
        DetGraph.Samples = item.SpeedHistory.Length > 1 ? item.SpeedHistory : null;
        int ranges;
        lock (item.Gate) ranges = item.Segments.Count;
        DetSegmentsHint.Text = item.Kind switch
        {
            DownloadKind.Torrent => "Torrents fetch pieces from many peers at once.",
            DownloadKind.Hls => "A video stream, fetched piece by piece.",
            DownloadKind.Ftp => "FTP uses one connection.",
            _ => item.IsFinished ? "" : ranges > 0 ? $"{ranges} range{(ranges == 1 ? "" : "s")}; a finished connection takes half of the biggest range left." : "Waiting to start.",
        };

        void Set(string label, string text) => detailValues[label].Text = text;
        Set("Size", item.TotalBytes > 0 ? Format.Bytes(item.TotalBytes) : "Unknown");
        Set("Downloaded", $"{Format.Bytes(item.DoneBytes)} ({item.Percent:0.#}%)");
        Set("Speed", item.Status == DownloadStatus.Downloading ? Format.Speed(item.Speed) : "–");
        Set("Time left", item.Eta is { } eta ? Format.Duration(eta) : "–");
        Set("Connections", item.Status == DownloadStatus.Downloading ? item.ActiveConnections.ToString() : "–");
        Set("Resume", item.Kind == DownloadKind.Torrent ? "Yes" : !item.Probed ? "Not checked yet" : item.Resumable ? "Supported" : "Not supported by the server");
        Set("Saved to", item.IsFinished ? item.FilePath : item.Directory);
        Set("Address", item.Url + (item.Mirrors.Count > 0 ? $"\n+ {item.Mirrors.Count} mirror{(item.Mirrors.Count == 1 ? "" : "s")}" : ""));
        Set("Added", item.AddedAt.ToString("g"));
        Set("Checksum", item.ActualHash.Length > 0 ? item.ActualHash : item.ExpectedHash.Length > 0 ? "Will be checked: " + item.ExpectedHash : "–");
    }

    // ---- ticking -----------------------------------------------------------------------------------------------

    private void Tick()
    {
        tickCount++;
        try
        {
            SyncList();
            foreach (var item in visible) item.Refresh();
            if (tickCount % 3 == 0) BuildRail();
            UpdateDetails();
            UpdateStatusBar();
            UpdateTaskbar();
            if (tickCount % 3 == 0) _ = CheckClipboardAsync();
        }
        catch (Exception ex) { Log.Write("UI tick failed", ex); }
    }

    private void UpdateStatusBar()
    {
        var all = manager.Snapshot();
        StatusSpeed.Text = Format.Speed(manager.TotalSpeed);
        int active = all.Count(i => i.Status == DownloadStatus.Downloading), waiting = all.Count(i => i.Status == DownloadStatus.Queued);
        StatusCounts.Text = $"{active} downloading · {waiting} waiting";
        StatusQueue.Text = manager.HeldForMeteredNetwork ? "Held: this is a metered connection."
            : !manager.QueueOpen ? $"Queue is waiting for the schedule ({settings.ScheduleStart})."
            : settings.ScheduleEnabled ? $"Schedule: {settings.ScheduleStart}–{(settings.ScheduleStopEnabled ? settings.ScheduleStop : "no end")}"
            : "";
        StatusBrowser.Text = BrowserIntegration.IsInstalled() ? "Browser integration on" : "Browser integration off";
        LimitText.Text = settings.LimiterOn ? "Limit " + Format.Bytes(settings.SpeedLimitKb * 1024L) + "/s" : "No limit";
    }

    private void UpdateTaskbar()
    {
        if (taskbar == null) return;
        var running = manager.Snapshot().Where(i => i.Status == DownloadStatus.Downloading).ToList();
        if (running.Count == 0) { taskbar.Set(TaskbarProgress.State.None, 0); return; }
        long total = running.Where(i => i.TotalBytes > 0).Sum(i => i.TotalBytes), done = running.Where(i => i.TotalBytes > 0).Sum(i => i.DoneBytes);
        if (total <= 0) taskbar.Set(TaskbarProgress.State.Indeterminate, 0);
        else taskbar.Set(TaskbarProgress.State.Normal, done * 100.0 / total);
    }

    // ---- the speed limiter ------------------------------------------------------------------------------------

    private void LimitFlyout_Opening(object? sender, object e)
    {
        loading = true;
        LimitSwitch.IsOn = settings.LimiterOn;
        LimitBox.Value = settings.SpeedLimitKb;
        loading = false;
    }

    private void LimitSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        settings.LimiterOn = LimitSwitch.IsOn;
        settings.Save();
        manager.ApplyLimiter();
    }

    private void LimitBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (loading || double.IsNaN(args.NewValue)) return;
        settings.SpeedLimitKb = (int)Math.Clamp(args.NewValue, 10, 10_000_000);
        settings.Save();
        manager.ApplyLimiter();
    }

    // ---- clipboard, drag and drop -----------------------------------------------------------------------------

    private async Task CheckClipboardAsync()
    {
        if (!settings.WatchClipboard || offscreen || !AppWindow.IsVisible) return;
        uint sequence = SystemActions.ClipboardSequence;
        if (sequence == lastClipboard) return;
        lastClipboard = sequence;

        string text = (await SystemActions.ClipboardTextAsync()).Trim();
        if (!PathUtil.LooksDownloadable(text) || text == lastOffered || manager.Snapshot().Any(i => i.Url == text)) return;
        lastOffered = text;

        string name = PathUtil.NameFromUrl(text);
        var button = new Button { Content = "Download…", Style = (Style)Application.Current.Resources["PrimaryButton"], MinHeight = 28 };
        button.Click += (_, _) =>
        {
            Notice.IsOpen = false;
            var request = new AddRequest();
            request.Urls.Add(text);
            _ = ShowAddDialogAsync(request);
        };
        Notice.Title = "Link copied";
        Notice.Message = name;
        Notice.Severity = InfoBarSeverity.Informational;
        Notice.ActionButton = button;
        Notice.IsOpen = true;
    }

    private async Task AddFromClipboardAsync()
    {
        BringToFront();
        string text = (await SystemActions.ClipboardTextAsync()).Trim();
        var request = new AddRequest();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(PathUtil.LooksDownloadable)) request.Urls.Add(line);
        await ShowAddDialogAsync(request.Urls.Count > 0 ? request : null);
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        Notice.Title = message;
        Notice.Message = "";
        Notice.Severity = severity;
        Notice.ActionButton = null;
        Notice.IsOpen = true;
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        var view = e.DataView;
        if (view.Contains(StandardDataFormats.Text) || view.Contains(StandardDataFormats.WebLink) || view.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Download with EveryDownload";
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        var request = new AddRequest();
        var view = e.DataView;
        try
        {
            if (view.Contains(StandardDataFormats.StorageItems))
                foreach (var item in await view.GetStorageItemsAsync())
                    if (item.Path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) request.Urls.Add(item.Path);
            if (view.Contains(StandardDataFormats.WebLink)) request.Urls.Add((await view.GetWebLinkAsync()).AbsoluteUri);
            if (request.Urls.Count == 0 && view.Contains(StandardDataFormats.Text))
                foreach (var line in (await view.GetTextAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(PathUtil.LooksDownloadable))
                    request.Urls.Add(line);
        }
        catch (Exception ex) { Log.Write("Could not read what was dropped", ex); }

        if (request.Urls.Count == 0) { ShowNotice("That is not a link EveryDownload can download.", InfoBarSeverity.Warning); return; }
        await ShowAddDialogAsync(request);
    }

    // ---- finishing ---------------------------------------------------------------------------------------------

    private async Task OnQueueFinishedAsync()
    {
        if (settings.Notify) tray?.Notify("All downloads finished", "The queue is empty.");
        switch (settings.WhenDone)
        {
            case "exit": await ExitAsync(); break;
            case "sleep" or "hibernate" or "shutdown":
                string action = settings.WhenDone;
                string phrase = action == "shutdown" ? "shut down" : action;
                BringToFront();
                if (await CountdownAsync($"The computer will {phrase} in", 30)) SystemActions.Power(action);
                break;
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closeConfirmed) return;
        args.Cancel = true;
        if (settings.CloseToTray && tray != null)
        {
            AppWindow.Hide();
            if (settings.Notify) tray.Notify("EveryDownload is still running", "Downloads continue in the background. Right-click the tray icon to exit.");
        }
        else _ = ExitAsync();
    }

    private async Task ExitAsync()
    {
        if (closeConfirmed) return;
        closeConfirmed = true;
        timer.Stop();
        try { await manager.ShutdownAsync(); }
        catch (Exception ex) { Log.Write("Shutdown was not clean", ex); }
        tray?.Dispose();
        Environment.Exit(0);
    }
}
