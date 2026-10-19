namespace DriverForge.Core.Apps;

/// <summary>One program, as the Apps page shows it (APPS.md §2).</summary>
public sealed record AppRow
{
    public required string Id { get; init; }                 // winget package ID
    public required string Name { get; init; }
    public string Publisher { get; init; } = "";
    public string? InstalledVersion { get; init; }
    public string? AvailableVersion { get; init; }
    public bool IsUpdateAvailable { get; init; }
    public string Source { get; init; } = "winget";
    // From the curated catalog (§4); null/false for anything else.
    public string? Category { get; init; }
    public bool Curated { get; init; }
    public bool SelfUpdating { get; init; }
    public bool MachineOnly { get; init; }
    public string? Verified { get; init; }
    // Licence terms the user has to accept before a silent install (§5.2).
    public IReadOnlyList<AppAgreement> Agreements { get; init; } = [];
    // Only a per-user installer exists, so an elevated install would land in
    // the elevating account's profile (§5.3).
    public bool UserScopeOnly { get; init; }

    public bool IsInstalled => InstalledVersion is not null;
}

public sealed record AppAgreement(string Label, string Text, string Url);

public enum AppAction { Install, Upgrade, Uninstall }

public enum AppOutcome
{
    Installed, Updated, Uninstalled, Skipped, UpToDate, Simulated,
    NeedsConsent, Blocked, DownloadFailed, Failed, Cancelled,
}

/// <summary>A program that appeared during an install and isn't the app asked for (§6).</summary>
public sealed record InstalledProgram(string Key, string Name, string Publisher, string Version,
                                      string? UninstallCommand, string? QuietUninstallCommand);

public sealed record AppResult
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AppAction Action { get; init; }
    public required AppOutcome Outcome { get; init; }
    public string? FromVersion { get; init; }
    public string? ToVersion { get; init; }
    public bool RebootRequired { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<InstalledProgram> Extras { get; init; } = [];

    /// <summary>Counts as done for the command line's exit code (§8).</summary>
    public bool Succeeded => Outcome is AppOutcome.Installed or AppOutcome.Updated or AppOutcome.Uninstalled
                                     or AppOutcome.Skipped or AppOutcome.UpToDate or AppOutcome.Simulated;
}

public sealed record AppStep(string Id, string State, double Percent);

public sealed record AppInstallOptions
{
    public bool AcceptAgreements { get; init; }
    public bool MachineScopeOnly { get; init; } = true;      // SystemOrUnknown; false = Any
    public bool TestMode { get; init; }
    public string? CorrelationId { get; init; }
}

public sealed record AppStatus(bool Available, string EngineVersion, string Message);

public interface IAppEngine
{
    Task<AppStatus> GetStatusAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AppRow>> ListCuratedAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AppRow>> SearchAsync(string text, CancellationToken ct = default);
    Task<IReadOnlyList<AppRow>> ListUpdatesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AppRow>> ListInstalledAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AppRow>> DescribeAsync(IReadOnlyList<AppRow> rows);
    /// <summary>The row for exactly this winget ID, or null.</summary>
    Task<AppRow?> FindAsync(string id);
    Task<AppResult> InstallAsync(AppRow app, AppInstallOptions options, IProgress<AppStep>? progress = null, CancellationToken ct = default);
    Task<AppResult> UpgradeAsync(AppRow app, AppInstallOptions options, IProgress<AppStep>? progress = null, CancellationToken ct = default);
    Task<AppResult> UninstallAsync(AppRow app, IProgress<AppStep>? progress = null, CancellationToken ct = default);
}
