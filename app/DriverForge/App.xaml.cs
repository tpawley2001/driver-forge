using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DriverForge.Core;
using DriverForge.ViewModels;

namespace DriverForge;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        string data = DataDirectory();
        IDriverEngine engine = e.Args.Contains("--demo") ? new DemoEngine() : new WindowsEngine(data);

        // --schedule Off|Daily|Weekly: set the scheduled scan without opening the app (installer, tests).
        if (Arg(e.Args, "--schedule") is { } schedule)
        {
            int code = 0;
            try
            {
                var s = Enum.Parse<ScanSchedule>(schedule, ignoreCase: true);
                ScheduledScan.Apply(s, Environment.ProcessPath!);
                var settings = AppSettings.Load(Path.Combine(data, "settings.json"));
                settings.ScanSchedule = s;
                settings.Save(Path.Combine(data, "settings.json"));
            }
            catch (Exception ex) when (ex is EngineException or ArgumentException)
            {
                File.WriteAllText(Path.Combine(data, "schedule.error.txt"), ex.Message);
                code = 1;
            }
            Shutdown(code);
            return;
        }

        // --apps-probe <winget id> [--install]: milestone A1 (APPS.md §10), no window.
        // Proves the in-process winget engine from this elevated process; the
        // log goes to apps-probe.log in the data folder.
        if (Arg(e.Args, "--apps-probe") is { } probeId)
        {
            var (ok, log) = Task.Run(() => DriverForge.Core.Apps.AppsProbe.RunAsync(probeId, e.Args.Contains("--install")))
                                .GetAwaiter().GetResult();
            File.WriteAllLines(Path.Combine(data, "apps-probe.log"), log);
            Shutdown(ok ? 0 : 1);
            return;
        }

        // --apps-install / --apps-update / --apps-json …: the Apps command line (APPS.md §8), no window.
        if (DriverForge.Core.Apps.AppsCli.Handles(e.Args))
        {
            int code;
            try { code = Task.Run(() => DriverForge.Core.Apps.AppsCli.RunAsync(e.Args, data)).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(data, "last-apps.error.txt"), ex.ToString());
                code = 2;
            }
            Shutdown(code);
            return;
        }

        // Scheduled scan (SPEC §9): no window; a notification when something needs attention.
        if (e.Args.Contains("--scan-only"))
        {
            ScanOnly(engine, data);
            return;
        }

        // Headless: write the scan as JSON and exit. Used to check the scan
        // against Windows' own device list on the test box (build/check-scan.sh).
        if (Arg(e.Args, "--scan-json") is { } json)
        {
            try
            {
                var report = Task.Run(() => engine.ScanAsync(null)).GetAwaiter().GetResult();
                File.WriteAllText(json, ScanJson.Write(report));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(json + ".error.txt", ex.ToString());
                Shutdown(1);
            }
            return;
        }

        var main = new MainViewModel(engine, data);
        var window = new MainWindow { DataContext = main };
        MainWindow = window;

        string? shot = Arg(e.Args, "--screenshot");
        if (shot is not null)
        {
            main.Confirm = _ => false;
            // Render off-screen without taking focus: used to check the UI on a
            // machine someone may be using.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
        }
        window.Show();
        var scan = main.StartAsync();
        if (shot is not null)
            _ = ScreenshotAsync(window, main, scan, shot, Arg(e.Args, "--page"), Arg(e.Args, "--select"),
                e.Args.Contains("--test-install"), e.Args.Contains("--real-install"));
    }

    /// <summary>
    /// Portable copy: data lives beside the exe. Installed copy (the installer
    /// leaves installed.txt): the exe is under Program Files, which isn't for
    /// data, so settings, backups, history and caches go to ProgramData.
    /// </summary>
    static string DataDirectory()
    {
        string root = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(root, "installed.txt"))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Driver Forge")
            : root;
    }

    void ScanOnly(IDriverEngine engine, string data)
    {
        try
        {
            var settings = AppSettings.Load(Path.Combine(data, "settings.json"));
            var report = Task.Run(() => engine.ScanAsync(null)).GetAwaiter().GetResult();
            var summary = ScanSummary.From(report, settings.IgnoredDevices);
            // App pass (APPS.md §8). A broken winget never costs the driver notification.
            try
            {
                var (pending, updated) = Task.Run(() => DriverForge.Core.Apps.ScheduledApps.RunAsync(data, settings)).GetAwaiter().GetResult();
                summary = summary with { AppUpdates = pending, AppsUpdated = updated };
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(data, "last-apps.error.txt"), ex.ToString()); }
            summary.Save(Path.Combine(data, "last-scan.json"));
            if (summary.Total > 0 || summary.AppsUpdated > 0) Notify(summary);
        }
        catch (Exception ex) when (ex is EngineException or IOException or UnauthorizedAccessException)
        {
            File.WriteAllText(Path.Combine(data, "last-scan.error.txt"), ex.ToString());
        }
        Shutdown(0);
    }

    /// <summary>A notification-area balloon (shown as a Windows notification); clicking it opens the app.</summary>
    static void Notify(ScanSummary summary)
    {
        bool clicked = TrayBalloon.Show("Driver Forge", summary.Text + " Click to review.", TimeSpan.FromSeconds(15));
        if (clicked) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
    }

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    async Task ScreenshotAsync(MainWindow window, MainViewModel main, Task scan, string path, string? page, string? select, bool testInstall, bool realInstall)
    {
        try
        {
            await scan;
            if (Environment.GetCommandLineArgs().Contains("--restore-point-test"))
            {
                var r = await Task.Run(() => RestorePoints.Create("Driver Forge restore point test"));
                await File.WriteAllTextAsync(path + ".txt", $"{r.Created}: {r.Message}");
            }
            if (select is not null) main.Drivers.SelectByName(select);
            // Automation simulates installs unless --real-install is named
            // explicitly; either way only the one --select'ed device.
            if ((testInstall || realInstall) && select is not null && main.Drivers.Selected is { CanInstall: true } row)
                await main.InstallAsync([row], testMode: !realInstall);
            // --rollback <device>: roll back that device's newest change (real unless test mode is on).
            if (Arg(Environment.GetCommandLineArgs(), "--rollback") is { } rollback
                && main.Engine.History().FirstOrDefault(h => h.CanRollBack && h.DeviceName.Contains(rollback, StringComparison.OrdinalIgnoreCase)) is { } entry)
            {
                await main.History.RollBackAsync(entry, askFirst: false);
                page ??= "History";
            }
            // --uninstall <device>: remove that device's third-party driver (real unless test mode is on).
            if (Arg(Environment.GetCommandLineArgs(), "--uninstall") is { } uninstall)
            {
                main.Drivers.SelectByName(uninstall);
                if (main.Drivers.Selected is { CanUninstall: true } u) await main.Drivers.UninstallAsync(u, askFirst: false);
            }
            // --remove-ghost <instance ID part>: remove matching ghost devices.
            if (Arg(Environment.GetCommandLineArgs(), "--remove-ghost") is { } ghost)
            {
                await main.Cleanup.LoadAsync();
                await main.Cleanup.RemoveAsync(main.Cleanup.Rows.Where(r => r.InstanceId.Contains(ghost, StringComparison.OrdinalIgnoreCase)).ToList(), askFirst: false);
                page ??= "Cleanup";
            }
            if (page is not null) main.NavigateTo(page);
            if (main.CurrentPage is SystemInfoViewModel si) await si.LoadAsync();
            await Task.Delay(800);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            var content = (FrameworkElement)window.Content;
            var dpi = VisualTreeHelper.GetDpi(window);
            double w = content.ActualWidth, h = content.ActualHeight;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, w, h));
            }
            var bmp = new RenderTargetBitmap((int)(w * dpi.DpiScaleX), (int)(h * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            await using (var fs = File.Create(path)) png.Save(fs);
        }
        finally
        {
            Shutdown();
        }
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "Driver Forge", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
