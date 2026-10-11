using System.Runtime.InteropServices;

namespace DriverForge.Core;

/// <summary>Ghost devices (SPEC §8.2): devices Windows remembers that aren't connected.</summary>
public static class Ghosts
{
    // Present only while their parent is: a removable disk's volumes, a phone's
    // portable-device entries, audio endpoints, software devices. Listed, never pre-selected.
    static readonly string[] ByNatureClasses = ["Volume", "VolumeSnapshot", "SoftwareDevice", "AudioEndpoint", "WPD", "DigitalMediaDevices", "PrintQueue"];
    static readonly string[] ByNaturePrefixes = [@"SWD\", @"SW\", @"STORAGE\VOLUME", @"ROOT\", @"HTREE\"];

    /// <summary>Worth suggesting for removal: hardware that was unplugged, not a by-product of other devices.</summary>
    public static bool Suggested(Device d) =>
        !d.IsPresent
        && !ByNatureClasses.Contains(d.Class, StringComparer.OrdinalIgnoreCase)
        && !ByNaturePrefixes.Any(p => d.InstanceId.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes a ghost's device entry (DiUninstallDevice). Refuses a connected device.</summary>
    public static void Remove(string instanceId)
    {
        using var dev = DeviceHandle.Open(instanceId) ?? throw new EngineException("Windows no longer knows this device.");
        if (Native.CM_Get_DevNode_Status(out _, out _, dev.Info.DevInst, 0) != Native.CR_NO_SUCH_DEVINST)
            throw new EngineException("This device is connected; only devices that aren't connected are removed here.");
        if (!Native.DiUninstallDevice(0, dev.Set, ref dev.Info, 0, out _))
        {
            int err = Marshal.GetLastWin32Error();
            throw new EngineException(err == Native.ERROR_ACCESS_DENIED
                ? "Removing a device needs administrator rights."
                : $"Windows couldn't remove the device (error 0x{err:X8}).");
        }
    }
}

/// <summary>Removes a third-party driver package from Windows (SPEC §8.1).</summary>
public static class DriverRemoval
{
    const int ErrorSuccessRebootRequired = 3010;

    /// <summary>
    /// pnputil /delete-driver oemNN.inf /uninstall: off every device using it,
    /// and out of the driver store. Returns whether a restart finishes it.
    /// </summary>
    public static bool Delete(string oemInf)
    {
        if (!DriverBackup.IsThirdParty(oemInf))
            throw new EngineException("Drivers that come with Windows can't be uninstalled.");
        var (code, output) = DriverBackup.Run("pnputil.exe", $"/delete-driver {Path.GetFileName(oemInf)} /uninstall");
        if (code is not (0 or ErrorSuccessRebootRequired))
            throw new EngineException($"Windows couldn't remove {Path.GetFileName(oemInf)}: {Last(output)}");
        return code == ErrorSuccessRebootRequired;
    }

    static string Last(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}

internal static partial class Native
{
    [DllImport("newdev.dll", SetLastError = true)]
    public static extern bool DiUninstallDevice(nint hwndParent, nint set, ref SP_DEVINFO_DATA info, uint flags, out int needReboot);
}
