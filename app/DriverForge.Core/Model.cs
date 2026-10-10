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
    InstalledDriver? Installed)
{
    // CM_PROB_NOT_CONFIGURED, CM_PROB_REINSTALL, CM_PROB_FAILED_INSTALL
    static readonly int[] NoDriverProblems = [1, 18, 28];

    /// <summary>The device has no working driver (SPEC §3.1).</summary>
    public bool NeedsDriver => Installed is null || NoDriverProblems.Contains(ProblemCode);

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
    uint? Rank);

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
    string? Inf = null);

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

public interface IDriverEngine
{
    string Name { get; }
    Task<ScanResult> ScanAsync(IProgress<string>? progress, CancellationToken ct = default);
    Task<InstallResult> InstallAsync(Device device, DriverOffer offer, InstallOptions options,
        IProgress<InstallStep>? progress, CancellationToken ct = default);
}

public sealed class EngineException(string message) : Exception(message);
