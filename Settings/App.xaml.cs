using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace InfoHubLite;

public partial class App : Application
{
    Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;
        window.Activate();

        string[] a = Environment.GetCommandLineArgs(); // [exe, "--shot", png, tab?]: save a picture of the window and exit
        if (a.Length >= 3 && a[1] == "--shot") _ = Shot(window, a[2], a.Length > 3 ? int.Parse(a[3]) : -1);
        if (a.Length >= 3 && a[1] == "--selftest") _ = SelfTest(window, a[2]); // run the editor's own checks, write the report, exit
    }

    async Task Shot(MainWindow window, string png, int tab)
    {
        try
        {
            await Task.Delay(4500); // let the sensor list and the live view arrive
            window.Select(tab); // -1 = nothing selected (screen and background settings)
            await window.SaveScreenshot(Path.GetFullPath(png));
        }
        catch (Exception e) { File.WriteAllText(Path.GetFullPath(png) + ".error.txt", e.ToString()); }
        Exit();
    }

    async Task SelfTest(MainWindow window, string report)
    {
        await Task.Delay(1500);
        string text;
        try { text = window.SelfTest(); } catch (Exception e) { text = "CRASH " + e; }
        File.WriteAllText(Path.GetFullPath(report), text + Environment.NewLine + "data folder: " + Config.Dir);
        try { Directory.Delete(Config.Dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Exit();
    }

    [STAThread]
    static int Main(string[] args)
    {
        // The self-test applies, reverts and cleans up for real, so it gets a throwaway data folder of its own.
        // This must happen before anything touches Config (its paths are fixed on first use).
        if (args is ["--selftest", ..] && Environment.GetEnvironmentVariable("INFOHUBLITE_DATA") is null or "")
            Environment.SetEnvironmentVariable("INFOHUBLITE_DATA", Path.Combine(Path.GetTempPath(), "InfoHubLite-selftest-" + Environment.ProcessId));

        if (args is ["--import", string src, string name, ..]) // headless conversion, same code path as the "add media" button
        {
            bool portrait = args.Contains("--portrait");
            string dst = Media.PathFor(Path.GetFileNameWithoutExtension(name) + ".ihl", portrait);
            Directory.CreateDirectory(Config.MediaDir);
            try
            {
                if (Video.Extensions.Contains(Path.GetExtension(src).ToLowerInvariant())) Task.Run(() => Video.Import(Path.GetFullPath(src), dst, portrait)).GetAwaiter().GetResult();
                else Media.Import(Path.GetFullPath(src), dst, portrait, null);
                return 0;
            }
            catch (Exception e) { File.WriteAllText(dst + ".error.txt", e.ToString()); return 1; }
        }

        bool shot = args is ["--shot", ..] or ["--selftest", ..];
        using var single = new Mutex(true, "InfoHubLite.Settings.Single" + Config.Scope, out bool first);
        if (!first && !shot) return 0; // ponytail: a second launch just exits instead of focusing the open window

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
        return 0;
    }
}
