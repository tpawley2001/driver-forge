using System.Text.RegularExpressions;
using DriverForge.Core;
using DriverForge.Core.Apps;
using Microsoft.Management.Deployment;
using Xunit;

namespace DriverForge.Tests;

public class CuratedCatalogTests
{
    [Fact]
    public void Embedded_catalog_has_no_problems() => Assert.Empty(CuratedCatalog.Load().Problems());

    [Fact]
    public void Only_verified_apps_are_offered()
    {
        var c = Parse("""{ "version": 1, "categories": ["Media"], "apps": [ { "id": "A.B", "category": "Media", "verified": "2026-10-20" }, { "id": "C.D", "category": "Media" } ] }""");
        Assert.Equal(["A.B"], c.Offered.Select(a => a.Id));
    }

    [Theory]
    [InlineData("""{ "version": 1, "categories": ["Media"], "apps": [ { "id": "NoDot", "category": "Media" } ] }""", "not a winget package ID")]
    [InlineData("""{ "version": 1, "categories": ["Media"], "apps": [ { "id": "A.B", "category": "Media" }, { "id": "a.b", "category": "Media" } ] }""", "listed twice")]
    [InlineData("""{ "version": 1, "categories": ["Media"], "apps": [ { "id": "A.B", "category": "Games" } ] }""", "unknown category")]
    [InlineData("""{ "version": 1, "categories": ["Media"], "apps": [ { "id": "A.B", "category": "Media", "verified": "20 Oct" } ] }""", "yyyy-MM-dd")]
    [InlineData("""{ "version": 1, "categories": ["Media", "Empty"], "apps": [ { "id": "A.B", "category": "Media" } ] }""", "has no apps")]
    public void Problems_are_reported(string json, string expected) =>
        Assert.Contains(Parse(json).Problems(), p => p.Contains(expected));

    static CuratedCatalog Parse(string json) => CuratedCatalog.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
}

public class AppListTests
{
    [Fact]
    public void Round_trips_in_winget_export_format()
    {
        string json = AppList.Write(["7zip.7zip", "VideoLAN.VLC", "7ZIP.7zip"], "1.29.380");
        Assert.Contains(AppList.Schema, json);
        Assert.Equal(["7zip.7zip", "VideoLAN.VLC"], AppList.Read(json));
    }

    [Fact]
    public void Reads_what_winget_export_writes_and_skips_other_sources()
    {
        const string json = """
        {
          "$schema" : "https://aka.ms/winget-packages.schema.2.0.json",
          "CreationDate" : "2026-10-18T21:00:00.000-00:00",
          "Sources" : [
            { "Packages" : [ { "PackageIdentifier" : "Git.Git" }, { "PackageIdentifier" : "Notepad++.Notepad++" } ],
              "SourceDetails" : { "Argument" : "https://cdn.winget.microsoft.com/cache", "Identifier" : "Microsoft.Winget.Source_8wekyb3d8bbwe", "Name" : "winget", "Type" : "Microsoft.PreIndexed.Package" } },
            { "Packages" : [ { "PackageIdentifier" : "9NBLGGH4NNS1" } ],
              "SourceDetails" : { "Argument" : "https://storeedgefd.dsx.mp.microsoft.com/v9.0", "Identifier" : "StoreEdgeFD", "Name" : "msstore", "Type" : "Microsoft.Rest" } }
          ],
          "WinGetVersion" : "1.29.380"
        }
        """;
        Assert.Equal(["Git.Git", "Notepad++.Notepad++"], AppList.Read(json));
    }

    [Fact]
    public void Rejects_a_file_that_is_not_a_list() => Assert.Throws<InvalidDataException>(() => AppList.Read("""{ "apps": [] }"""));
}

public class BundledExtrasTests
{
    static readonly IReadOnlyList<Regex> Runtimes = ProgramsSnapshot.Runtimes();

    static InstalledProgram P(string key, string name, string publisher = "") => new(key, name, publisher, "1.0", null, null);

    [Fact]
    public void Finds_new_entries_only()
    {
        var before = new Dictionary<string, InstalledProgram> { ["HKLM\\a"] = P("HKLM\\a", "Old") };
        var after = new Dictionary<string, InstalledProgram>(before) { ["HKLM\\b"] = P("HKLM\\b", "New") };
        Assert.Equal(["New"], ProgramsSnapshot.Added(before, after).Select(p => p.Name));
    }

    [Fact]
    public void The_requested_app_and_runtimes_are_not_extras()
    {
        var added = new[]
        {
            P("HKLM\\{23170F69-40C1-2702-2600-000001000000}", "Something else entirely"),   // by product code
            P("HKLM\\7-Zip", "7-Zip 26.04 (x64)"),                                           // by name
            P("HKLM\\helper", "Igor Pavlov Shell Helper", "Igor Pavlov"),                   // by publisher
            P("HKLM\\vc", "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211"),
            P("HKLM\\wv2", "Microsoft Edge WebView2 Runtime"),
            P("HKLM\\bar", "Super Toolbar", "AdCorp"),
        };
        var extras = ProgramsSnapshot.Extras(added, "7-Zip", "Igor Pavlov", ["{23170F69-40C1-2702-2600-000001000000}"], Runtimes);
        Assert.Equal(["Super Toolbar"], extras.Select(e => e.Name));
    }
}

public class AppResultMappingTests
{
    [Theory]
    [InlineData(InstallResultStatus.Ok, 0, false, AppOutcome.Installed)]
    [InlineData(InstallResultStatus.Ok, 0, true, AppOutcome.Updated)]
    [InlineData(InstallResultStatus.InstallError, WinGetAppEngine.RebootToFinish, false, AppOutcome.Installed)]
    [InlineData(InstallResultStatus.InstallError, WinGetAppEngine.AlreadyInstalled, false, AppOutcome.Skipped)]
    [InlineData(InstallResultStatus.NoApplicableUpgrade, 0, true, AppOutcome.UpToDate)]
    [InlineData(InstallResultStatus.PackageAgreementsNotAccepted, 0, false, AppOutcome.NeedsConsent)]
    [InlineData(InstallResultStatus.BlockedByPolicy, 0, false, AppOutcome.Blocked)]
    [InlineData(InstallResultStatus.DownloadError, WinGetAppEngine.HashMismatch, false, AppOutcome.DownloadFailed)]
    [InlineData(InstallResultStatus.InstallError, WinGetAppEngine.InstallInProgress, false, AppOutcome.Failed)]
    [InlineData(InstallResultStatus.DownloadError, WinGetAppEngine.DependenciesFailed, false, AppOutcome.Failed)]
    [InlineData(InstallResultStatus.InstallError, unchecked((int)0x80070643), false, AppOutcome.Failed)]
    public void Maps_winget_results(InstallResultStatus status, int hr, bool upgrade, AppOutcome expected) =>
        Assert.Equal(expected, WinGetAppEngine.MapInstall(status, hr, 0, upgrade).Item1);

    [Fact]
    public void Hash_mismatch_is_named_as_such() =>
        Assert.Contains("checksum", WinGetAppEngine.MapInstall(InstallResultStatus.DownloadError, WinGetAppEngine.HashMismatch, 0, false).Item2);

    [Fact]
    public void Another_install_running_is_recognised_for_the_retry() =>
        Assert.Contains("another installation", WinGetAppEngine.MapInstall(InstallResultStatus.InstallError, WinGetAppEngine.InstallInProgress, 0, false).Item2,
                        StringComparison.OrdinalIgnoreCase);
}

public class AppQueueTests
{
    sealed class FakeEngine : IAppEngine
    {
        public List<string> Calls { get; } = [];
        public Func<AppRow, AppOutcome> Outcome { get; set; } = _ => AppOutcome.Installed;
        public Task<AppStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(new AppStatus(true, "test", ""));
        public Task<IReadOnlyList<AppRow>> ListCuratedAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AppRow>>([]);
        public Task<IReadOnlyList<AppRow>> SearchAsync(string text, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AppRow>>([]);
        public Task<IReadOnlyList<AppRow>> ListUpdatesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AppRow>>([]);
        public Task<IReadOnlyList<AppRow>> ListInstalledAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AppRow>>([]);
        public Task<IReadOnlyList<AppRow>> DescribeAsync(IReadOnlyList<AppRow> rows) => Task.FromResult(rows);
        public Task<AppRow?> FindAsync(string id) => Task.FromResult<AppRow?>(null);
        public Task<AppResult> InstallAsync(AppRow app, AppInstallOptions o, IProgress<AppStep>? p = null, CancellationToken ct = default) => Do("install", app, AppAction.Install);
        public Task<AppResult> UpgradeAsync(AppRow app, AppInstallOptions o, IProgress<AppStep>? p = null, CancellationToken ct = default) => Do("upgrade", app, AppAction.Upgrade);
        public Task<AppResult> UninstallAsync(AppRow app, IProgress<AppStep>? p = null, CancellationToken ct = default) => Do("uninstall", app, AppAction.Uninstall);
        Task<AppResult> Do(string what, AppRow app, AppAction action)
        {
            Calls.Add($"{what} {app.Id}");
            return Task.FromResult(new AppResult { Id = app.Id, Name = app.Name, Action = action, Outcome = Outcome(app) });
        }
    }

    static AppRow Row(string id, string? installed = null, bool update = false, bool terms = false) => new()
    {
        Id = id, Name = id, InstalledVersion = installed, IsUpdateAvailable = update,
        Agreements = terms ? [new AppAgreement("Licence", "", "https://example.invalid/terms")] : [],
    };

    [Fact]
    public async Task Installed_apps_are_skipped_or_upgraded()
    {
        var engine = new FakeEngine();
        var results = await new AppQueue(engine).RunAsync([Row("A.New"), Row("B.Current", "1.0"), Row("C.Old", "1.0", update: true)],
                                                          AppAction.Install, new AppInstallOptions());
        Assert.Equal(["install A.New", "upgrade C.Old"], engine.Calls);
        Assert.Equal(AppOutcome.Skipped, results[1].Outcome);
    }

    [Fact]
    public async Task Terms_nobody_accepted_are_not_started()
    {
        var engine = new FakeEngine();
        var results = await new AppQueue(engine).RunAsync([Row("A.Terms", terms: true)], AppAction.Install, new AppInstallOptions());
        Assert.Empty(engine.Calls);
        Assert.Equal(AppOutcome.NeedsConsent, results[0].Outcome);

        results = await new AppQueue(engine).RunAsync([Row("A.Terms", terms: true)], AppAction.Install, new AppInstallOptions { AcceptAgreements = true });
        Assert.Equal(["install A.Terms"], engine.Calls);
    }

    [Fact]
    public async Task A_failure_does_not_stop_the_queue()
    {
        var engine = new FakeEngine { Outcome = a => a.Id == "B.Bad" ? AppOutcome.Failed : AppOutcome.Installed };
        var results = await new AppQueue(engine).RunAsync([Row("A.Ok"), Row("B.Bad"), Row("C.Ok")], AppAction.Install, new AppInstallOptions());
        Assert.Equal(3, engine.Calls.Count);
        Assert.Equal([AppOutcome.Installed, AppOutcome.Failed, AppOutcome.Installed], results.Select(r => r.Outcome));
    }

    [Fact]
    public async Task Cancel_stops_before_the_next_package()
    {
        using var cts = new CancellationTokenSource();
        var engine = new FakeEngine();
        engine.Outcome = _ => { cts.Cancel(); return AppOutcome.Installed; };   // cancel while the first one runs
        var results = await new AppQueue(engine).RunAsync([Row("A.First"), Row("B.Second")], AppAction.Install, new AppInstallOptions(), null, cts.Token);
        Assert.Equal(["install A.First"], engine.Calls);
        Assert.Equal([AppOutcome.Installed, AppOutcome.Cancelled], results.Select(r => r.Outcome));
    }

    [Fact]
    public void Summary_counts_and_mentions_restarts()
    {
        var results = new[]
        {
            new AppResult { Id = "a", Name = "a", Action = AppAction.Install, Outcome = AppOutcome.Installed, RebootRequired = true },
            new AppResult { Id = "b", Name = "b", Action = AppAction.Install, Outcome = AppOutcome.Installed },
            new AppResult { Id = "c", Name = "c", Action = AppAction.Install, Outcome = AppOutcome.Failed },
        };
        string s = AppQueue.Summary(results);
        Assert.StartsWith("2 installed, 1 failed.", s);
        Assert.Contains("Restart", s);
    }
}

public class AppIgnoreTests
{
    [Fact]
    public void Ignoring_a_version_clears_when_a_newer_one_appears()
    {
        var s = new AppSettings();
        s.IgnoredApps["Git.Git"] = "2.51.0";
        Assert.True(s.IsAppIgnored("git.git", "2.51.0"));
        Assert.False(s.IsAppIgnored("Git.Git", "2.52.0"));
    }

    [Fact]
    public void Ignoring_every_version()
    {
        var s = new AppSettings();
        s.IgnoredApps["Zoom.Zoom"] = "";
        Assert.True(s.IsAppIgnored("Zoom.Zoom", "7.0"));
    }

    [Fact]
    public void App_history_entries_never_roll_back()
    {
        var e = new HistoryEntry(DateTime.Now, "7zip.7zip", "7-Zip", new DriverRef("", "", "25.0", null, "7-Zip", ""),
                                 new DriverRef("", "", "26.04", null, "7-Zip", ""), "winget", true, false, false, "Updated.", null,
                                 new AppChange("Upgrade", "25.0", "26.04", []));
        Assert.Equal("app", e.Kind);
        Assert.False(e.CanRollBack);
    }
}

public class ScanSummaryAppTests
{
    [Fact]
    public void Mentions_app_updates_after_drivers()
    {
        var s = new ScanSummary(DateTime.Now, 0, 1, ["x"], AppUpdates: 3, AppsUpdated: 1);
        Assert.Equal("1 driver update available. 1 app was updated. 3 app updates are available.", s.Text);
        Assert.Equal(4, s.Total);
    }

    [Fact]
    public void Updated_apps_alone_do_not_count_as_needing_attention() =>
        Assert.Equal(0, new ScanSummary(DateTime.Now, 0, 0, [], AppsUpdated: 2).Total);
}
