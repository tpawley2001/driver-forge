using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DriverForge.Core;

/// <summary>A source that installs its own offers instead of handing over an INF (SPEC §4.1 step 3).</summary>
public interface IInstallingSource : IDriverSource
{
    Task<InstallResult> InstallAsync(DriverOffer offer, InstallOptions options, IProgress<InstallStep>? progress, CancellationToken ct);
}

/// <summary>
/// Driver updates from Windows Update through the Windows Update Agent COM API
/// (SPEC §4.1), including the optional ones Windows doesn't install on its own.
/// WUA validated the package, so WUA downloads and installs it too.
/// </summary>
public sealed class WindowsUpdateSource : IInstallingSource
{
    public const string SourceName = "Windows Update";
    public string Name => SourceName;

    const int ServerWindowsUpdate = 2;          // ssWindowsUpdate
    const int ResultSucceeded = 2, ResultSucceededWithErrors = 3;

    public Task<IReadOnlyList<SourcedOffer>> FindAsync(IReadOnlyList<Device> devices, OsInfo os, IProgress<string>? progress, CancellationToken ct) =>
        Task.Run<IReadOnlyList<SourcedOffer>>(() =>
        {
            progress?.Report("Asking Windows Update…");
            var found = new List<SourcedOffer>();
            foreach (var u in Search("IsInstalled=0 and Type='Driver'", ct))
                foreach (var d in devices)
                    if (ToOffer(d, u) is { } o) found.Add(new(d.InstanceId, o));
            return found;
        }, ct);

    public Task<string> AcquireAsync(DriverOffer offer, IProgress<InstallStep>? progress, CancellationToken ct) =>
        throw new EngineException("Windows Update installs its own drivers; there's no INF to hand over.");

    public Task<InstallResult> InstallAsync(DriverOffer offer, InstallOptions options, IProgress<InstallStep>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            string id = UpdateId(offer);
            dynamic session = NewSession();
            dynamic searcher = Searcher(session);
            dynamic result = Wua(() => searcher.Search($"UpdateID='{id}'"));
            if ((int)result.Updates.Count == 0)
                return new InstallResult(false, false, 0, "Windows Update no longer offers this driver. Scan again.");
            dynamic update = result.Updates.Item(0);
            if (!(bool)update.EulaAccepted) update.AcceptEula();
            dynamic list = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", true)!)!;
            list.Add(update);

            ct.ThrowIfCancellationRequested();
            progress?.Report(InstallStep.Downloading);
            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = list;
            dynamic dl = Wua(() => downloader.Download());
            int dlCode = (int)dl.ResultCode;
            if (dlCode is not (ResultSucceeded or ResultSucceededWithErrors))
                return new InstallResult(false, false, (int)dl.HResult, $"Windows Update couldn't download the driver ({Hex((int)dl.HResult)}).");

            if (options.TestMode)
                return new InstallResult(true, false, 0, "Downloaded; install skipped (test mode)", Simulated: true);

            ct.ThrowIfCancellationRequested();
            progress?.Report(InstallStep.Installing);
            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = list;
            dynamic ir = Wua(() => installer.Install());
            int code = (int)ir.ResultCode, hr = (int)ir.GetUpdateResult(0).HResult;
            bool reboot = (bool)ir.RebootRequired;
            return code is ResultSucceeded or ResultSucceededWithErrors
                ? new InstallResult(true, reboot, 0, reboot ? "Installed; restart to finish" : "Installed")
                : new InstallResult(false, reboot, hr, $"Windows Update couldn't install the driver ({Hex(hr)}).");
        }, ct);

    /// <summary>A driver update as WUA describes it (IWindowsDriverUpdate).</summary>
    internal sealed record WuDriver(string UpdateId, string Title, string HardwareId, DateOnly? Date,
        string Class, string Manufacturer, string Provider, string Model, long DownloadBytes);

    static List<WuDriver> Search(string criteria, CancellationToken ct)
    {
        dynamic searcher = Searcher(NewSession());
        dynamic result = Wua(() => searcher.Search(criteria));
        ct.ThrowIfCancellationRequested();
        var list = new List<WuDriver>();
        int n = (int)result.Updates.Count;
        for (int i = 0; i < n; i++)
        {
            dynamic u = result.Updates.Item(i);
            string hwid = (string?)u.DriverHardwareID ?? "";
            if (hwid.Length == 0) continue;
            DateTime? date = u.DriverVerDate is DateTime dt ? dt : null;
            list.Add(new WuDriver((string)u.Identity.UpdateID, (string?)u.Title ?? "", hwid,
                date is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null,
                (string?)u.DriverClass ?? "", (string?)u.DriverManufacturer ?? "", (string?)u.DriverProvider ?? "",
                (string?)u.DriverModel ?? "", Convert.ToInt64(u.MaxDownloadSize)));
        }
        return list;
    }

    static dynamic NewSession()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session")
            ?? throw new EngineException("The Windows Update Agent isn't available on this PC.");
        dynamic session = Activator.CreateInstance(type)!;
        session.ClientApplicationID = "Driver Forge";
        return session;
    }

    static dynamic Searcher(dynamic session)
    {
        dynamic searcher = session.CreateUpdateSearcher();
        searcher.ServerSelection = ServerWindowsUpdate;
        searcher.Online = true;
        return searcher;
    }

    /// <summary>Turns WUA's COM errors into a readable message.</summary>
    static T Wua<T>(Func<T> call)
    {
        try { return call(); }
        catch (COMException ex)
        {
            throw new EngineException(ex.HResult switch
            {
                unchecked((int)0x8024402C) or unchecked((int)0x80072EE7) or unchecked((int)0x8024401C) => "Windows Update can't be reached (no internet?).",
                unchecked((int)0x8024002E) => "Windows Update access is turned off by policy on this PC.",
                unchecked((int)0x80240044) => "Windows Update needs administrator rights here.",
                _ => $"Windows Update failed ({Hex(ex.HResult)}).",
            });
        }
    }

    static string Hex(int hr) => $"0x{hr:X8}";

    static string UpdateId(DriverOffer offer) =>
        offer.Id.StartsWith("wu|", StringComparison.Ordinal) ? offer.Id[3..]
        : throw new EngineException("This offer isn't from Windows Update.");

    internal static DriverOffer? ToOffer(Device d, WuDriver u)
    {
        var match = IdMatch.Best(d, u.HardwareId, []);
        if (match is null) return null;
        // WU offers are signed by Microsoft (signature score best); WUA doesn't
        // report the feature score, so it's the default.
        uint rank = DriverRank.Compute(SignatureClass.Trusted, DriverRank.DefaultFeatureScore, match.Value.Score);
        return new DriverOffer(
            Id: "wu|" + u.UpdateId,
            Source: SourceName,
            Description: u.Model.Length > 0 ? u.Model : u.Title,
            Provider: u.Provider.Length > 0 ? u.Provider : u.Manufacturer,
            Version: VersionFromTitle(u.Title),
            Date: u.Date,
            MatchedId: match.Value.DeviceId,
            Match: match.Value.Kind,
            Rank: rank,
            Signed: true,
            IsLocal: false,
            DownloadBytes: u.DownloadBytes > 0 ? u.DownloadBytes : null);
    }

    /// <summary>
    /// WUA reports a driver's date but not its version; WU driver titles end
    /// with it ("Intel - System - 10.1.1.45").
    /// </summary>
    internal static DriverVersion VersionFromTitle(string title)
    {
        var m = Regex.Match(title, @"(\d+(?:\.\d+){1,3})\s*$");
        return m.Success ? DriverVersion.Parse(m.Groups[1].Value) : DriverVersion.Unknown;
    }
}
