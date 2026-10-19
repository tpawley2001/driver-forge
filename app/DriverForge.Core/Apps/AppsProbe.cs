using System.Security.Principal;
using Microsoft.Management.Deployment;

namespace DriverForge.Core.Apps;

/// <summary>
/// Milestone A1 (APPS.md §10): prove the in-process winget engine works from
/// this process. Finds a package, and with <c>install</c> installs it
/// silently, checks it shows as installed, uninstalls it, and checks it's gone.
/// Every step is a line in the returned log; the run fails on the first
/// broken step.
/// </summary>
public static class AppsProbe
{
    public static async Task<(bool Ok, List<string> Log)> RunAsync(string packageId, bool install)
    {
        var log = new List<string>();
        void Say(string line) => log.Add($"{DateTime.Now:HH:mm:ss} {line}");

        using var identity = WindowsIdentity.GetCurrent();
        bool admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        Say($"identity {identity.Name}, elevated={admin}, system={identity.IsSystem}, " +
            $"apartment(main)={Thread.CurrentThread.GetApartmentState()}");

        using var session = new WinGetSession();
        try
        {
            string version = await session.RunAsync(pm => pm.Version);
            Say($"engine started in-process, winget {version}");

            var found = await session.RunAsync(pm =>
            {
                var catalog = WinGetSession.Connect(pm, CompositeSearchBehavior.RemotePackagesFromAllCatalogs);
                var pkg = WinGetSession.FindById(catalog, packageId);
                return pkg is null ? null : new
                {
                    pkg.Id, pkg.Name,
                    Installed = pkg.InstalledVersion?.Version,
                    Available = pkg.DefaultInstallVersion?.Version,
                };
            });
            if (found is null) { Say($"FAIL {packageId} not in the catalog"); return (false, log); }
            Say($"found {found.Id} \"{found.Name}\" installed={found.Installed ?? "-"} available={found.Available}");
            if (!install) return (true, log);
            if (found.Installed is not null) { Say("FAIL already installed; uninstall it first for a clean probe"); return (false, log); }

            var installed = await session.RunAsync(pm =>
            {
                var catalog = WinGetSession.Connect(pm, CompositeSearchBehavior.RemotePackagesFromAllCatalogs);
                var pkg = WinGetSession.FindById(catalog, packageId)!;
                var options = new Microsoft.Management.Deployment.InstallOptions
                {
                    PackageInstallMode = PackageInstallMode.Silent,
                    PackageInstallScope = PackageInstallScope.SystemOrUnknown,
                    AcceptPackageAgreements = true,
                };
                var op = pm.InstallPackageAsync(pkg, options);
                var states = new List<string>();
                op.Progress = (_, p) => { string s = p.State.ToString(); if (states.LastOrDefault() != s) states.Add(s); };
                var r = op.GetAwaiter().GetResult();
                return (r.Status, r.RebootRequired, Error: r.ExtendedErrorCode?.HResult ?? 0, r.InstallerErrorCode, States: string.Join(">", states));
            });
            Say($"install {installed.Status} reboot={installed.RebootRequired} hr=0x{installed.Error:X8} " +
                $"installer={installed.InstallerErrorCode} progress={installed.States}");
            if (installed.Status != InstallResultStatus.Ok) return (false, log);

            string? now = await InstalledVersionAsync(session, packageId);
            Say($"after install: installed={now ?? "-"}");
            if (now is null) { Say("FAIL not listed as installed"); return (false, log); }

            var removed = await session.RunAsync(pm =>
            {
                var catalog = WinGetSession.Connect(pm, CompositeSearchBehavior.LocalCatalogs);
                var pkg = WinGetSession.FindById(catalog, packageId)!;
                var r = pm.UninstallPackageAsync(pkg, new UninstallOptions { PackageUninstallMode = PackageUninstallMode.Silent })
                          .GetAwaiter().GetResult();
                return (r.Status, r.RebootRequired, Error: r.ExtendedErrorCode?.HResult ?? 0);
            });
            Say($"uninstall {removed.Status} reboot={removed.RebootRequired} hr=0x{removed.Error:X8}");
            if (removed.Status != UninstallResultStatus.Ok) return (false, log);

            now = await InstalledVersionAsync(session, packageId);
            Say($"after uninstall: installed={now ?? "-"}");
            if (now is not null) { Say("FAIL still listed as installed"); return (false, log); }
            Say("PASS");
            return (true, log);
        }
        catch (Exception ex)
        {
            Say($"FAIL {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
            Say(ex.ToString());
            return (false, log);
        }
    }

    static Task<string?> InstalledVersionAsync(WinGetSession session, string id) =>
        session.RunAsync(pm =>
        {
            var catalog = WinGetSession.Connect(pm, CompositeSearchBehavior.LocalCatalogs);
            return WinGetSession.FindById(catalog, id)?.InstalledVersion?.Version;
        });
}
