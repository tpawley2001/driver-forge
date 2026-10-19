using System.Collections.Concurrent;
using Microsoft.Management.Deployment;

namespace DriverForge.Core.Apps;

/// <summary>
/// The winget engine, loaded in-process (APPS.md §3.2). Driver Forge is always
/// elevated, and App Installer's out-of-process server doesn't activate
/// normally from an elevated caller; the in-process engine works elevated, as
/// SYSTEM and as a standard user, and doesn't need App Installer.
///
/// ⚠️ The in-process API can deadlock on an STA thread, and WPF's UI thread is
/// STA, so every call goes through <see cref="RunAsync{T}"/> onto one MTA
/// thread this class owns. Activation itself comes from the app manifest
/// (reg-free WinRT, Windows 10 1903+): the activatableClass entries name
/// Microsoft.Management.Deployment.InProc.dll beside the exe.
/// </summary>
public sealed class WinGetSession : IDisposable
{
    readonly BlockingCollection<Action> _work = new();
    readonly Thread _thread;
    PackageManager? _manager;

    public WinGetSession()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "winget (MTA)" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    void Loop()
    {
        foreach (var item in _work.GetConsumingEnumerable()) item();
    }

    /// <summary>Run <paramref name="call"/> on the MTA thread.</summary>
    public Task<T> RunAsync<T>(Func<PackageManager, T> call)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { done.SetResult(call(Manager)); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    }

    PackageManager Manager
    {
        get
        {
            if (_manager is null)
            {
                // Names Driver Forge in winget's own logs. Must come before the
                // first PackageManager (Microsoft's in-process client does the same).
                new PackageManagerSettings().SetCallerIdentifier("DriverForge");
                _manager = new PackageManager();
            }
            return _manager;
        }
    }

    /// <summary>
    /// The community catalog ("winget" source) joined with what's installed,
    /// so each package carries its installed and available versions (§3.3).
    /// Call on the MTA thread (inside RunAsync).
    /// </summary>
    public static PackageCatalog Connect(PackageManager pm, CompositeSearchBehavior behavior)
    {
        var options = new CreateCompositePackageCatalogOptions { CompositeSearchBehavior = behavior };
        options.Catalogs.Add(pm.GetPredefinedPackageCatalog(PredefinedPackageCatalog.OpenWindowsCatalog));
        var result = pm.CreateCompositePackageCatalog(options).Connect();
        if (result.Status != ConnectResultStatus.Ok)
            throw new EngineException($"Can't open the winget catalog ({result.Status}).");
        return result.PackageCatalog;
    }

    /// <summary>The one package with exactly this winget ID, or null.</summary>
    public static CatalogPackage? FindById(PackageCatalog catalog, string id)
    {
        var find = new FindPackagesOptions();
        find.Filters.Add(new PackageMatchFilter
        {
            Field = PackageMatchField.Id,
            Option = PackageFieldMatchOption.EqualsCaseInsensitive,
            Value = id,
        });
        var matches = catalog.FindPackages(find).Matches;
        return matches.Count == 0 ? null : matches[0].CatalogPackage;
    }

    public void Dispose() => _work.CompleteAdding();
}
