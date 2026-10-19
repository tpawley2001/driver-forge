using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DriverForge.Core.Apps;

/// <summary>
/// The installed-programs list, read from the Uninstall registry keys, so an
/// install can be checked for bundled extras (APPS.md §6): anything that
/// appears besides the requested app and the runtimes it may pull in.
/// </summary>
public static class ProgramsSnapshot
{
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    const string UninstallKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Machine-wide entries plus the signed-in user's own (which an
    /// elevated process sees under HKEY_USERS, not its own HKCU).</summary>
    public static Dictionary<string, InstalledProgram> Take()
    {
        var all = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
        Read(Registry.LocalMachine, UninstallKey, "HKLM", all);
        Read(Registry.LocalMachine, UninstallKey32, "HKLM32", all);
        if (SessionUser.Sid() is { } sid)
            Read(Registry.Users, sid + "\\" + UninstallKey, "HKU", all);
        return all;
    }

    static void Read(RegistryKey hive, string path, string prefix, Dictionary<string, InstalledProgram> into)
    {
        using var root = hive.OpenSubKey(path);
        if (root is null) return;
        foreach (string name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name);
            if (k?.GetValue("DisplayName") is not string display || display.Trim().Length == 0) continue;
            if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
            into[prefix + "\\" + name] = new InstalledProgram(prefix + "\\" + name, display.Trim(),
                k.GetValue("Publisher") as string ?? "", k.GetValue("DisplayVersion") as string ?? "",
                k.GetValue("UninstallString") as string, k.GetValue("QuietUninstallString") as string);
        }
    }

    /// <summary>Entries in <paramref name="after"/> that weren't in <paramref name="before"/>.</summary>
    public static List<InstalledProgram> Added(IReadOnlyDictionary<string, InstalledProgram> before,
                                               IReadOnlyDictionary<string, InstalledProgram> after) =>
        after.Where(kv => !before.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();

    /// <summary>
    /// The new entries that are neither the requested app nor an allowed
    /// runtime. The requested app is recognised by its product codes (winget
    /// reports them for the installed version; for MSI and most installers the
    /// product code is the Uninstall subkey name), its name, or its publisher.
    /// The COM API doesn't expose a package's dependency list, so
    /// Apps/runtimes.json stands in for it (§6).
    /// </summary>
    public static List<InstalledProgram> Extras(IEnumerable<InstalledProgram> added, string appName, string appPublisher,
                                                IReadOnlyCollection<string> productCodes, IReadOnlyList<Regex> runtimes)
    {
        string name = Normalise(appName), publisher = Normalise(appPublisher);
        return added.Where(p =>
        {
            string sub = p.Key[(p.Key.IndexOf('\\') + 1)..];
            if (productCodes.Any(c => string.Equals(c, sub, StringComparison.OrdinalIgnoreCase))) return false;
            string n = Normalise(p.Name);
            if (name.Length > 0 && (n.Contains(name) || name.Contains(n))) return false;
            if (publisher.Length > 2 && Normalise(p.Publisher) == publisher) return false;
            return !runtimes.Any(r => r.IsMatch(p.Name));
        }).ToList();
    }

    static string Normalise(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Patterns from the embedded Apps/runtimes.json.</summary>
    public static IReadOnlyList<Regex> Runtimes()
    {
        using var stream = typeof(ProgramsSnapshot).Assembly.GetManifestResourceStream("DriverForge.Core.Apps.runtimes.json")
            ?? throw new InvalidOperationException("runtimes.json is not embedded");
        var doc = JsonSerializer.Deserialize<RuntimesFile>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        }) ?? new RuntimesFile();
        return doc.Patterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
    }

    sealed class RuntimesFile { public List<string> Patterns { get; set; } = []; }
}

/// <summary>The account signed in at the console, which can differ from the
/// elevated account Driver Forge runs as (APPS.md §5.3).</summary>
public static class SessionUser
{
    public static string? Name()
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF) return null;
        string? user = Query(session, 5 /* WTSUserName */), domain = Query(session, 7 /* WTSDomainName */);
        return string.IsNullOrEmpty(user) ? null : string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
    }

    public static string? Sid()
    {
        try { return Name() is { } n ? new NTAccount(n).Translate(typeof(SecurityIdentifier)).Value : null; }
        catch (IdentityNotMappedException) { return null; }
    }

    /// <summary>True when the elevated account isn't the one at the console.</summary>
    public static bool ElevatedAsSomeoneElse()
    {
        using var me = WindowsIdentity.GetCurrent();
        if (me.IsSystem) return false;
        string? sid = Sid();
        return sid is not null && me.User?.Value != sid;
    }

    static string? Query(uint session, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out IntPtr buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQuerySessionInformationW(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);
}
