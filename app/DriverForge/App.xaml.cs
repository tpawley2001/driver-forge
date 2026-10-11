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
            if (page is not null) main.NavigateTo(page);
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
