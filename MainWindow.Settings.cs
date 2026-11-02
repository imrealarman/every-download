using EveryDownload.Models;
using EveryDownload.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EveryDownload;

// Settings and the first-run questions.
public sealed partial class MainWindow
{
    private static readonly string[] WhenDoneValues = { "nothing", "sleep", "hibernate", "shutdown", "exit" };
    private static readonly string[] WhenDoneNames = { "Do nothing", "Put the computer to sleep", "Hibernate", "Shut the computer down", "Close EveryDownload" };
    private static readonly string[] DayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private bool firstRunShowing;

    private static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Content = text, IsChecked = value, FontSize = 12 };
        box.Click += (_, _) => set(box.IsChecked == true);
        return box;
    }

    private static StackPanel Field(string label, UIElement control) =>
        new() { Spacing = 4, Children = { new TextBlock { Text = label, Style = (Style)Application.Current.Resources["MutedText"] }, control } };

    private static NumberBox Number(double value, double min, double max, Action<int> set)
    {
        var box = new NumberBox { Value = value, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, FontSize = 12, SmallChange = 1 };
        box.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) set((int)e.NewValue); };
        return box;
    }

    private static StackPanel Page(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(2, 14, 14, 6) };
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    private async Task ShowSettingsAsync()
    {
        string exe = Environment.ProcessPath ?? "";

        // ---- General
        var theme = Combo(new[] { "Follow Windows", "Light", "Dark" }, settings.Theme switch { "light" => 1, "dark" => 2, _ => 0 });
        theme.SelectionChanged += (_, _) => { settings.Theme = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" }; ApplyTheme(); };
        bool startup = false;
        try { startup = Startup.IsEnabled(); } catch (Exception ex) { Log.Write("Could not read the startup entry", ex); }
        var whenDone = Combo(WhenDoneNames, Math.Max(0, Array.IndexOf(WhenDoneValues, settings.WhenDone)));
        whenDone.SelectionChanged += (_, _) => settings.WhenDone = WhenDoneValues[whenDone.SelectedIndex];
        var general = Page(
            Field("Theme", theme),
            Check("Start with Windows (hidden in the tray)", startup, on => { try { Startup.Set(on, exe); } catch (Exception ex) { Log.Write("Startup change failed", ex); } }),
            Check("Keep running in the tray when the window is closed", settings.CloseToTray, on => settings.CloseToTray = on),
            Check("Show notifications when downloads finish or fail", settings.Notify, on => settings.Notify = on),
            Check("Offer links I copy to the clipboard as downloads", settings.WatchClipboard, on => settings.WatchClipboard = on),
            Field("When every download is finished", whenDone));

        // ---- Downloads
        var folder = Box("", text: settings.BaseFolder);
        folder.TextChanged += (_, _) => { if (folder.Text.Trim().Length > 0) settings.BaseFolder = folder.Text.Trim(); };
        var connections = Combo(new[] { "Smart Boost (recommended)", "1", "2", "4", "8", "16", "32" }, Math.Max(0, Array.IndexOf(ConnectionChoices, settings.DefaultConnections)));
        connections.SelectionChanged += (_, _) => settings.DefaultConnections = ConnectionChoices[connections.SelectedIndex];
        string defender = Integrity.DefenderPath() != null ? "Microsoft Defender is available." : "Microsoft Defender was not found on this PC.";
        string ffmpeg = Ffmpeg.Find() != null ? "ffmpeg found." : "ffmpeg not found; videos stay as .ts files.";
        var downloads = Page(
            Field("Save downloads in", FolderRow(folder)),
            Check("Sort into folders by type (Video, Music, Documents…)", settings.AutoCategorize, on => settings.AutoCategorize = on),
            Field("Downloads at the same time", Number(settings.MaxSimultaneous, 1, 16, v => settings.MaxSimultaneous = v)),
            Field("Connections per download", connections),
            Field("Most connections Smart Boost may open", Number(settings.MaxConnections, 1, 32, v => settings.MaxConnections = v)),
            Field("Automatic retries when a download fails", Number(settings.AutoRetries, 0, 50, v => settings.AutoRetries = v)),
            Check("Scan finished files for viruses. " + defender, settings.ScanDownloads, on => settings.ScanDownloads = on),
            Check("Turn finished videos into MP4. " + ffmpeg, settings.RemuxVideo, on => settings.RemuxVideo = on),
            Check("Keep sharing torrents after they finish", settings.SeedTorrents, on => settings.SeedTorrents = on));

        // ---- Network
        var proxy = Combo(new[] { "Use the Windows settings", "No proxy", "Use this proxy" }, settings.ProxyMode switch { "none" => 1, "manual" => 2, _ => 0 });
        var proxyHost = Box("Host", text: settings.ProxyHost);
        var proxyUser = Box("User name (optional)", text: settings.ProxyUser);
        var proxyPass = new PasswordBox { PlaceholderText = "Password (optional)", Password = settings.ProxyPassword, FontSize = 12 };
        var proxyPort = Number(settings.ProxyPort, 1, 65535, v => settings.ProxyPort = v);
        var proxyBox = new StackPanel { Spacing = 8, Children = { proxyHost, Field("Port", proxyPort), proxyUser, proxyPass } };
        proxyBox.Visibility = settings.ProxyMode == "manual" ? Visibility.Visible : Visibility.Collapsed;
        proxy.SelectionChanged += (_, _) =>
        {
            settings.ProxyMode = proxy.SelectedIndex switch { 1 => "none", 2 => "manual", _ => "system" };
            proxyBox.Visibility = proxy.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        };
        proxyHost.TextChanged += (_, _) => settings.ProxyHost = proxyHost.Text.Trim();
        proxyUser.TextChanged += (_, _) => settings.ProxyUser = proxyUser.Text;
        proxyPass.PasswordChanged += (_, _) => settings.ProxyPassword = proxyPass.Password;
        var agent = Box("", text: settings.UserAgent);
        agent.TextChanged += (_, _) => settings.UserAgent = agent.Text.Trim();
        var network = Page(
            Field("Proxy", proxy), proxyBox,
            Field("Identify as (User-Agent)", agent),
            Check("Hold downloads while the connection is metered (mobile data, hotspot)", settings.PauseOnMetered, on => settings.PauseOnMetered = on));

        // ---- Schedule
        var start = new TimePicker { MinuteIncrement = 5, Time = TimeSpan.TryParse(settings.ScheduleStart, out var s1) ? s1 : new TimeSpan(2, 0, 0) };
        start.TimeChanged += (_, e) => settings.ScheduleStart = e.NewTime.ToString(@"hh\:mm");
        var stop = new TimePicker { MinuteIncrement = 5, Time = TimeSpan.TryParse(settings.ScheduleStop, out var s2) ? s2 : new TimeSpan(7, 0, 0), IsEnabled = settings.ScheduleStopEnabled };
        stop.TimeChanged += (_, e) => settings.ScheduleStop = e.NewTime.ToString(@"hh\:mm");
        var stopCheck = Check("Stop at", settings.ScheduleStopEnabled, on => { settings.ScheduleStopEnabled = on; stop.IsEnabled = on; });
        var days = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        for (int d = 0; d < 7; d++)
        {
            int bit = 1 << d;
            days.Children.Add(Check(DayNames[d], (settings.ScheduleDays & bit) != 0, on => settings.ScheduleDays = on ? settings.ScheduleDays | bit : settings.ScheduleDays & ~bit));
        }
        var schedule = Page(
            Check("Only run the queue during this window", settings.ScheduleEnabled, on => settings.ScheduleEnabled = on),
            Field("Start at", start), stopCheck, stop, Field("On these days", days),
            new TextBlock { Style = (Style)Application.Current.Resources["MutedText"], TextWrapping = TextWrapping.Wrap, Text = "Downloads you start by hand ignore the schedule. A window that passes midnight (22:00 to 06:00) counts for the day it started." });

        // ---- Browser
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        void ShowState() => state.Text = BrowserIntegration.IsInstalled() ? "The browser hand-off is set up for Chrome, Edge, Brave and Vivaldi." : "The browser hand-off is not set up yet.";
        ShowState();
        var install = new Button { Content = "Set up / repair browser hand-off", Style = (Style)Application.Current.Resources["SecondaryButton"] };
        install.Click += (_, _) => { try { BrowserIntegration.Install(exe); } catch (Exception ex) { Log.Write("Could not register the browser host", ex); } ShowState(); };
        var openFolder = new Button { Content = "Open the extension folder", Style = (Style)Application.Current.Resources["SecondaryButton"] };
        openFolder.Click += (_, _) => SystemActions.ShowInFolder(BrowserIntegration.ExtensionFolder);
        bool magnet = false;
        try { magnet = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\magnet\shell\open\command")?.GetValue(null) is string c && c.Contains("EveryDownload", StringComparison.OrdinalIgnoreCase); } catch { }
        var browser = Page(
            state,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { install, openFolder } },
            new TextBlock
            {
                Style = (Style)Application.Current.Resources["MutedText"], TextWrapping = TextWrapping.Wrap,
                Text = "To add the extension: open chrome://extensions (or edge://extensions), turn on Developer mode, choose Load unpacked, and pick the extension folder. " +
                       "The extension then catches downloads, offers a Download button on videos, and adds “Download with EveryDownload” to the right-click menu.",
            },
            Check("Ask me before adding a download sent by the browser", settings.AskForBrowserDownloads, on => settings.AskForBrowserDownloads = on),
            Check("Open magnet links and .torrent files with EveryDownload", magnet, on => { try { if (on) FileAssociations.Install(exe); else FileAssociations.Uninstall(); } catch (Exception ex) { Log.Write("Could not change file associations", ex); } }));

        var pivot = new Pivot { MinHeight = 420 };
        foreach (var (title, content) in new[] { ("General", general), ("Downloads", downloads), ("Network", network), ("Schedule", schedule), ("Browser", browser) })
            pivot.Items.Add(new PivotItem { Header = title, Content = new ScrollViewer { Content = content, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });

        var dialog = new ContentDialog { Title = "Settings", Content = new Grid { Width = 560, Children = { pivot } }, CloseButtonText = "Done", DefaultButton = ContentDialogButton.Close };
        await ShowDialogAsync(dialog);
        settings.Save();
        manager.ApplySettings();
    }

    // ---- first run --------------------------------------------------------------------------------------------

    private async Task FirstRunAsync()
    {
        if (settings.FirstRunDone || offscreen || firstRunShowing) return;
        firstRunShowing = true;
        try
        {
            string exe = Environment.ProcessPath ?? "";
            bool browser = true, magnet = true, startup = true;
            var panel = new StackPanel { Spacing = 10, Width = 480 };
            panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "EveryDownload splits every file into parts and downloads them side by side, adds connections while your speed keeps rising, and resumes anything that breaks. A few choices to get you started:" });
            panel.Children.Add(Check("Catch downloads from Chrome, Edge and Brave (browser hand-off)", browser, on => browser = on));
            panel.Children.Add(Check("Open magnet links and .torrent files with EveryDownload", magnet, on => magnet = on));
            panel.Children.Add(Check("Start with Windows, hidden in the tray", startup, on => startup = on));
            panel.Children.Add(new TextBlock { Style = (Style)Application.Current.Resources["MutedText"], TextWrapping = TextWrapping.Wrap, Text = "You can change all of this later in Settings." });

            var answer = await ShowDialogAsync(new ContentDialog { Title = "Welcome to EveryDownload", Content = panel, PrimaryButtonText = "Get started", CloseButtonText = "Not now", DefaultButton = ContentDialogButton.Primary });
            Log.Write("First-run dialog closed: " + answer);
            if (answer != ContentDialogResult.Primary) return; // nothing is changed on the PC unless the user says so; ask again next time
            try
            {
                if (browser) BrowserIntegration.Install(exe);
                if (magnet) FileAssociations.Install(exe);
                Startup.Set(startup, exe);
            }
            catch (Exception ex) { Log.Write("First-run setup failed", ex); }
            settings.FirstRunDone = true;
            settings.Save();

            if (browser)
            {
                var open = new Button { Content = "Open extension folder", Style = (Style)Application.Current.Resources["PrimaryButton"], MinHeight = 28 };
                open.Click += (_, _) => SystemActions.ShowInFolder(BrowserIntegration.ExtensionFolder);
                Notice.Title = "One more step for your browser";
                Notice.Message = "Load the extension once: chrome://extensions → Developer mode → Load unpacked → pick the extension folder.";
                Notice.Severity = InfoBarSeverity.Informational;
                Notice.ActionButton = open;
                Notice.IsOpen = true;
            }
        }
        finally { firstRunShowing = false; }
    }
}
