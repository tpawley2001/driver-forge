namespace DriverForge.Core;

/// <summary>
/// The real engine (SPEC §3–§7): scans devices, asks every source in
/// parallel, installs. A failing source never fails the scan; it adds a warning.
/// </summary>
public sealed class WindowsEngine(string dataDirectory) : IDriverEngine
{
    public string Name => "Windows engine";
    public string DataDirectory { get; } = dataDirectory;

    /// <summary>In priority order: when two sources offer the same driver, the first one's offer is kept.</summary>
    public IReadOnlyList<IDriverSource> Sources { get; } =
        [
            new WindowsUpdateSource(),
            new DriverStoreSource(),
            new CatalogSource(Path.Combine(dataDirectory, "cache", "catalog")) { Enabled = () => LoadSettings(dataDirectory).UseCatalog },
            new FolderSource(() => LoadSettings(dataDirectory).DriverFolders, Path.Combine(dataDirectory, "cache", "folders")),
        ];

    /// <summary>Read at scan time, so a change in the app applies to the next scan.</summary>
    static AppSettings LoadSettings(string dataDirectory) => AppSettings.Load(Path.Combine(dataDirectory, "settings.json"));

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
            offers.ToDictionary(kv => kv.Key, kv => Dedupe(kv.Value), StringComparer.OrdinalIgnoreCase),
            warnings);
    }

    /// <summary>The same driver from several sources is listed once, from the highest-priority source.</summary>
    internal IReadOnlyList<DriverOffer> Dedupe(IEnumerable<DriverOffer> offers)
    {
        int Priority(DriverOffer o) => Sources.Select((s, i) => (s, i)).FirstOrDefault(x => x.s.Name == o.Source).i;
        return offers.OrderBy(Priority)
            .GroupBy(o => o.Version.IsKnown && o.Date is not null ? $"{o.Version}|{o.Date}|{o.Provider.ToUpperInvariant()}" : o.Id)
            .Select(g => g.First())
            .ToList();
    }

    InstallHistory HistoryLog => new(Path.Combine(DataDirectory, "history.jsonl"));
    string BackupsDirectory => Path.Combine(DataDirectory, "backups");

    public IReadOnlyList<HistoryEntry> History() => HistoryLog.Load();

    /// <summary>SPEC §6.1: acquire, back up, install, verify, record.</summary>
    public async Task<InstallResult> InstallAsync(Device device, DriverOffer offer, InstallOptions options,
        IProgress<InstallStep>? progress, CancellationToken ct = default)
    {
        var source = Sources.FirstOrDefault(s => s.Name == offer.Source)
            ?? throw new EngineException($"No source named {offer.Source}.");
        if (source is IInstallingSource own)
            return await RecordAsync(device, offer.Source, DriverRef.From(offer), options, progress, async backup =>
            {
                var r = await own.InstallAsync(offer, options, progress, ct);
                if (!r.Ok || r.Simulated) return r;
                progress?.Report(InstallStep.Verifying);
                return Verify(device.InstanceId, r, i => DatesClose(i.Date, offer.Date), offer.Description);
            });

        if (!offer.IsLocal) progress?.Report(InstallStep.Downloading);
        string inf;
        try { inf = await source.AcquireAsync(offer, progress, ct); }
        catch (EngineException ex) { return Fail(device, offer.Source, DriverRef.From(offer), options, ex.Message); }
        // A package folder: Windows picks the INF and driver node for this device (InfMatcher).
        Func<DriverNode, bool> pick = n => Matches(n, offer.InfSection, offer.Version, offer.Description);
        if (Directory.Exists(inf))
        {
            if (offer.Confirmed && offer.Inf is { } known && File.Exists(known)) inf = known;
            else
            {
                var node = await Task.Run(() => InfMatcher.Best(device, InfMatcher.InfsIn(inf)), ct);
                if (node is null)
                    return Fail(device, offer.Source, DriverRef.From(offer), options, "Windows says this package has no driver for this device.");
                inf = node.Inf;
                pick = n => Matches(n, node.Section, node.Version, node.Description);
            }
        }
        return await InstallPackageAsync(device, inf, pick,
            offer.Source, DriverRef.From(offer) with { Inf = Path.GetFileName(inf) }, options, progress, ct);
    }

    public async Task<InstallResult> RollbackAsync(HistoryEntry entry, InstallOptions options,
        IProgress<InstallStep>? progress, CancellationToken ct = default)
    {
        const string label = "Rollback";
        var device = await Task.Run(() => DeviceScanner.ReadOne(entry.InstanceId), ct);
        if (device is null || entry.From is not { } from || !entry.CanRollBack)
            return new(false, false, 0, device is null ? "The device isn't connected." : "There's nothing to roll back to.");
        string? inf = DriverBackup.IsThirdParty(from.Inf)
            ? DriverBackup.FindInf(entry.BackupFolder!, from.Inf)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", from.Inf);
        if (inf is null || !File.Exists(inf))
            return Fail(device, label, from, options, "The backup of the old driver is missing.");
        return await InstallPackageAsync(device, inf, n => Matches(n, from.Section, DriverVersion.Parse(from.Version), from.Description),
            label, from, options, progress, ct);
    }

    public async Task<InstallResult> UninstallDriverAsync(Device device, InstallOptions options, CancellationToken ct = default)
    {
        if (device.Installed is not { } cur || !DriverBackup.IsThirdParty(cur.Inf))
            return new(false, false, 0, "Only drivers that didn't come with Windows can be uninstalled.");
        return await RecordAsync(device, "Uninstall", DriverRef.Removed, options, null, _ => Task.Run(() =>
        {
            if (options.TestMode)
                return new InstallResult(true, false, 0, $"Would remove {cur.Inf} (test mode)", Simulated: true);
            bool reboot = DriverRemoval.Delete(cur.Inf);
            var now = DeviceScanner.ReadOne(device.InstanceId);
            if (!reboot && now?.Installed is { } i && string.Equals(i.Inf, cur.Inf, StringComparison.OrdinalIgnoreCase))
                return new InstallResult(false, false, 0, $"Windows still uses {cur.Inf} for this device.");
            string message = now?.Installed is { } next ? $"Removed; the device now uses {next.Description} ({next.Inf})" : "Removed";
            return new InstallResult(true, reboot, 0, reboot ? message + "; restart to finish" : message);
        }, ct));
    }

    public Task<IReadOnlyList<Device>> GhostsAsync(CancellationToken ct = default) =>
        Task.Run(() => DeviceScanner.Ghosts(ct), ct);

    public Task RemoveGhostAsync(Device ghost, CancellationToken ct = default) =>
        Task.Run(() => Core.Ghosts.Remove(ghost.InstanceId), ct);

    /// <summary>Back up, stage, install one node on one device, verify (SPEC §6.1 steps 2–4).</summary>
    Task<InstallResult> InstallPackageAsync(Device device, string inf, Func<DriverNode, bool> pick, string source, DriverRef target,
        InstallOptions options, IProgress<InstallStep>? progress, CancellationToken ct) =>
        RecordAsync(device, source, target, options, progress, _ => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(InstallStep.Installing);
            if (options.TestMode)
            {
                // Everything but the install (SPEC §6.3): still prove the package has this driver for the device.
                var node = DriverNodes.FromInf(device.InstanceId, inf).FirstOrDefault(pick);
                return node is null
                    ? new InstallResult(false, false, 0, $"{Path.GetFileName(inf)} doesn't contain the chosen driver for this device.")
                    : new InstallResult(true, false, 0, $"Would install {node.Description} {node.Version} from {Path.GetFileName(inf)} (test mode)", Simulated: true);
            }
            string storeInf = DriverInstaller.Stage(inf);
            bool reboot = DriverInstaller.InstallOnDevice(device.InstanceId, storeInf, pick);
            progress?.Report(InstallStep.Verifying);
            var r = new InstallResult(true, reboot, 0, reboot ? "Installed; restart to finish" : "Installed");
            return Verify(device.InstanceId, r, i => string.Equals(i.Inf, Path.GetFileName(storeInf), StringComparison.OrdinalIgnoreCase), target.Description);
        }, ct));

    /// <summary>Backs up the current driver, runs the install, and writes the history entry whatever happens.</summary>
    async Task<InstallResult> RecordAsync(Device device, string source, DriverRef target, InstallOptions options,
        IProgress<InstallStep>? progress, Func<string?, Task<InstallResult>> install)
    {
        var at = DateTime.Now;
        string? backup = null;
        InstallResult result;
        try
        {
            if (device.Installed is { } cur && DriverBackup.IsThirdParty(cur.Inf))
            {
                progress?.Report(InstallStep.BackingUp);
                backup = await Task.Run(() => DriverBackup.Export(cur.Inf, BackupsDirectory, at));
            }
            result = await install(backup);
        }
        catch (EngineException ex) { result = new(false, false, 0, ex.Message); }

        var after = result.Ok && !result.Simulated ? DeviceScanner.ReadOne(device.InstanceId)?.Installed : null;
        HistoryLog.Append(new HistoryEntry(at, device.InstanceId, device.DisplayName,
            device.Installed is { } before ? DriverRef.From(before) : null,
            after is not null ? DriverRef.From(after) : target,
            source, result.Ok, result.Simulated, result.NeedsRestart, result.Message, backup));
        return result;
    }

    InstallResult Fail(Device device, string source, DriverRef target, InstallOptions options, string message)
    {
        HistoryLog.Append(new HistoryEntry(DateTime.Now, device.InstanceId, device.DisplayName,
            device.Installed is { } before ? DriverRef.From(before) : null, target, source, false, options.TestMode, false, message, null));
        return new(false, false, 0, message);
    }

    /// <summary>
    /// SPEC §6.1 step 4: the device must now use the new driver and work, unless
    /// Windows asked for a restart (then the switch happens at boot).
    /// </summary>
    static InstallResult Verify(string instanceId, InstallResult r, Func<InstalledDriver, bool> isNew, string description)
    {
        if (r.NeedsRestart) return r;
        var now = DeviceScanner.ReadOne(instanceId);
        if (now is null) return r with { Message = r.Message + " (the device disappeared; it may come back after a restart)" };
        if (now.Installed is not { } i || !isNew(i))
            return new(false, false, 0, $"Windows reported success, but the device still uses {(now.Installed is { } o ? $"{o.Description} {o.Version} ({o.Inf})" : "no driver")}.");
        if (now.ProblemCode != 0)
            return new(false, false, 0, $"{description} is installed, but the device reports problem code {now.ProblemCode}.");
        return r;
    }

    internal static bool Matches(DriverNode n, string? section, DriverVersion version, string description)
    {
        if (!string.IsNullOrEmpty(section))
            return SameSection(n.Section, section) && (!version.IsKnown || n.Version.CompareTo(version) == 0);
        return string.Equals(n.Description, description, StringComparison.OrdinalIgnoreCase) && (!version.IsKnown || n.Version.CompareTo(version) == 0);
    }

    /// <summary>
    /// A driver node names its models install section ("ICCWDT"); a device's
    /// DriverInfSection property names the decorated one Windows ran
    /// ("ICCWDT.NT", "Inst.NTamd64"). Same section either way.
    /// </summary>
    internal static bool SameSection(string a, string b) =>
        string.Equals(SectionBase(a), SectionBase(b), StringComparison.OrdinalIgnoreCase);

    static readonly System.Text.RegularExpressions.Regex PlatformSuffix =
        new(@"\.nt(x86|amd64|arm64|arm|ia64)?(\..*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static string SectionBase(string section) => PlatformSuffix.Replace(section, "");

    /// <summary>WU's driver date is a local midnight, SetupAPI's a UTC one: allow a day either way.</summary>
    internal static bool DatesClose(DateOnly? a, DateOnly? b) =>
        a is null || b is null || Math.Abs(a.Value.DayNumber - b.Value.DayNumber) <= 1;
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
                new("Intel(R) Ethernet Connection I219-LM", "Intel", DriverVersion.Parse("12.18.9.7"), new(2019, 5, 2), "oem12.inf", @"PCI\VEN_8086&DEV_15B7&SUBSYS_06DE1028", 0x00FF0001)),
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
        history.Add(new(DateTime.Now, device.InstanceId, device.DisplayName,
            device.Installed is { } i ? DriverRef.From(i) : null, DriverRef.From(offer), offer.Source, true, true, false, "Simulated (demo data)", null));
        return new(true, false, 0, "Simulated (demo data)", Simulated: true);
    }

    readonly List<HistoryEntry> history = [];

    public Task<InstallResult> UninstallDriverAsync(Device device, InstallOptions options, CancellationToken ct = default) =>
        Task.FromResult(new InstallResult(true, false, 0, "Simulated (demo data)", Simulated: true));

    public Task<IReadOnlyList<Device>> GhostsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Device>>(
        [
            new(@"USB\VID_534D&PID_2109&MI_00\6&EDA53C3&0&0000", "USB Video", "USB Video", "", "Camera", [@"USB\VID_534D&PID_2109&MI_00"], [], 0, null,
                IsPresent: false, LastRemoval: new DateTime(2025, 11, 2, 18, 4, 0)),
            new(@"STORAGE\VOLUME\{ECD3589C-F340-11ED-BFD7-B88A606D4B19}#0000000000100000", "Volume", "Volume", "", "Volume", [@"STORAGE\Volume"], [], 0, null,
                IsPresent: false, LastRemoval: new DateTime(2026, 3, 9, 9, 12, 0)),
        ]);

    public Task RemoveGhostAsync(Device ghost, CancellationToken ct = default) => Task.Delay(200, ct);

    public IReadOnlyList<HistoryEntry> History() => Enumerable.Reverse(history).ToList();

    public async Task<InstallResult> RollbackAsync(HistoryEntry entry, InstallOptions options,
        IProgress<InstallStep>? progress, CancellationToken ct = default)
    {
        progress?.Report(InstallStep.Installing);
        await Task.Delay(400, ct);
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
