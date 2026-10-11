namespace DriverForge.Core;

/// <summary>
/// The real engine (SPEC §3–§7): scans devices, asks every source in
/// parallel, installs. A failing source never fails the scan; it adds a warning.
/// </summary>
public sealed class WindowsEngine(string dataDirectory) : IDriverEngine
{
    public string Name => "Windows engine";
    public string DataDirectory { get; } = dataDirectory;

    public IReadOnlyList<IDriverSource> Sources { get; } = [new WindowsUpdateSource(), new DriverStoreSource()];

    public async Task<ScanResult> ScanAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        progress?.Report("Reading devices…");
        var (os, devices) = await Task.Run(() => (DeviceScanner.Os(), DeviceScanner.Scan(ct: ct)), ct);

        var warnings = new List<string>();
        var runs = Sources.Select(async s =>
        {
            try { return (s, offers: await s.FindAsync(devices, os, progress, ct), error: (string?)null); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (s, offers: (IReadOnlyList<SourcedOffer>)[], error: ex.Message);
            }
        }).ToList();
        var offers = new Dictionary<string, List<DriverOffer>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, found, error) in await Task.WhenAll(runs))
        {
            if (error is not null) warnings.Add($"{source.Name}: {error}");
            foreach (var o in found)
            {
                if (!offers.TryGetValue(o.InstanceId, out var list)) offers[o.InstanceId] = list = [];
                list.Add(o.Offer);
            }
        }
        return new ScanResult(DateTime.Now, os, devices,
            offers.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<DriverOffer>)kv.Value, StringComparer.OrdinalIgnoreCase),
            warnings);
    }

    public Task<InstallResult> InstallAsync(Device device, DriverOffer offer, InstallOptions options,
        IProgress<InstallStep>? progress, CancellationToken ct = default)
    {
        var source = Sources.FirstOrDefault(s => s.Name == offer.Source)
            ?? throw new EngineException($"No source named {offer.Source}.");
        if (source is IInstallingSource own) return own.InstallAsync(offer, options, progress, ct);
        throw new EngineException("Installing from INF packages isn't built yet (milestone 4 in docs/SPEC.md).");
    }
}

/// <summary>Made-up devices and offers for working on the UI. Installs are always simulated.</summary>
public sealed class DemoEngine : IDriverEngine
{
    public string Name => "Demo data";

    static readonly OsInfo Os = new("amd64", 10, 0, 26100, 1, 0x100);

    public async Task<ScanResult> ScanAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        progress?.Report("Reading devices…");
        await Task.Delay(300, ct);
        var devices = new List<Device>
        {
            Dev(@"PCI\VEN_8086&DEV_A123&SUBSYS_00000000&REV_31\3&11583659&0&FC", "SM Bus Controller", "System", null, 28),
            Dev(@"PCI\VEN_8086&DEV_15B7&SUBSYS_06DE1028&REV_31\3&11583659&0&FE", "Ethernet Connection I219-LM", "Net",
                new("Intel(R) Ethernet Connection I219-LM", "Intel", DriverVersion.Parse("12.18.9.7"), new(2019, 5, 2), "oem12.inf", @"PCI\VEN_8086&DEV_15B7&SUBSYS_06DE1028", 0x00FF0000)),
            Dev(@"PCI\VEN_8086&DEV_A145&SUBSYS_06DE1028&REV_31\3&11583659&0&F8", "PCI standard ISA bridge", "System",
                new("PCI standard ISA bridge", "Microsoft", DriverVersion.Parse("10.0.26100.1"), new(2006, 6, 21), "machine.inf", @"PCI\CC_0601", 0x00FF2001)),
            Dev(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0255&SUBSYS_102806DE&REV_1000\4&2BE6AF0E&0&0001", "Realtek Audio", "MEDIA",
                new("Realtek High Definition Audio", "Realtek", DriverVersion.Parse("6.0.9500.1"), new(2023, 3, 1), "oem20.inf", @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0255&SUBSYS_102806DE", 0x00FF0000)),
            Dev(@"PCI\VEN_10DE&DEV_2489&SUBSYS_38811462&REV_A1\4&1A2B3C4D&0&0008", "NVIDIA GeForce RTX 3060 Ti", "Display",
                new("NVIDIA GeForce RTX 3060 Ti", "NVIDIA", DriverVersion.Parse("32.0.15.6094"), new(2024, 8, 14), "oem33.inf", @"PCI\VEN_10DE&DEV_2489", 0x00FF0001)),
            Dev(@"USB\VID_046D&PID_C52B\5&3A1B2C3D&0&2", "Logitech USB Receiver", "USB",
                new("USB Composite Device", "Microsoft", DriverVersion.Parse("10.0.26100.1"), new(2006, 6, 21), "usb.inf", @"USB\COMPOSITE", 0x00FF2003)),
            Dev(@"ACPI\VEN_INT&DEV_33A0\0", "Unknown device", "Other", null, 28),
        };
        progress?.Report("Asking driver sources…");
        await Task.Delay(500, ct);
        var offers = new Dictionary<string, IReadOnlyList<DriverOffer>>
        {
            [devices[0].InstanceId] =
            [
                Offer("wu-smbus", "Windows Update", "Intel(R) SMBus - A123", "Intel", "10.1.1.45", new(2020, 9, 9), devices[0], 0x00FF0000),
            ],
            [devices[1].InstanceId] =
            [
                // Closer ID match but older than the installed driver: must not be an update.
                Offer("cat-i219-old", "Update Catalog", "Intel(R) Ethernet Connection I219-LM", "Intel", "12.15.22.6", new(2017, 9, 1), devices[1], 0x00FF0000),
            ],
            [devices[2].InstanceId] =
            [
                Offer("wu-lpc", "Windows Update", "Intel(R) 100 Series LPC Controller - A145", "INTEL", "10.1.1.45", new(2020, 9, 9), devices[2], 0x00FF0000),
            ],
            [devices[3].InstanceId] =
            [
                Offer("wu-realtek", "Windows Update", "Realtek High Definition Audio", "Realtek", "6.0.9600.1", new(2024, 2, 7), devices[3], 0x00FF0000),
                Offer("cat-realtek", "Update Catalog", "Realtek High Definition Audio", "Realtek", "6.0.9500.1", new(2023, 3, 1), devices[3], 0x00FF0000),
            ],
            [devices[4].InstanceId] =
            [
                Offer("wu-nv", "Windows Update", "NVIDIA GeForce RTX 3060 Ti", "NVIDIA", "32.0.15.6614", new(2024, 12, 2), devices[4], 0x00FF0000),
            ],
        };
        return new(DateTime.Now, Os, devices, offers, ["Demo data: nothing here is from this PC."]);
    }

    public async Task<InstallResult> InstallAsync(Device device, DriverOffer offer, InstallOptions options,
        IProgress<InstallStep>? progress, CancellationToken ct = default)
    {
        foreach (var step in new[] { InstallStep.Downloading, InstallStep.Extracting, InstallStep.BackingUp, InstallStep.Installing, InstallStep.Verifying })
        {
            if (step == InstallStep.Downloading && offer.IsLocal) continue;
            progress?.Report(step);
            await Task.Delay(400, ct);
        }
        return new(true, false, 0, "Simulated (demo data)", Simulated: true);
    }

    static Device Dev(string id, string name, string cls, InstalledDriver? installed, int problem = 0)
    {
        string hw = id[..id.LastIndexOf('\\')];
        var hwids = new List<string> { hw };
        int amp = hw.LastIndexOf('&');
        if (amp > 0) hwids.Add(hw[..amp]);
        return new(id, name, name, "", cls, hwids, [], problem, installed);
    }

    static DriverOffer Offer(string id, string source, string desc, string provider, string version, DateOnly date, Device d, uint rank) =>
        new(id, source, desc, provider, DriverVersion.Parse(version), date, d.HardwareIds[^1], MatchKind.HardwareId, rank,
            Signed: true, IsLocal: false, DownloadBytes: 2_400_000);
}
