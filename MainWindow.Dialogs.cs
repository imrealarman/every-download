using EveryDownload.Models;
using EveryDownload.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EveryDownload;

// The dialogs. They are built in code: each is a short form, and the controls feed straight into an AddRequest or the settings.
public sealed partial class MainWindow
{
    private static readonly int[] ConnectionChoices = { 0, 1, 2, 4, 8, 16, 32 };

    // ---- plumbing ---------------------------------------------------------------------------------------------

    /// <summary>Shows a dialog once any other one has been closed (only one can be open at a time).</summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await dialogGate.WaitAsync();
        try
        {
            dialog.XamlRoot = Root.XamlRoot;
            dialog.RequestedTheme = Root.ActualTheme;
            return await dialog.ShowAsync();
        }
        finally { dialogGate.Release(); }
    }

    private Task<ContentDialogResult> AskAsync(string title, string text, string primary, string? secondary = null) =>
        ShowDialogAsync(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            SecondaryButtonText = secondary ?? "",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        });

    /// <summary>Counts down; true if it ran out, false if the user cancelled.</summary>
    private async Task<bool> CountdownAsync(string text, int seconds)
    {
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var dialog = new ContentDialog { Title = "All downloads finished", Content = label, PrimaryButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        int left = seconds;
        bool expired = false;
        void Update() => label.Text = $"{text} {left} s.";
        Update();
        var countdown = DispatcherQueue.CreateTimer();
        countdown.Interval = TimeSpan.FromSeconds(1);
        countdown.Tick += (_, _) =>
        {
            if (--left <= 0) { expired = true; countdown.Stop(); dialog.Hide(); }
            else Update();
        };
        countdown.Start();
        await ShowDialogAsync(dialog);
        countdown.Stop();
        return expired;
    }

    private static TextBox Box(string placeholder = "", bool multiline = false, string text = "")
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = text, FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["AppFont"], FontSize = 12 };
        if (multiline) { box.AcceptsReturn = true; box.TextWrapping = TextWrapping.NoWrap; box.MinHeight = 76; box.MaxHeight = 140; ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto); }
        return box;
    }

    private static TextBlock Label(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["PanelHeader"], Margin = new Thickness(0, 10, 0, 4) };

    private static ComboBox Combo(IEnumerable<string> items, int selected)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        foreach (var i in items) combo.Items.Add(i);
        combo.SelectedIndex = selected;
        return combo;
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private UIElement FolderRow(TextBox box)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(box);
        var browse = new Button { Content = "Browse…", Style = (Style)Application.Current.Resources["SecondaryButton"], MinHeight = 30 };
        browse.Click += async (_, _) => { if (await PickFolderAsync() is { } folder) box.Text = folder; };
        Grid.SetColumn(browse, 1);
        grid.Children.Add(browse);
        return grid;
    }

    // ---- toolbar entry points ---------------------------------------------------------------------------------

    private void Add_Click(object sender, RoutedEventArgs e) => _ = ShowAddDialogAsync(null);
    private void Batch_Click(object sender, RoutedEventArgs e) => _ = ShowBatchDialogAsync();
    private void Grabber_Click(object sender, RoutedEventArgs e) => _ = ShowGrabberAsync(null);
    private void Settings_Click(object sender, RoutedEventArgs e) => _ = ShowSettingsAsync();

    // ---- add --------------------------------------------------------------------------------------------------

    /// <summary>The main "new download" form. <paramref name="preset"/> carries what the browser or the clipboard already knows.</summary>
    private async Task ShowAddDialogAsync(AddRequest? preset)
    {
        var urls = Box("Paste one or more links, one per line (web, FTP, magnet or .torrent)", multiline: true, text: string.Join("\n", preset?.Urls ?? new List<string>()));
        var info = new TextBlock { Style = (Style)Application.Current.Resources["MutedText"], TextWrapping = TextWrapping.Wrap, Text = " " };
        var name = Box("File name (leave empty to use the server's)", text: preset?.FileName ?? "");
        var folder = Box("", text: preset?.Directory ?? (string.IsNullOrEmpty(settings.LastFolder) ? "" : settings.LastFolder));
        folder.PlaceholderText = "Automatic: " + settings.BaseFolder + (settings.AutoCategorize ? "\\<type>" : "");
        var connections = Combo(ConnectionChoices.Select(c => c == 0 ? "Smart Boost (automatic)" : c + (c == 1 ? " connection" : " connections")), 0);
        var start = Combo(new[] { "Download now", "Add to the queue", "Don't start (download later)", "Start at a set time" }, 0);
        var time = new TimePicker { Time = DateTime.Now.AddHours(1).TimeOfDay, Visibility = Visibility.Collapsed, MinuteIncrement = 5 };
        start.SelectionChanged += (_, _) => time.Visibility = start.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;

        var mirrors = Box("Other addresses of the same file, one per line", multiline: true);
        var referer = Box("Referrer", text: preset?.Referer ?? "");
        var cookie = Box("Cookie", text: preset?.Cookie ?? "");
        var headers = Box("Extra headers, one per line (Name: value)", multiline: true, text: preset?.Headers ?? "");
        var user = Box("User name", text: preset?.Username ?? "");
        var password = new PasswordBox { PlaceholderText = "Password", FontSize = 12 };
        var hash = Box("Expected checksum (MD5, SHA-1, SHA-256 or SHA-512)", text: preset?.ExpectedHash ?? "");
        var limit = new NumberBox { PlaceholderText = "Speed limit for this download (KB/s, empty = none)", Minimum = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, FontSize = 12 };
        var more = new Expander
        {
            Header = "More options: mirrors, login, checksum, speed limit",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 8, Children = { mirrors, referer, cookie, headers, user, password, hash, limit } },
        };

        // Ask the server about a single address as soon as it is typed: name, size and whether it can resume.
        CancellationTokenSource? probing = null;
        async void Probe()
        {
            probing?.Cancel();
            probing = new CancellationTokenSource();
            var token = probing.Token;
            var lines = urls.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length != 1 || !PathUtil.LooksDownloadable(lines[0])) { info.Text = lines.Length > 1 ? $"{lines.Length} links" : " "; return; }
            info.Text = "Checking the link…";
            try
            {
                await Task.Delay(350, token);
                var details = new AddRequest { Referer = referer.Text, Cookie = cookie.Text, Headers = headers.Text, Username = user.Text, Password = password.Password };
                var result = await manager.ProbeAsync(lines[0], details, token);
                if (token.IsCancellationRequested) return;
                info.Text = $"{result.FileName}  ·  {(result.Size > 0 ? Format.Bytes(result.Size) : "unknown size")}  ·  {(result.Resumable ? "can resume and use several connections" : "no resume support")}";
                if (name.Text.Length == 0) name.PlaceholderText = result.FileName;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!token.IsCancellationRequested) info.Text = "Could not check the link: " + (ex is FatalDownloadException ? ex.Message : "the server did not answer."); }
        }
        urls.TextChanged += (_, _) => Probe();
        Probe();

        var panel = new StackPanel { Spacing = 4, Width = 520 };
        panel.Children.Add(urls);
        panel.Children.Add(info);
        panel.Children.Add(Label("SAVE AS"));
        panel.Children.Add(name);
        panel.Children.Add(Label("SAVE IN"));
        panel.Children.Add(FolderRow(folder));
        panel.Children.Add(Label("CONNECTIONS"));
        panel.Children.Add(connections);
        panel.Children.Add(Label("WHEN"));
        panel.Children.Add(start);
        panel.Children.Add(time);
        panel.Children.Add(new Border { Height = 8 });
        panel.Children.Add(more);

        var dialog = new ContentDialog
        {
            Title = "Add download",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await ShowDialogAsync(dialog);
        probing?.Cancel();
        if (result != ContentDialogResult.Primary) return;

        var request = new AddRequest
        {
            FileName = name.Text.Trim(),
            Directory = folder.Text.Trim().Length > 0 ? folder.Text.Trim() : null,
            Connections = ConnectionChoices[connections.SelectedIndex],
            Referer = referer.Text.Trim(),
            Cookie = cookie.Text.Trim(),
            UserAgent = preset?.UserAgent ?? "",
            Headers = headers.Text.Trim(),
            Username = user.Text.Trim(),
            Password = password.Password,
            ExpectedHash = hash.Text.Trim(),
            SpeedLimitKb = double.IsNaN(limit.Value) ? 0 : (int)limit.Value,
            Start = start.SelectedIndex switch { 0 => StartMode.Now, 2 => StartMode.Later, _ => StartMode.Queue },
        };
        foreach (var line in urls.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) request.Urls.Add(line);
        request.Mirrors.AddRange(mirrors.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (start.SelectedIndex == 3)
        {
            var at = DateTime.Today + time.Time;
            request.StartAt = at > DateTime.Now ? at : at.AddDays(1);
            request.Start = StartMode.Queue;
        }
        if (request.Urls.Count == 0) return;
        if (request.Directory != null) { settings.LastFolder = request.Directory; settings.Save(); }

        manager.Add(request);
        ShowNotice(request.Urls.Count == 1 ? "Download added" : $"{request.Urls.Count} downloads added", InfoBarSeverity.Success);
    }

    // ---- batch ------------------------------------------------------------------------------------------------

    private async Task ShowBatchDialogAsync()
    {
        var pattern = Box("http://site.com/photo[001-050].jpg", multiline: true);
        var preview = new TextBlock { Style = (Style)Application.Current.Resources["MutedText"], TextWrapping = TextWrapping.Wrap, Text = "Type an address with a range in square brackets." };
        pattern.TextChanged += (_, _) =>
        {
            var all = UrlPattern.ExpandLines(pattern.Text);
            preview.Text = all.Count == 0 ? "Type an address with a range in square brackets."
                : $"{all.Count} address{(all.Count == 1 ? "" : "es")}:\n" + string.Join("\n", all.Take(4)) + (all.Count > 4 ? "\n…\n" + all[^1] : "");
        };
        var folder = Box("", text: settings.LastFolder);
        folder.PlaceholderText = "Automatic: " + settings.BaseFolder;
        var help = new TextBlock
        {
            Style = (Style)Application.Current.Resources["MutedText"], TextWrapping = TextWrapping.Wrap,
            Text = "[1-20] counts 1 to 20 · [001-120] keeps the zeros · [a-e] letters · [1-99:2] every second number · several ranges can be combined.",
        };
        var panel = new StackPanel { Spacing = 6, Width = 520, Children = { pattern, help, preview, Label("SAVE IN"), FolderRow(folder) } };
        var result = await ShowDialogAsync(new ContentDialog { Title = "Batch download", Content = panel, PrimaryButtonText = "Add all", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary });
        if (result != ContentDialogResult.Primary) return;

        var request = new AddRequest { Directory = folder.Text.Trim().Length > 0 ? folder.Text.Trim() : null };
        request.Urls.AddRange(UrlPattern.ExpandLines(pattern.Text));
        if (request.Urls.Count == 0) return;
        manager.Add(request);
        ShowNotice($"{request.Urls.Count} downloads added", InfoBarSeverity.Success);
    }

    // ---- site grabber -----------------------------------------------------------------------------------------

    private async Task ShowGrabberAsync(string? address)
    {
        var url = Box("Address of a web page", text: address ?? "");
        var kind = Combo(SiteGrabber.Filters.Select(f => f.Name), 0);
        var depth = Combo(new[] { "This page only", "This page and the pages it links to" }, 0);
        var go = new Button { Content = "Find files", Style = (Style)Application.Current.Resources["PrimaryButton"] };
        var status = new TextBlock { Style = (Style)Application.Current.Resources["MutedText"], Text = "Enter a page, choose what to look for, and press Find files." };
        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, Height = 260, ItemContainerStyle = (Style)Application.Current.Resources["BareListViewItem"] };
        var found = new List<FoundFile>();
        var selectAll = new Button { Content = "Select all", Style = (Style)Application.Current.Resources["GhostButton"], MinHeight = 26 };
        selectAll.Click += (_, _) => list.SelectAll();

        CancellationTokenSource? cts = null;
        async Task Find()
        {
            if (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https")) { status.Text = "That is not a web address."; return; }
            cts?.Cancel();
            cts = new CancellationTokenSource();
            status.Text = "Reading the page…";
            list.Items.Clear();
            found.Clear();
            try
            {
                var results = await SiteGrabber.GrabAsync(manager.Client, page.AbsoluteUri, new DownloadItem(), settings.UserAgent,
                    SiteGrabber.Filters[kind.SelectedIndex].Extensions, depth.SelectedIndex == 0 ? 1 : 2, cts.Token);
                found.AddRange(results);
                foreach (var f in found)
                    list.Items.Add(new TextBlock { Text = $"{f.Name}   ·   {f.Category}", Margin = new Thickness(10, 8, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis });
                status.Text = found.Count == 0 ? "No matching files were found on that page." : $"{found.Count} file{(found.Count == 1 ? "" : "s")} found. Tick the ones you want.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { status.Text = "Could not read that page: " + ex.Message; }
        }
        go.Click += (_, _) => _ = Find();

        var panel = new StackPanel { Spacing = 6, Width = 560 };
        panel.Children.Add(url);
        panel.Children.Add(new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { kind, depth, go },
        });
        Grid.SetColumn(depth, 1);
        Grid.SetColumn(go, 2);
        panel.Children.Add(status);
        panel.Children.Add(selectAll);
        panel.Children.Add(list);

        var dialog = new ContentDialog { Title = "Site grabber", Content = panel, PrimaryButtonText = "Download selected", CloseButtonText = "Close", DefaultButton = ContentDialogButton.Primary };
        if (!string.IsNullOrEmpty(address)) dialog.Opened += (_, _) => _ = Find(); // opened from the browser: start looking straight away
        var opened = ShowDialogAsync(dialog);
        var result = await opened;
        cts?.Cancel();
        if (result != ContentDialogResult.Primary) return;

        var request = new AddRequest { Referer = url.Text.Trim() };
        foreach (var index in list.SelectedItems.Select(i => list.Items.IndexOf(i))) request.Urls.Add(found[index].Url);
        if (request.Urls.Count == 0) return;
        manager.Add(request);
        ShowNotice($"{request.Urls.Count} downloads added", InfoBarSeverity.Success);
    }

    // ---- changing an existing download ------------------------------------------------------------------------

    private async Task RefreshLinkAsync(DownloadItem item)
    {
        var box = Box("New address of the same file", text: "");
        var note = new TextBlock { Style = (Style)Application.Current.Resources["MutedText"], TextWrapping = Wrap, Text = "Use this when a link has expired. If the new link is the same size, what was already downloaded is kept." };
        var result = await ShowDialogAsync(new ContentDialog
        {
            Title = "Refresh link",
            Content = new StackPanel { Spacing = 8, Width = 480, Children = { box, note } },
            PrimaryButtonText = "Resume with this link",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (result != ContentDialogResult.Primary || !PathUtil.LooksDownloadable(box.Text.Trim())) return;
        try
        {
            bool kept = await manager.RefreshLinkAsync(item, box.Text.Trim(), CancellationToken.None);
            ShowNotice(kept ? "Link refreshed. The download continues where it stopped." : "Link refreshed. The file is different, so it starts again.", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowNotice("Could not use that link: " + ex.Message, InfoBarSeverity.Error); }
    }

    private async Task AddMirrorAsync(DownloadItem item)
    {
        var box = Box("Another address of the same file");
        var result = await ShowDialogAsync(new ContentDialog
        {
            Title = "Add mirror",
            Content = new StackPanel { Spacing = 8, Width = 480, Children = { box, new TextBlock { Style = (Style)Application.Current.Resources["MutedText"], TextWrapping = Wrap, Text = "Connections are spread over every address, and a failing address is skipped." } } },
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (result != ContentDialogResult.Primary || !PathUtil.LooksDownloadable(box.Text.Trim())) return;
        item.Mirrors.Add(box.Text.Trim());
        manager.SaveNow();
        ShowNotice("Mirror added", InfoBarSeverity.Success);
    }

    private static readonly TextWrapping Wrap = TextWrapping.Wrap;
}
