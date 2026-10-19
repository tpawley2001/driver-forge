# Driver Forge — Apps specification

The **Apps** page installs and updates everyday Windows programs the way
Ninite does: tick the apps you want, press one button, and they install
silently from their publishers, with no setup wizards and no bundled offers.
Run it again later and it updates them. This document specifies the Apps
engine and page. It sits beside the driver engine ([SPEC.md](SPEC.md)) and
reuses its history, scheduling and settings (SPEC §7, §9).

Driver Forge doesn't keep its own list of download links. It drives the
**Windows Package Manager** (winget) through Microsoft's COM API, so package
data (installer URLs, hashes, silent switches) comes from Microsoft's
community catalog, which is maintained and checked independently of us.

Target: same as the driver engine (Windows 10 1809+ and Windows 11, x64,
.NET 10, elevated process).

---

## 1. Goals and non-goals

**Goals**

1. A curated picker: about 60 common apps in categories, each a checkbox.
   Search of the full winget catalog for anything else.
2. One-click batch install: silent, unattended, latest version, 64-bit when
   available, machine-wide when the installer allows it, already-installed
   apps skipped (or updated).
3. Updates: every installed program winget recognises and has a newer
   version of, curated or not. Update all or pick, with an ignore list.
4. Catch bundled extras: compare installed programs before and after each
   install and flag anything that appeared besides the requested app (§6).
5. Scheduled updates through the existing scheduled task (SPEC §9).
6. Lists that move between PCs: export and import Microsoft's
   `winget export` JSON format, plus a command-line mode for unattended setup
   of a new PC from the portable copy (§8).

**Non-goals**

- Hosting, mirroring or repackaging installers. Every download comes from the
  URL in the winget manifest, verified against the manifest's SHA-256.
- Our own package manifests or a fork of the catalog.
- The Microsoft Store source in the first version (its terms and sign-in
  rules need their own design). The Store catalog is a later milestone.
- Paid or licence-key software, or anything a vendor only distributes behind
  a sign-in.
- Removing preinstalled Windows apps ("debloat"). Separate feature if ever.
- Version rollback. Uninstall is offered; reinstalling an older version is
  not in the first version (winget can do it, but downgrades of apps with
  their own data are risky).

---

## 2. Architecture

```
 DriverForge (WPF)                  DriverForge.Core/Apps
 ────────────────────               ───────────────────────────────────────────
 AppsViewModel   ── list ────────▶  IAppEngine
 AppUpdatesView  ── updates ─────▶    ├─ WinGetSession     (COM activation)       §3
 (Apps page,                          ├─ Curated catalog   (catalog.json)         §4
  two tabs)                           ├─ AppQueue          (install / upgrade)    §5
                                      ├─ ProgramsSnapshot  (bundled-offer check)  §6
                                      └─ History           (existing, SPEC §7)
```

```csharp
public interface IAppEngine
{
    Task<AppStatus> GetStatusAsync(CancellationToken ct);           // winget usable? version, source health
    Task<IReadOnlyList<AppRow>> ListCuratedAsync(CancellationToken ct);
    Task<IReadOnlyList<AppRow>> SearchAsync(string text, CancellationToken ct);
    Task<IReadOnlyList<AppRow>> ListUpdatesAsync(CancellationToken ct);
    Task<AppResult> InstallAsync(AppRow app, AppInstallOptions options,
                                 IProgress<AppStep>? progress, CancellationToken ct);
    Task<AppResult> UpgradeAsync(AppRow app, AppInstallOptions options,
                                 IProgress<AppStep>? progress, CancellationToken ct);
    Task<AppResult> UninstallAsync(AppRow app, IProgress<AppStep>? progress, CancellationToken ct);
}
```

`AppRow`: winget package ID, name, publisher, installed version (if any),
available version, category (curated only), flags from the curated catalog
(§4), and whether the package has licence terms to accept.

A `DemoAppEngine` (fixed rows, simulated progress) backs `--demo`, as
`DemoEngine` does for drivers.

---

## 3. Talking to winget

### 3.1 Packages

| NuGet package | Version | Licence | Use |
|---|---|---|---|
| `Microsoft.WindowsPackageManager.InProcCom` | 1.29.380 | MIT | The winget engine loaded into Driver Forge's own process |
| `Microsoft.WindowsPackageManager.ComInterop` | 1.29.380 | MIT | CsWinRT projection of `Microsoft.Management.Deployment` |

Both are published and verified by Microsoft and listed in
`THIRD-PARTY-NOTICES.md`. InProcCom adds about 8 MB (x64) to the build.

Build notes (all in `app/Directory.Build.props` / `.targets`):

- The projection targets `net8.0-windows10.0.26100.0`, so every project
  targets `net10.0-windows10.0.26100.0` (`SupportedOSPlatformVersion` stays
  10.0.17763.0). That pulls in `Microsoft.Windows.SDK.NET.dll` (25 MB).
  ⚠️ **Open decision:** that DLL is under the Windows SDK licence
  (redistributable inside an app, not MIT), which CLEAN-ROOM.md's
  permissive-only rule doesn't allow. Either accept it as an exception (every
  .NET app on a Windows-versioned TFM ships it), or generate our own
  projection of just the winget API and the few `Windows.Foundation` types it
  uses with C#/WinRT (MIT) and go back to plain `net10.0-windows`. No release
  ships with it until this is decided.
- `MicrosoftManagementDeployment-FactoryLinkage=none`: no out-of-process
  factory DLL, so activation can only reach the in-process engine.
- ComInterop adds its `.winmd` as a reference, which .NET 5+ refuses
  (NETSDK1130); a target drops it before the SDK's check.
- InProcCom is referenced with `ExcludeAssets="compile;runtime"` (its lib
  `.winmd` has no TFM, NU1701), and its 130 MB of `.pdb` native assets are
  removed from the output.

### 3.2 Activation

Driver Forge always runs elevated (`requireAdministrator`), including the
scheduled task. The out-of-process COM server that ships with App Installer
doesn't activate normally from an elevated caller: Microsoft's own
`Microsoft.WinGet.Client` module needs a special activation helper for
administrators and loads winget **in-process** when running as SYSTEM.

Decision: **in-process** (InProcCom) is the primary path, because it

- works the same elevated, as SYSTEM and as a standard user;
- doesn't need App Installer at all (missing on Windows 10 LTSC, Server, and
  some broken Store installs), so the Apps page works on the same PCs the
  driver engine does;
- pins the winget engine version to what we tested.

Out-of-process activation (App Installer's server) was the planned
fallback; A1 showed the in-process engine works from every context Driver
Forge runs in, so the fallback isn't built.

Activation is reg-free WinRT from the app manifest: `app.manifest` lists
the engine's `activatableClass` entries against
`Microsoft.Management.Deployment.InProc.dll` (copied from that DLL's own
manifest in the package; regenerate on a package update). Windows supports
this for unpackaged apps from **Windows 10 1903**, so the Apps page needs
1903+ (the driver engine still runs on 1809) and says so on older builds.

Rules from Microsoft's client code:

- Every in-process COM call runs on an **MTA** thread. The in-process API can
  deadlock on an STA thread, and WPF's UI thread is STA. `WinGetSession` owns
  one dedicated MTA worker thread and marshals all calls onto it.
- Call `PackageManagerSettings.SetCallerIdentifier("DriverForge")` once before
  the first `PackageManager`, so winget's logs name the caller.

### 3.3 Catalogs

- Remote: `PackageManager.GetPredefinedPackageCatalog(PredefinedPackageCatalog.OpenWindowsCatalog)`
  (the community "winget" source). Plus `DesktopFrameworks` for dependencies
  (VC++ runtimes, .NET) that winget resolves on its own.
- Installed: `GetLocalPackageCatalog(LocalPackageCatalog.InstalledPackages)`.
- Searches use a composite catalog
  (`CreateCompositePackageCatalog`, remote + local,
  `CompositeSearchBehavior.RemotePackagesFromAllCatalogs` for the picker,
  `LocalCatalogs` for the updates list), so each `CatalogPackage` carries
  `InstalledVersion`, `DefaultInstallVersion` and `IsUpdateAvailable` together.
- Source refresh: winget's own cache rules. A failed refresh (offline) shows
  a warning, and the page still lists what's installed.

### 3.4 Install options

`InstallOptions` for every install and upgrade:

| Option | Value | Why |
|---|---|---|
| `PackageInstallMode` | `Silent` | No wizard, no prompts (the core Ninite promise) |
| `PackageInstallScope` | Install: `SystemOrUnknown` (setting: `Any`). Upgrade: `Any` | Machine-wide, so it works for every user and the elevated install doesn't land in the admin's profile (§5.3). An upgrade keeps the scope the app is already installed in (found in A6: a per-user MobaXterm had "no installer" under `SystemOrUnknown`) |
| `AcceptPackageAgreements` | `true` only after consent (§5.2) | Packages with licence terms otherwise fail with `PackageAgreementsNotAccepted` |
| `AllowUpgradeToUnknownVersion` | `false` | Don't "upgrade" apps whose installed version winget can't read |
| `Force` | `false` | Never override hash or applicability checks |
| `PackageVersionId` | unset | Latest (`DefaultInstallVersion`) |
| `CorrelationData` | history entry ID | Ties winget's log lines to our history |

Architecture: winget picks the best installer for the OS (x64 before x86).
Locale: winget's default (the OS UI language, then en-US).

### 3.5 Results

`InstallResult.Status` (`InstallResultStatus`) maps to what the user sees:

| Status / code | Shown as |
|---|---|
| `Ok` | Installed (or Updated). `RebootRequired` → "Restart to finish" |
| (checked before the call: `InstalledVersion` set) | Skipped: already installed, or queued as an upgrade if `IsUpdateAvailable` |
| `InstallError` + `0x8A150061` | Skipped: already installed (winget found it although the catalog match didn't) |
| `NoApplicableUpgrade` / `0x8A15002B` | Up to date |
| `PackageAgreementsNotAccepted` | Needs licence consent |
| `BlockedByPolicy` / `0x8A15010F` | Blocked by this PC's policy |
| `DownloadError`, `0x8A150011` hash mismatch | Download failed (hash mismatch is shown as such, never retried with force) |
| `InstallError` + `0x8A150102` | Another install is running; retried once after 60 s |
| `InstallError` + `0x8A150109` | Installed, restart to finish |
| `InstallError` other | Failed, with `InstallerErrorCode` and `ExtendedErrorCode` |

Codes are from winget's documented return codes (`returnCodes.md`).

---

## 4. Curated catalog

A data file, `app/DriverForge.Core/Apps/catalog.json`, embedded in the build:

```json
{
  "version": 1,
  "categories": ["Browsers", "Messaging", "Media", "Imaging", "Documents",
                 "Security", "Runtimes", "File sharing", "Cloud storage",
                 "Compression", "Utilities", "Developer tools"],
  "apps": [
    { "id": "Mozilla.Firefox", "category": "Browsers",
      "selfUpdating": true, "verified": "2026-10-20" },
    { "id": "7zip.7zip", "category": "Compression", "verified": "2026-10-20" },
    { "id": "VideoLAN.VLC", "category": "Media", "verified": "2026-10-20" }
  ]
}
```

- `id` is the winget package ID. Name, publisher and version come live from
  the catalog, never from this file, so it can't go stale on those.
- `verified` is the date the app's silent install was run on the test box
  with no bundled extras (§6) and no window left open. **An app goes into the
  file only after that run.** The list is ours. It isn't copied from Ninite or
  any other installer.
- `selfUpdating`: the app updates itself (browsers, chat apps, Steam). It is
  installed like any other, but left out of scheduled updates by default,
  because a second updater only races the app's own.
- `machineOnly`: optional, only offer the app when a machine-scope installer
  exists.
- A curated ID that no longer resolves in the catalog is hidden, and a test
  (`CatalogTests`) fails on it, so a renamed package is caught at build time.

Icons: the generic Fluent icon for the category. No vendor logos are shipped.

---

## 5. Install and update queue

### 5.1 Flow

1. The user ticks apps (Apps tab) or updates (Updates tab) → **Install** /
   **Update selected**.
2. Consent (§5.2) if any package has licence terms.
3. Optional restore point (setting, off by default for apps), reusing
   `RestorePoints.cs`.
4. The queue runs **one package at a time** (Windows Installer allows only one
   install anyway). For each: program snapshot (§6) → `InstallPackageAsync` /
   `UpgradePackageAsync` with progress (`PackageInstallProgressState`: queued,
   downloading with bytes, installing, post-install, finished) → snapshot
   again → history entry.
5. A failure never stops the queue. The summary lists each app's result.
   **Cancel** stops after the current package. A running installer is never
   killed half-way.
6. Restarts: never automatic. "Restart to finish" is collected and shown once
   at the end.

### 5.2 Licence terms

Packages that declare agreements are marked in the list. Before the batch
starts, one dialog lists them with links to each set of terms, and the user
ticks "I accept these terms". Only then is `AcceptPackageAgreements` set.
Packages without terms install without the dialog. The command-line mode
(§8) needs `--accept-agreements`, or those packages are skipped and
reported.

### 5.3 Scope and the elevated user

Driver Forge runs elevated. On a PC where the person signs in as a standard
user and elevates with another account's password, a **user-scope**
installer would install into the *admin* account's profile. So:

- Machine scope is preferred (`SystemOrUnknown`).
- If a package only has a user-scope installer and the elevated account
  differs from the signed-in session's user (`WTSQuerySessionInformation`
  `WTSUserName` vs the process token), the row says "Installs for
  <admin account> only" and is not pre-selected.

### 5.4 Updates list

`ListUpdatesAsync`: installed packages where `IsUpdateAvailable` is true and
`InstalledVersion` is known. Columns: name, installed, available, source,
curated/self-updating flags. The ignore list (`settings.json`, as for
drivers) takes a package ID and an optional version ("ignore this version",
which clears itself when a newer one appears). Apps winget can't match to a
catalog package are listed under "Not managed" with no action.

### 5.5 Test mode

As SPEC §6.3: everything runs except the install call. Test mode downloads
the installer (`DownloadPackageAsync`) and checks its hash, then reports
"Simulated".

---

## 6. Bundled-offer check

Ninite's promise is "no toolbars or extra junk". winget's silent switches
usually decline offers, but a manifest can be wrong, so Driver Forge
**checks** instead of trusting:

- Snapshot = the installed-programs registry keys
  (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`, its
  `WOW6432Node` twin, and `HKCU\…\Uninstall` of the signed-in user), keyed by
  subkey name, with `DisplayName`, `Publisher`, `DisplayVersion`.
  `SystemComponent=1` entries and entries with no `DisplayName` are ignored.
- After each install, new entries other than the requested app are matched
  against an allowlist of runtimes winget installs as dependencies
  (`Apps/runtimes.json`: VC++ redistributables, .NET and Windows App SDK
  runtimes, WebView2) and against the requested package's own publisher. The
  COM API doesn't expose a package's dependency list, so the allowlist stands
  in for it. Anything left over is flagged
  in the result: **"Also installed: <name> (<publisher>)"**, with an
  **Uninstall** button (its `QuietUninstallString`, else `UninstallString`).
- A curated app that triggers a flag on the test box loses its `verified`
  status until it's checked again.

The check runs per package, sequentially, so a flag is always attributed to
the right install.

---

## 7. History

App actions go into the existing `history.jsonl` (SPEC §7) with
`"kind": "app"`: time, package ID and name, from version, to version, action
(install, upgrade, uninstall), result, flagged extras. The History page
shows both kinds with a filter. Rollback for apps is **Uninstall** only (§1).

---

## 8. Scheduling, lists and command line

- **Scheduled updates**: the existing `Driver Forge\Scan` task (SPEC §9)
  gains an app pass. `--scan-only` also lists app updates. A setting,
  "Install app updates automatically" (off by default), installs them in
  that run, excluding `selfUpdating` apps and the ignore list, and reports
  in the same notification balloon. No new scheduled task.
- **Export / import**: Microsoft's `winget export` format (schema
  `https://aka.ms/winget-packages.schema.2.0.json`: `Sources[].Packages[].PackageIdentifier`).
  Export writes the ticked apps (or all installed catalog apps). Import ticks
  them. Files stay compatible with `winget import` both ways.
- **Command line** (for setting up a new PC from the portable zip):

  ```
  DriverForge.exe --apps-install <list.json> [--accept-agreements] [--scope machine|any]
  DriverForge.exe --apps-update [--include-self-updating] [--only <id,id>]
  DriverForge.exe --apps-uninstall <id>
  DriverForge.exe --apps-export <list.json> [--only <id,id>]
  DriverForge.exe --apps-json curated|installed|updates|missing|search=<text>|describe=<id,id> <out.json>
  ```

  `--test-mode` on any of them simulates (§5.5). `--only` limits an update
  run to those IDs (and includes them even if they update themselves).

  No window, results to the console and the data folder (`last-apps.json`),
  exit code 0 when every package ended Installed, Skipped or Up to date, 1
  otherwise.

---

## 9. Page layout

Apps sits in the left navigation after Drivers, with two tabs:

- **Get apps**: categories as cards of checkboxes (name and a ✓ when
  installed, "Update available" badge). Search box on top: typing searches
  the full catalog, and results appear in an "Other apps" card. Bottom bar:
  "N selected", **Install**, Export list, Import list.
- **Updates**: the §5.4 table, **Update all** / **Update selected**, Ignore.

Install progress reuses the Install page's per-item progress style. Status
line at the top: winget engine version and source health ("Catalog updated
2 hours ago" / "Offline: showing installed apps only").

---

## 10. Milestones

| # | Milestone | Done when |
|---|---|---|
| A1 | Activation spike (§3.2) | From the elevated app on the test box: in-process engine starts on an MTA thread, searches `OpenWindowsCatalog`, installs and uninstalls 7-Zip silently. Also run from the scheduled task. **Done 2026-10-18** (`build/gui.sh apps-probe 7zip.7zip --install`): Windows 10 22H2 (19045), engine 1.29.380 in-process. As the signed-in admin through an interactive highest-run-level task (how `Driver Forge\Scan` runs): found 26.04, installed silently (Downloading > Installing > PostInstall, 7 s), listed as installed, uninstalled, gone. Same as SYSTEM. No App Installer involvement |
| A2 | Engine read side (§3.3, §4) | Curated list and full-catalog search with installed/available versions. `catalog.json` parses and `CatalogTests` resolve every ID. **Done 2026-10-18**: offline catalog tests in xunit; live, `--apps-json missing` resolved 60 of 61 curated IDs (RustDesk isn't in the catalog any more; dropped). `installed`: 143 programs, 44 matched to the catalog, 22 with updates. `search=notepad` works. `describe` flags Discord as per-user only |
| A3 | Install queue (§5, §3.5) | A batch of 5 curated apps installs silently on the test box, one already installed and skipped. Each result maps correctly. Cancel mid-batch stops after the current package. History entries written. **Done 2026-10-18**: `--apps-install` of 7-Zip, WinMerge, Everything, SumatraPDF (installed, 40 s total) + CPU-Z (skipped, current); app entries in `history.jsonl`. Cancel is covered by `AppQueueTests` (the UI's Cancel uses the same token) |
| A4 | Bundled-offer check (§6) | Snapshot diff unit tests pass. On the test box a package that pulls in a VC++ runtime doesn't flag, and a deliberately added extra program does. **Done 2026-10-18**: during a LibreOffice install, two Uninstall keys were added from outside: "Super Toolbar" was flagged ("Also installed: Super Toolbar (AdCorp)"), the VC++ 2015-2022 redistributable entry wasn't |
| A5 | Updates (§5.4) | An older 7-Zip installed by hand shows in Updates and upgrades. Ignore-this-version works and clears on a newer version. **Done 2026-10-18** with VLC 3.0.18 (already old on the box): ignored at 3.0.24 → not run; ignore removed → updated 3.0.18 → 3.0.24; again → nothing to do. Clearing on a newer version: `AppIgnoreTests` |
| A6 | Lists, CLI, schedule (§8) | Export → `winget import` on a second box installs the same list, and the reverse. `--apps-install` with exit codes. The scheduled pass reports app updates in its balloon. **Done 2026-10-18** on the test box (the format is what's under test, not the box): our export (49 packages) read by `winget import --no-upgrade`; `winget export` (50) read by `--apps-install --test-mode` (28 skipped, 21 downloaded and hash-checked, 1 scope failure that led to the upgrade-scope fix in §3.4). `--scan-only` wrote `AppUpdates: 21` beside 16 driver updates. Note: the winget *CLI* can't refresh its source from an SSH session (404); run it in the signed-in session |
| A7 | Curated list (§4) | Every curated app run once on the test box and given its `verified` date |

Testing rule: same as drivers, installs only on the **test box**.

---

## Clean-room notes

Ninite is closed-source. This spec copies its *behavior* (pick, one click,
silent, no offers, rerun to update), not its names, wording, app list or
artwork. The winget engine and its client code are Microsoft's MIT
open source. Reading them for API usage is allowed (CLEAN-ROOM.md permits
permissively licensed sources), and they're the sources below. The SDIO
rules in CLEAN-ROOM.md are unaffected.

## Sources

- Microsoft Learn, Windows Package Manager: *Use WinGet* (`/windows/package-manager/winget/`),
  *install*, *upgrade*, *export*, *import* commands, *Return codes*
- `microsoft/winget-cli` (MIT): `src/Microsoft.Management.Deployment/PackageManager.idl`
  (COM API: `PackageManager`, `InstallOptions`, `CatalogPackage`,
  `InstallResultStatus`, `PackageInstallScope`, `PackageInstallMode`,
  `PredefinedPackageCatalog`, `LocalPackageCatalog`, `CompositeSearchBehavior`);
  `src/PowerShell/Microsoft.WinGet.Client.Engine` (activation per caller:
  standard, administrator, SYSTEM in-process; MTA requirement);
  `doc/windows/package-manager/winget/returnCodes.md`
- NuGet: `Microsoft.WindowsPackageManager.InProcCom`, `Microsoft.WindowsPackageManager.ComInterop` (MIT)
- Installed-programs registry keys: *Uninstall Registry Key* (Windows Installer docs)
- `WTSQuerySessionInformationW` (`WTSUserName`)
