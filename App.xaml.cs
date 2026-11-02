using EveryDownload.Services;
using Microsoft.UI.Xaml;

namespace EveryDownload;

public partial class App : Application
{
    private MainWindow? window;
    private readonly string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();

    public App()
    {
        string exe = Environment.ProcessPath ?? "";

        // The browser starts this exe as its native messaging host: answer it and leave, without opening any UI.
        if (BrowserIntegration.IsHostLaunch(args))
        {
            try { BrowserIntegration.RunHost(exe); }
            catch (Exception ex) { Log.Write("Browser host stopped", ex); }
            Environment.Exit(0);
        }

        // "--install-integration" / "--uninstall-integration" are what the installer runs: do the registry work and leave.
        if (HandleIntegrationFlags(exe)) Environment.Exit(0);

        if (!SingleInstance.Acquire(args)) Environment.Exit(0); // already running: it was handed our arguments

        // Nothing should take the app down in the middle of a download: log it and carry on.
        UnhandledException += (_, e) => { Log.Write("Unhandled UI exception", e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("Unobserved task exception", e.Exception); e.SetObserved(); };

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs launchArgs)
    {
        var request = LaunchRequest.Parse(args);
        bool background = request.Background && request.Adds.Count == 0 && request.GrabUrl == null;
        window = new MainWindow(request, background);
        window.Activate(); // the window has to open once for its content to load; a background start hides it again
        SingleInstance.Listen(later => window.DispatcherQueue.TryEnqueue(() =>
        {
            try { window.Handle(LaunchRequest.Parse(later)); }
            catch (Exception ex) { Log.Write("Could not handle a launch request", ex); }
        }));
    }

    private bool HandleIntegrationFlags(string exe)
    {
        try
        {
            if (args.Contains("--install-integration", StringComparer.OrdinalIgnoreCase))
            {
                BrowserIntegration.Install(exe);
                if (args.Contains("--magnet", StringComparer.OrdinalIgnoreCase)) FileAssociations.Install(exe);
                return true;
            }
            if (args.Contains("--uninstall-integration", StringComparer.OrdinalIgnoreCase))
            {
                BrowserIntegration.Uninstall();
                FileAssociations.Uninstall();
                return true;
            }
        }
        catch (Exception ex) { Log.Write("Browser integration change failed", ex); return true; }
        return false;
    }
}
