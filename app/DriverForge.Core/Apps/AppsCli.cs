using System.Runtime.InteropServices;
using System.Text.Json;

namespace DriverForge.Core.Apps;

/// <summary>
/// The Apps command line (APPS.md §8), for setting up a new PC from the
/// portable copy and for checks on the test box. No window: results go to
/// the console the app was started from (if any) and to last-apps.json in
/// the data folder. Exit code 0 when every package ended Installed,
/// Updated, Uninstalled, Skipped, Up to date or Simulated.
/// </summary>
public static class AppsCli
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    static readonly string[] Commands = ["--apps-install", "--apps-update", "--apps-uninstall", "--apps-export", "--apps-json"];

    /// <summary>Only the command names: --apps-tab / --apps-search belong to --screenshot.</summary>
    public static bool Handles(string[] args) => args.Any(a => Commands.Contains(a, StringComparer.Ordinal));

    public static async Task<int> RunAsync(string[] args, string data)
    {
        AttachConsole(-1);                      // ATTACH_PARENT_PROCESS; harmless when there's none
        var settings = AppSettings.Load(Path.Combine(data, "settings.json"));
        using var engine = new WinGetAppEngine(data);
        var status = await engine.GetStatusAsync();
        if (!status.Available)
        {
            Say(status.Message);
            File.WriteAllText(Path.Combine(data, "last-apps.json"), JsonSerializer.Serialize(new { status.Message }, Json));
            return 2;
        }
        var options = new AppInstallOptions
        {
            AcceptAgreements = args.Contains("--accept-agreements"),
            MachineScopeOnly = Arg(args, "--scope") is { } scope ? !scope.Equals("any", StringComparison.OrdinalIgnoreCase) : !settings.AppsAnyScope,
            TestMode = args.Contains("--test-mode") || settings.TestMode,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
        var progress = new Progress<AppStep>(s => { if (s.Percent is 0 or >= 100) Say($"  {s.Id}: {s.State}"); });

        IReadOnlyList<AppResult> results;
        if (Arg(args, "--apps-json") is { } what)
        {
            string output = Arg(args, what) ?? Path.Combine(data, "apps.json");
            object payload = what switch
            {
                "curated" => await engine.ListCuratedAsync(),
                "installed" => await engine.ListInstalledAsync(),
                "updates" => await engine.ListUpdatesAsync(),
                "missing" => await engine.MissingCuratedAsync(),
                _ when what.StartsWith("search=") => await engine.SearchAsync(what["search=".Length..]),
                _ when what.StartsWith("describe=") => await engine.DescribeAsync(await RowsAsync(engine, what["describe=".Length..].Split(','))),
                _ => throw new ArgumentException($"unknown --apps-json {what}"),
            };
            File.WriteAllText(output, JsonSerializer.Serialize(payload, Json));
            Say($"wrote {output}");
            return 0;
        }
        if (Arg(args, "--apps-export") is { } exportPath)
        {
            var only = Arg(args, "--only")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var ids = (await engine.ListInstalledAsync()).Where(r => r.AvailableVersion is not null)
                .Where(r => only is null || only.Contains(r.Id, StringComparer.OrdinalIgnoreCase)).Select(r => r.Id);
            File.WriteAllText(exportPath, AppList.Write(ids, status.EngineVersion));
            Say($"wrote {exportPath}");
            return 0;
        }
        if (Arg(args, "--apps-install") is { } listPath)
        {
            var rows = await engine.DescribeAsync(await RowsAsync(engine, AppList.Read(File.ReadAllText(listPath))));
            Say($"installing {rows.Count} app(s)");
            results = await new AppQueue(engine).RunAsync(rows, AppAction.Install, options, progress);
        }
        else if (args.Contains("--apps-update"))
        {
            bool all = args.Contains("--include-self-updating");
            var only = Arg(args, "--only")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var updates = (await engine.ListUpdatesAsync())
                .Where(r => only is null || only.Contains(r.Id, StringComparer.OrdinalIgnoreCase))
                .Where(r => all || only is not null || !r.SelfUpdating)
                .Where(r => !settings.IsAppIgnored(r.Id, r.AvailableVersion))
                .ToList();
            Say($"updating {updates.Count} app(s)");
            results = await new AppQueue(engine).RunAsync(await engine.DescribeAsync(updates), AppAction.Upgrade, options, progress);
        }
        else if (Arg(args, "--apps-uninstall") is { } id)
        {
            results = await new AppQueue(engine).RunAsync(await RowsAsync(engine, [id]), AppAction.Uninstall, options, progress);
        }
        else throw new ArgumentException("unknown --apps- command");

        foreach (var r in results) Say($"{r.Name} ({r.Id}): {r.Outcome}. {r.Message}");
        Say(AppQueue.Summary(results));
        File.WriteAllText(Path.Combine(data, "last-apps.json"), JsonSerializer.Serialize(results, Json));
        return results.All(r => r.Succeeded) ? 0 : 1;
    }

    /// <summary>Rows for IDs, as the installed+catalog view sees them; an ID
    /// winget doesn't know still gets a row, so it's reported, not dropped.</summary>
    static async Task<IReadOnlyList<AppRow>> RowsAsync(WinGetAppEngine engine, IEnumerable<string> ids)
    {
        var rows = new List<AppRow>();
        foreach (string id in ids.Select(i => i.Trim()).Where(i => i.Length > 0))
        {
            var found = (await engine.FindAsync(id));
            rows.Add(found ?? new AppRow { Id = id, Name = id });
        }
        return rows;
    }

    static void Say(string line) => Console.WriteLine(line);

    static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
