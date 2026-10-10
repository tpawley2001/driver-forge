using System.Management;
using Microsoft.Win32;

namespace DriverForge.Core;

public sealed record RestorePointResult(bool Created, string Message);

/// <summary>
/// Creates a System Restore point and checks that it really exists. Windows
/// reports success either way: it finishes the point in the background (half
/// a minute or so after the call returns) and only then applies its
/// one-point-per-24-hours rule. So the rule is lifted until the new point is
/// listed, and only a listed point counts.
/// </summary>
public static class RestorePoints
{
    const string Key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    const string Frequency = "SystemRestorePointCreationFrequency";
    const uint DeviceDriverInstall = 10, BeginSystemChange = 100;
    static readonly TimeSpan Wait = TimeSpan.FromMinutes(3);

    public static RestorePointResult Create(string description)
    {
        uint? before;
        try { before = LatestSequence(); }
        catch (ManagementException ex) { return new(false, "System Restore isn't available: " + ex.Message); }

        using var key = Registry.LocalMachine.CreateSubKey(Key, writable: true);
        object? old = key.GetValue(Frequency);
        key.SetValue(Frequency, 0, RegistryValueKind.DWord);
        try
        {
            uint rv;
            using (var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null))
            using (var args = cls.GetMethodParameters("CreateRestorePoint"))
            {
                args["Description"] = description;
                args["RestorePointType"] = DeviceDriverInstall;
                args["EventType"] = BeginSystemChange;
                using var result = cls.InvokeMethod("CreateRestorePoint", args, null);
                rv = Convert.ToUInt32(result["ReturnValue"]);
            }
            if (rv != 0) return new(false, Explain(rv));

            var deadline = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < deadline)
            {
                if (LatestSequence() is { } after && (before is null || after > before))
                    return new(true, "System restore point created");
                Thread.Sleep(2000);
            }
            return new(false, "Windows accepted the request but no restore point appeared.");
        }
        catch (ManagementException ex)
        {
            return new(false, "Couldn't create a restore point: " + ex.Message);
        }
        finally
        {
            if (old is null) key.DeleteValue(Frequency, throwOnMissingValue: false);
            else key.SetValue(Frequency, old, RegistryValueKind.DWord);
        }
    }

    static string Explain(uint rv) => rv switch
    {
        1058 => "Couldn't create a restore point: System Protection is turned off for this drive.",
        _ => $"Couldn't create a restore point (error {rv}).",
    };

    static uint? LatestSequence()
    {
        using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT SequenceNumber FROM SystemRestore");
        uint? max = null;
        foreach (ManagementObject o in searcher.Get())
            using (o)
            {
                uint n = Convert.ToUInt32(o["SequenceNumber"]);
                if (max is null || n > max) max = n;
            }
        return max;
    }
}
