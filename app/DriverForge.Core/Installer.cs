using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DriverForge.Core;

/// <summary>
/// Puts one specific driver on one specific device (SPEC §6.1 step 3):
/// stage the package in the driver store, find its driver node for the
/// device with DI_ENUMSINGLEINF, then DiInstallDevice with that node.
/// </summary>
public static class DriverInstaller
{
    static readonly string InfDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

    /// <summary>The package's INF in the driver store (%windir%\INF\oemNN.inf or an inbox INF), staging it first if needed.</summary>
    public static string Stage(string infPath)
    {
        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(infPath))?.TrimEnd('\\'), InfDir, StringComparison.OrdinalIgnoreCase))
            return infPath;
        var dest = new char[260];
        if (!Native.SetupCopyOEMInfW(infPath, null, Native.SPOST_PATH, 0, dest, dest.Length, out _, 0))
        {
            int err = Marshal.GetLastWin32Error();
            throw new EngineException(err switch
            {
                Native.ERROR_FILE_NOT_FOUND or Native.ERROR_PATH_NOT_FOUND => $"The driver package is missing ({infPath}).",
                Native.ERROR_ACCESS_DENIED => "Adding a driver to Windows needs administrator rights.",
                _ when (uint)err == 0xE000022F => "Windows refused the package: it isn't signed properly.",
                _ => $"Windows couldn't add the driver package to the driver store (error 0x{err:X8}).",
            });
        }
        return PropertyData.String(dest);
    }

    /// <summary>Installs the node <paramref name="pick"/> chooses from <paramref name="storeInf"/>. Returns whether a restart is needed.</summary>
    public static bool InstallOnDevice(string instanceId, string storeInf, Func<DriverNode, bool> pick)
    {
        using var dev = DeviceHandle.Open(instanceId) ?? throw new EngineException("The device is gone (unplugged?).");
        if (!dev.SetSingleInf(storeInf))
            throw new EngineException($"Windows wouldn't read {Path.GetFileName(storeInf)}.");
        bool? reboot = DriverNodes.WithNodes<bool?>(dev, nodes =>
        {
            if (nodes.Count == 0)
                throw new EngineException($"{Path.GetFileName(storeInf)} has no driver for this device.");
            var hit = nodes.FirstOrDefault(n => pick(n.Node));
            if (hit.Node is null)
                throw new EngineException($"{Path.GetFileName(storeInf)} doesn't contain the chosen driver for this device.");
            var drv = hit.Drv;
            if (!Native.DiInstallDevice(0, dev.Set, ref dev.Info, ref drv, 0, out int needReboot))
            {
                int err = Marshal.GetLastWin32Error();
                throw new EngineException(err == Native.ERROR_ACCESS_DENIED
                    ? "Installing a driver needs administrator rights."
                    : $"Windows couldn't install the driver (error 0x{err:X8}).");
            }
            return needReboot != 0;
        });
        return reboot ?? throw new EngineException("Windows couldn't list the drivers for this device.");
    }
}

/// <summary>Copies a third-party driver package out of the driver store before it's replaced (SPEC §7).</summary>
public static class DriverBackup
{
    public static bool IsThirdParty(string inf) => Path.GetFileName(inf).StartsWith("oem", StringComparison.OrdinalIgnoreCase);

    /// <summary>pnputil /export-driver into backups\yyyyMMdd-HHmmss-oemNN; returns the folder.</summary>
    public static string Export(string oemInf, string backupsDir, DateTime at)
    {
        string name = Path.GetFileNameWithoutExtension(oemInf);
        string dir = Path.Combine(backupsDir, $"{at:yyyyMMdd-HHmmss}-{name}");
        Directory.CreateDirectory(dir);
        var (code, output) = Run("pnputil.exe", $"/export-driver {Path.GetFileName(oemInf)} \"{dir}\"");
        if (code != 0 || !Directory.EnumerateFiles(dir, "*.inf", SearchOption.AllDirectories).Any())
            throw new EngineException($"Couldn't back up {Path.GetFileName(oemInf)}: {output.Trim()}");
        return dir;
    }

    /// <summary>The INF to reinstall from a backup folder: the one named like the original package, else the only one.</summary>
    public static string? FindInf(string backupDir, string originalInfName)
    {
        if (!Directory.Exists(backupDir)) return null;
        var infs = Directory.EnumerateFiles(backupDir, "*.inf", SearchOption.AllDirectories).ToList();
        return infs.Count == 1 ? infs[0]
            : infs.FirstOrDefault(f => string.Equals(Path.GetFileName(f), originalInfName, StringComparison.OrdinalIgnoreCase)) ?? infs.FirstOrDefault();
    }

    internal static (int Code, string Output) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new EngineException($"Couldn't start {exe}.");
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}

internal static partial class Native
{
    public const uint SPOST_PATH = 1;
    public const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_ACCESS_DENIED = 5;

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupCopyOEMInfW(string sourceInf, string? mediaLocation, uint mediaType, uint copyStyle,
        char[] destination, int destinationSize, out int required, nint destinationComponent);

    [DllImport("newdev.dll", SetLastError = true)]
    public static extern bool DiInstallDevice(nint hwndParent, nint set, ref SP_DEVINFO_DATA info, ref SP_DRVINFO_DATA_V2 drv,
        uint flags, out int needReboot);
}
