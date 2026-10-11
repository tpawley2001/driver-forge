namespace DriverForge.Core;

// The engine's data model (docs/SPEC.md §2–§4).

public sealed record OsInfo(string Architecture, int Major, int Minor, int Build, int ProductType, int SuiteMask)
{
    public override string ToString() => $"Windows {Major}.{Minor} build {Build} ({Architecture})";
}

/// <summary>A device as Windows reports it (SPEC §3.1). ID lists keep Windows' order.</summary>
public sealed record Device(
    string InstanceId,
    string Name,
    string Description,
    string Manufacturer,
    string Class,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    int ProblemCode,
    InstalledDriver? Installed,
    Guid ClassGuid = default,
    bool IsPresent = true,
    DateTime? LastArrival = null,
    DateTime? LastRemoval = null)
{
    // CM_PROB_NOT_CONFIGURED, CM_PROB_REINSTALL, CM_PROB_FAILED_INSTALL
    static readonly int[] NoDriverProblems = [1, 18, 28];

    /// <summary>
    /// The device has no working driver (SPEC §3.1). A device with no driver
    /// and no problem code (the device tree root, raw and software devices)
    /// works as it is, so it doesn't count.
    /// </summary>
    public bool NeedsDriver => NoDriverProblems.Contains(ProblemCode) || (Installed is null && ProblemCode != 0);

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name
        : !string.IsNullOrWhiteSpace(Description) ? Description
        : InstanceId;

    public string PrimaryHardwareId => HardwareIds.Count > 0 ? HardwareIds[0] : InstanceId;
}

/// <summary>The driver a device has now, from its DEVPKEY_Device_Driver* properties.</summary>
public sealed record InstalledDriver(
    string Description,
    string Provider,
    DriverVersion Version,
    DateOnly? Date,
    string Inf,
    string MatchingId,
    uint? Rank,
    string InfSection = "",
    DateTime? InstalledAt = null);

public enum MatchKind { HardwareId, CompatibleId }

/// <summary>A driver some source can provide for a device (SPEC §4).</summary>
public sealed record DriverOffer(
    string Id,
    string Source,
    string Description,
    string Provider,
    DriverVersion Version,
    DateOnly? Date,
    string MatchedId,
    MatchKind Match,
    uint Rank,
    bool Signed,
    bool IsLocal,
    long? DownloadBytes = null,
    string? Inf = null,
    string? InfSection = null,
    bool Confirmed = true);

public sealed record ScanResult(
    DateTime At,
    OsInfo Os,
    IReadOnlyList<Device> Devices,
    IReadOnlyDictionary<string, IReadOnlyList<DriverOffer>> Offers,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<DriverOffer> OffersFor(Device d) =>
        Offers.TryGetValue(d.InstanceId, out var o) ? o : [];
}

public sealed record InstallOptions(bool TestMode);

public enum InstallStep { Downloading, Extracting, BackingUp, Installing, Verifying }

public sealed record InstallResult(bool Ok, bool NeedsRestart, int ErrorCode, string Message, bool Simulated = false);

/// <summary>Somewhere drivers come from (SPEC §4).</summary>
public interface IDriverSource
{
    string Name { get; }
    Task<IReadOnlyList<SourcedOffer>> FindAsync(IReadOnlyList<Device> devices, OsInfo os, IProgress<string>? progress, CancellationToken ct);
    /// <summary>Make the offer's driver package available locally; returns the INF path.</summary>
    Task<string> AcquireAsync(DriverOffer offer, IProgress<InstallStep>? progress, CancellationToken ct);
}

/// <summary>An offer a source found, with the device it is for.</summary>
public sealed record SourcedOffer(string InstanceId, DriverOffer Offer);

public interface IDriverEngine
{
    string Name { get; }
    Task<ScanResult> ScanAsync(IProgress<string>? progress, CancellationToken ct = default);
    Task<InstallResult> InstallAsync(Device device, DriverOffer offer, InstallOptions options,
        IProgress<InstallStep>? progress, CancellationToken ct = default);
    /// <summary>Past installs and rollbacks, newest first.</summary>
    IReadOnlyList<HistoryEntry> History();
    /// <summary>Puts back the driver a history entry replaced (SPEC §7).</summary>
    Task<InstallResult> RollbackAsync(HistoryEntry entry, InstallOptions options,
        IProgress<InstallStep>? progress, CancellationToken ct = default);
    /// <summary>Removes the device's third-party driver from Windows, backed up first (SPEC §8.1).</summary>
    Task<InstallResult> UninstallDriverAsync(Device device, InstallOptions options, CancellationToken ct = default);
    /// <summary>Devices Windows remembers that aren't connected (SPEC §8.2).</summary>
    Task<IReadOnlyList<Device>> GhostsAsync(CancellationToken ct = default);
    Task RemoveGhostAsync(Device ghost, CancellationToken ct = default);
    /// <summary>What this PC is (SPEC §9.1).</summary>
    Task<IReadOnlyList<InfoSection>> SystemInfoAsync(CancellationToken ct = default);
}

public sealed class EngineException(string message) : Exception(message);
