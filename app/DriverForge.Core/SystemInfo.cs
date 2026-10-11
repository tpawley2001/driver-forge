using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DriverForge.Core;

public sealed record InfoRow(string Label, string Value);

public sealed record InfoSection(string Title, IReadOnlyList<InfoRow> Rows);

/// <summary>
/// What this PC is (SPEC §9.1), from documented WMI classes and Windows APIs.
/// Each section is read on its own: one that fails reads "Not available".
/// </summary>
public static class SystemInfo
{
    public const string NotAvailable = "Not available";

    public static IReadOnlyList<InfoSection> Read(CancellationToken ct = default)
    {
        var sections = new List<InfoSection>();
        foreach (var (title, read) in new (string, Func<IReadOnlyList<InfoRow>>)[]
                 {
                     ("Computer", Computer), ("Firmware", Firmware), ("Windows", Windows), ("Processor and memory", ProcessorAndMemory),
                     ("Graphics", Graphics), ("Disks", Disks), ("Battery", Battery),
                 })
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<InfoRow> rows;
            try { rows = read(); }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException
                                           or InvalidCastException or FormatException or ArgumentOutOfRangeException or OverflowException)
            {
                rows = [new(title, NotAvailable)];
            }
            // A desktop has no battery: leave the section out rather than show an empty one.
            if (rows.Count > 0 || title != "Battery") sections.Add(new(title, rows.Count > 0 ? rows : [new(title, NotAvailable)]));
        }
        return sections;
    }

    /// <summary>The page as plain text, for pasting into a forum post or a message.</summary>
    public static string ToText(IReadOnlyList<InfoSection> sections)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in sections)
        {
            sb.AppendLine(s.Title);
            foreach (var r in s.Rows) sb.AppendLine($"  {r.Label}: {r.Value}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    static IReadOnlyList<InfoRow> Computer()
    {
        var cs = First("Win32_ComputerSystem");
        var product = First("Win32_ComputerSystemProduct");
        var board = First("Win32_BaseBoard");
        string maker = Text(cs, "Manufacturer");
        string model = ModelName(maker, Text(cs, "Model"), Text(product, "Version"));
        return
        [
            new("Model", WithMaker(Tidy(maker), model)),
            new("Motherboard", Join(" ", Tidy(Text(board, "Manufacturer")), Text(board, "Product"), Version(Text(board, "Version")))),
        ];
    }

    /// <summary>
    /// Some makers put a machine-type code in Model and the name people know
    /// in the product's Version ("20XW…" vs "ThinkPad X1 Carbon Gen 9").
    /// </summary>
    internal static string ModelName(string maker, string model, string productVersion)
    {
        bool versionIsName = productVersion.Any(char.IsLetter) && productVersion.Contains(' ')
            && !productVersion.Contains("System Version", StringComparison.OrdinalIgnoreCase)
            && !productVersion.Contains("To be filled", StringComparison.OrdinalIgnoreCase);
        return versionIsName && !model.Contains(productVersion, StringComparison.OrdinalIgnoreCase)
            ? $"{productVersion} ({model})"
            : model;
    }

    static IReadOnlyList<InfoRow> Firmware()
    {
        var bios = First("Win32_BIOS");
        string date = Text(bios, "ReleaseDate") is { Length: > 0 } d ? $" ({ManagementDateTimeConverter.ToDateTime(d):d})" : "";
        string mode = FirmwareMode();
        if (mode == "UEFI") mode += SecureBoot() switch { true => ", Secure Boot on", false => ", Secure Boot off", null => "" };
        return
        [
            new("BIOS", Join(" ", Tidy(Text(bios, "Manufacturer")), Text(bios, "SMBIOSBIOSVersion")) + date),
            new("Mode", mode),
        ];
    }

    static IReadOnlyList<InfoRow> Windows()
    {
        var os = First("Win32_OperatingSystem");
        using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string release = cv?.GetValue("DisplayVersion") as string ?? "";
        string build = Text(os, "BuildNumber") + (cv?.GetValue("UBR") is int ubr ? $".{ubr}" : "");
        DateTime? Date(string p) => Text(os, p) is { Length: > 0 } s ? ManagementDateTimeConverter.ToDateTime(s) : null;
        return
        [
            new("Edition", Join(" ", Text(os, "Caption").Replace("Microsoft ", ""), release) + $" · {Text(os, "OSArchitecture")}"),
            new("Build", build),
            new("Installed", Date("InstallDate")?.ToString("d") ?? NotAvailable),
            new("Last restart", Date("LastBootUpTime")?.ToString("g") ?? NotAvailable),
        ];
    }

    static IReadOnlyList<InfoRow> ProcessorAndMemory()
    {
        var rows = new List<InfoRow>();
        foreach (var cpu in All("Win32_Processor"))
            rows.Add(new("Processor", $"{Squash(Text(cpu, "Name"))} · {Num(cpu, "NumberOfCores")} cores, {Num(cpu, "NumberOfLogicalProcessors")} threads"));

        var sticks = All("Win32_PhysicalMemory");
        long total = sticks.Sum(m => (long)Num(m, "Capacity"));
        if (total == 0) total = (long)Num(First("Win32_ComputerSystem"), "TotalPhysicalMemory");
        long slots = All("Win32_PhysicalMemoryArray").Sum(a => (long)Num(a, "MemoryDevices"));
        var parts = new List<string> { Bytes(total) };
        if (sticks.Count > 0)
            parts.Add(slots >= sticks.Count ? $"{sticks.Count} of {slots} slots used" : $"{sticks.Count} modules");
        if (sticks.Select(m => MemoryType((int)Num(m, "SMBIOSMemoryType"))).FirstOrDefault(t => t.Length > 0) is { } type)
            parts.Add(type);
        if (sticks.Select(m => Num(m, "ConfiguredClockSpeed")).Where(s => s > 0).DefaultIfEmpty().Min() is var speed and > 0)
            parts.Add($"{speed} MT/s");
        rows.Add(new("Memory", string.Join(" · ", parts)));
        return rows;
    }

    /// <summary>SMBIOS memory device types (Win32_PhysicalMemory.SMBIOSMemoryType).</summary>
    internal static string MemoryType(int smbios) => smbios switch
    {
        20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3",
        30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5",
        _ => "",
    };

    static IReadOnlyList<InfoRow> Graphics()
    {
        var memory = AdapterMemory();
        return All("Win32_VideoController").Select(v =>
        {
            string name = Text(v, "Name");
            var parts = new List<string> { name };
            if (memory.TryGetValue(name, out long bytes) && bytes > 0) parts.Add(Bytes(bytes));
            if (Text(v, "DriverVersion") is { Length: > 0 } ver) parts.Add("driver " + ver);
            if (Num(v, "CurrentHorizontalResolution") is > 0 and var w)
                parts.Add($"{w}×{Num(v, "CurrentVerticalResolution")} at {Num(v, "CurrentRefreshRate")} Hz");
            return new InfoRow("Adapter", string.Join(" · ", parts));
        }).ToList();
    }

    /// <summary>
    /// Adapter memory by driver description: HardwareInformation.qwMemorySize
    /// in each display adapter's driver key (AdapterRAM stops at 4 GB).
    /// </summary>
    static Dictionary<string, long> AdapterMemory()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (cls is null) return result;
        foreach (var sub in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            try
            {
                using var key = cls.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is string desc && key.GetValue("HardwareInformation.qwMemorySize") is long bytes)
                    result.TryAdd(desc, bytes);
            }
            catch (System.Security.SecurityException) { }
        }
        return result;
    }

    static IReadOnlyList<InfoRow> Disks()
    {
        List<ManagementBaseObject> disks;
        try { disks = All("MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"); }
        catch (ManagementException) { disks = []; }
        if (disks.Count > 0)
            return disks.OrderBy(d => Text(d, "DeviceId").PadLeft(4, '0')).Select(d =>
            {
                var parts = new List<string> { Text(d, "FriendlyName").Trim(), Bytes((long)Num(d, "Size")) };
                string kind = DiskKind((int)Num(d, "MediaType"), (int)Num(d, "BusType"));
                if (kind.Length > 0) parts.Add(kind);
                parts.Add(Health((int)Num(d, "HealthStatus")));
                return new InfoRow("Disk", string.Join(" · ", parts));
            }).ToList();
        return All("Win32_DiskDrive").Select(d =>
            new InfoRow("Disk", $"{Text(d, "Model").Trim()} · {Bytes((long)Num(d, "Size"))} · {Text(d, "InterfaceType")}")).ToList();
    }

    /// <summary>MSFT_PhysicalDisk MediaType (3 HDD, 4 SSD, 5 SCM) and BusType.</summary>
    internal static string DiskKind(int mediaType, int busType)
    {
        string media = mediaType switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" };
        string bus = busType switch { 7 => "USB", 8 => "RAID", 11 => "SATA", 12 => "SD", 13 => "MMC", 17 => "NVMe", 3 => "ATA", 10 => "SAS", 1 => "SCSI", _ => "" };
        return bus == "NVMe" ? "NVMe SSD" : Join(" ", bus, media);
    }

    internal static string Health(int status) => status switch { 0 => "healthy", 1 => "warning", 2 => "unhealthy", _ => "health unknown" };

    static IReadOnlyList<InfoRow> Battery()
    {
        // Laptops only: a desktop has no BatteryStaticData instances, which makes the section empty.
        var design = All("BatteryStaticData", @"root\WMI");
        var full = All("BatteryFullChargedCapacity", @"root\WMI");
        var rows = new List<InfoRow>();
        for (int i = 0; i < design.Count; i++)
        {
            long designed = (long)Num(design[i], "DesignedCapacity");
            long now = i < full.Count ? (long)Num(full[i], "FullChargedCapacity") : 0;
            rows.Add(new("Battery", designed > 0 && now > 0
                ? $"Holds {BatteryShare(now, designed)}% of its design capacity ({now / 1000.0:0.#} of {designed / 1000.0:0.#} Wh)"
                : NotAvailable));
        }
        return rows;
    }

    internal static int BatteryShare(long fullCharge, long designed) =>
        (int)Math.Round(100.0 * fullCharge / designed);

    // ---- Firmware ------------------------------------------------------------------

    static string FirmwareMode() =>
        GetFirmwareType(out uint type) ? type switch { 1 => "Legacy BIOS", 2 => "UEFI", _ => NotAvailable } : NotAvailable;

    static bool? SecureBoot()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return key?.GetValue("UEFISecureBootEnabled") is int v ? v != 0 : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFirmwareType(out uint firmwareType);

    // ---- WMI helpers ---------------------------------------------------------------

    static List<ManagementBaseObject> All(string cls, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(scope, $"SELECT * FROM {cls}");
        try { return searcher.Get().Cast<ManagementBaseObject>().ToList(); }
        // Classes that need hardware (root\WMI batteries) report "not supported" when there is none.
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.NotSupported or ManagementStatus.InvalidClass) { return []; }
    }

    static ManagementBaseObject? First(string cls) => All(cls).FirstOrDefault();

    static string Text(ManagementBaseObject? o, string prop) =>
        o is null ? "" : (Prop(o, prop) ?? "").ToString()!.Trim();

    static ulong Num(ManagementBaseObject? o, string prop) =>
        o is not null && Prop(o, prop) is { } v ? Convert.ToUInt64(v) : 0;

    static object? Prop(ManagementBaseObject o, string prop)
    {
        try { return o[prop]; }
        catch (ManagementException) { return null; }
    }

    // ---- Formatting ----------------------------------------------------------------

    /// <summary>Binary units, the way Windows shows sizes (a "16 GB" stick is 16 GiB).</summary>
    internal static string Bytes(long bytes)
    {
        if (bytes <= 0) return NotAvailable;
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v >= 100 || u == 0 ? $"{v:0} {units[u]}" : $"{v:0.#} {units[u]}";
    }

    /// <summary>"8055 rev A00", or "8055 (KBC Version 05.39)" when the version is more than a revision; firmware placeholders dropped.</summary>
    internal static string Version(string v) =>
        v.Length == 0 || v.Contains("Default string", StringComparison.OrdinalIgnoreCase) || v.Contains("To be filled", StringComparison.OrdinalIgnoreCase)
            ? "" : v.Contains(' ') ? $"({v})" : "rev " + v;

    /// <summary>"HP" + "HP EliteDesk 800" is "HP EliteDesk 800", not "HP HP EliteDesk 800".</summary>
    internal static string WithMaker(string maker, string model) =>
        model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? model : Join(" ", maker, model);

    /// <summary>Drops company suffixes ("Dell Inc." → "Dell", "ASUSTeK COMPUTER INC." → "ASUSTeK").</summary>
    internal static string Tidy(string maker)
    {
        string m = maker.Trim();
        foreach (var suffix in new[] { " Inc.", " INC.", " Inc", " Corporation", " CORPORATION", " Corp.", " Co., Ltd.", " CO., LTD.", " Ltd.", " LTD.", " COMPUTER", " Computer" })
            if (m.EndsWith(suffix, StringComparison.Ordinal)) m = m[..^suffix.Length].TrimEnd(',', ' ');
        return m == "LENOVO" ? "Lenovo" : m;
    }

    static string Squash(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    static string Join(string sep, params string[] parts) => string.Join(sep, parts.Where(p => p.Length > 0));
}
