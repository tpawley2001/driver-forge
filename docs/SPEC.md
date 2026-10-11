# Driver Forge — engine specification

Driver Forge finds, installs, backs up and rolls back Windows device drivers.
This document specifies the **engine**: everything below the WPF front end.
It is written from Microsoft's public documentation only (listed in
[Sources](#sources)) and from the app's own requirements. Read
[CLEAN-ROOM.md](CLEAN-ROOM.md) before working on the engine.

Target: Windows 10 1809+ and Windows 11, x64 (arm64 later). .NET 10, C#.
The engine lives in `DriverForge.Core`, with no native code of its own:
Windows APIs are reached through P/Invoke and COM interop.

---

## 1. Goals and non-goals

**Goals**

1. List every device, its IDs and its current driver, exactly as Windows sees them.
2. Find better drivers from **Windows Update** and the **Microsoft Update
   Catalog** (Microsoft serves the files; Driver Forge redistributes nothing).
   Optionally also from **local folders** of drivers the user points at.
3. Pick the best driver the way Windows itself does (section 5), and never
   offer a downgrade as an update.
4. Install with a verified restore point and a backup of the driver it
   replaces, so every change can be rolled back.
5. Uninstall drivers, remove ghost (non-present) devices, scheduled scans,
   an ignore list.

**Non-goals**

- Shipping or mirroring driver files.
- Kernel-mode code, unsigned-driver tricks, disabling signature enforcement.
- Replacing vendor update tools: devices kept current by a vendor app
  (NVIDIA App, AMD Software, …) are listed but not touched (section 5.4).

---

## 2. Architecture

```
 DriverForge (WPF)                 DriverForge.Core
 ───────────────────               ─────────────────────────────────────────────
 DriversViewModel  ── scan ──────▶ IDriverEngine
 InstallViewModel  ── install ───▶   ├─ DeviceScanner        (SetupAPI / CfgMgr32)   §3
                                     ├─ IDriverSource[]                              §4
                                     │    ├─ WindowsUpdateSource  (WUA COM API)
                                     │    ├─ CatalogSource        (Update Catalog)
                                     │    └─ FolderSource         (INF parser + index)
                                     ├─ DriverRanking / Recommender                  §5
                                     ├─ DriverInstaller      (newdev / SetupAPI)     §6
                                     ├─ DriverBackup         (pnputil export)        §7
                                     └─ RestorePoints        (exists)                §6.2
```

`IDriverEngine` is the only thing the UI talks to:

```csharp
public interface IDriverEngine
{
    string Name { get; }
    Task<ScanResult> ScanAsync(IProgress<string>? progress, CancellationToken ct);
    Task<InstallResult> InstallAsync(Device device, DriverOffer offer, InstallOptions options,
                                     IProgress<InstallStep>? progress, CancellationToken ct);
}
```

A scan returns the devices plus every **offer** (a driver some source can
provide for a device). The Recommender (section 5) turns that into what the
UI shows. Data types are in `DriverForge.Core/Model.cs`.

---

## 3. Device scan

### 3.1 Enumeration

- `SetupDiGetClassDevsW(NULL, NULL, NULL, DIGCF_ALLCLASSES | DIGCF_PRESENT)`
  for present devices; without `DIGCF_PRESENT` for ghost devices (section 8.2).
- Per device (`SetupDiEnumDeviceInfo`), read with `SetupDiGetDevicePropertyW`
  (unified device property model):

| Field | Property key |
|---|---|
| Instance ID | `SetupDiGetDeviceInstanceIdW` |
| Name | `DEVPKEY_NAME` (falls back to `DEVPKEY_Device_DeviceDesc`) |
| Description | `DEVPKEY_Device_DeviceDesc` |
| Manufacturer | `DEVPKEY_Device_Manufacturer` |
| Class / ClassGuid | `DEVPKEY_Device_Class`, `DEVPKEY_Device_ClassGuid` |
| Hardware IDs | `DEVPKEY_Device_HardwareIds` (ordered, most specific first) |
| Compatible IDs | `DEVPKEY_Device_CompatibleIds` (ordered) |
| Problem code / status | `CM_Get_DevNode_Status` |
| Installed driver | `DEVPKEY_Device_DriverInfPath`, `…DriverVersion`, `…DriverDate`, `…DriverProvider`, `…DriverDesc`, `…MatchingDeviceId`, `…DriverRank`, `…DriverInfSection` |

- ID lists are kept **in order**: position feeds the rank (section 5.1).
- A device **needs a driver** when it has problem code 28
  (`CM_PROB_FAILED_INSTALL`, no driver installed), 1 (`CM_PROB_NOT_CONFIGURED`)
  or 18 (`CM_PROB_REINSTALL`), or no driver INF together with any other
  problem code. No INF and no problem is a working device (the device tree
  root `HTREE\ROOT\0`, raw and software devices); Device Manager shows no
  warning for those either.

### 3.2 OS facts

Collected once per scan, needed for INF decoration matching (section 4.3.3):
architecture (`amd64`/`arm64`), major/minor (`RtlGetVersion`), build number,
product type (`VER_NT_WORKSTATION` etc), suite mask.

---

## 4. Driver sources

```csharp
public interface IDriverSource
{
    string Name { get; }
    Task<IReadOnlyList<DriverOffer>> FindAsync(IReadOnlyList<Device> devices, OsInfo os, CancellationToken ct);
    // Make the offer's driver package available locally; returns the INF path.
    Task<string> AcquireAsync(DriverOffer offer, IProgress<InstallStep>? progress, CancellationToken ct);
}
```

Sources run in parallel; one failing (offline, throttled) never fails the
scan, it adds a warning to `ScanResult.Warnings`.

### 4.1 Windows Update (primary)

Windows Update Agent COM API (`Microsoft.Update.Session`):

1. `IUpdateSearcher.Search("IsInstalled=0 and Type='Driver'")`, online.
   This includes optional driver updates that Windows doesn't install on its own.
2. Each result is an `IWindowsDriverUpdate`: use `DriverHardwareID`,
   `DriverVerDate`, `DriverClass`, `DriverManufacturer`, `DriverProvider`,
   `DriverModel`, plus `Identity.UpdateID` as the offer ID. Map it to devices
   by hardware ID (and `DeviceProblemNumber`/`DriverMatchingID` when set).
3. Acquire and install go through WUA itself (`IUpdateDownloader`,
   `IUpdateInstaller`), not through section 6, because WU installs the package
   it validated. Result codes come from `IInstallationResult`
   (`ResultCode`, `RebootRequired`, `HResult`).
4. WU offers are signed by Microsoft by definition (signature score best).
   WUA doesn't report a driver's version or feature score: the version comes
   from the title's last token when it is one (`"INTEL - System - … - 10.1.1.40"`),
   else it's unknown; the feature score is the default.
5. WU also offers drivers for devices that aren't present (seen before);
   those match no scanned device and are dropped.

Note from Microsoft's docs: since Windows 10 1709, WU offers the *best
matching* driver, not necessarily the newest; it ranks critical/automatic
above optional. We show what WU offers and still apply section 5 against the
installed driver.

### 4.2 Microsoft Update Catalog (secondary)

For devices WU has nothing for, search the Catalog by hardware ID:

- Search: `https://www.catalog.update.microsoft.com/Search.aspx?q=<hwid>`,
  HTML table of results (title, products, classification, last updated,
  version, size, update ID).
- Download links: `POST https://www.catalog.update.microsoft.com/DownloadDialog.aspx`
  with the update ID; the response contains the `.cab` URL(s).
- Query the most specific hardware ID first and stop at the first one with
  results; never query compatible IDs (far too broad, and slow).
- Polite client: one request at a time, a cache of results for 24 h
  (`%ProgramData%\Driver Forge\cache\catalog`), a real User-Agent.
- Acquire: download the `.cab`, verify its Authenticode signature
  (`WinVerifyTrust`), expand with the built-in `expand.exe -F:* <cab> <dir>`,
  then rank the INFs inside with section 5.3 before offering them.
- The HTML layout is not a documented API. The parser must fail softly
  (warning, no offers) and have fixture tests from saved pages.

### 4.3 Local folders (optional)

The user can add folders that hold extracted driver packages (for example an
OEM's driver download, or a USB stick of drivers). Archives (`.zip`, `.7z`,
`.cab`) can be read with a permissively licensed library (SharpCompress, MIT)
or `expand.exe` for cabs.

#### 4.3.1 Index

Parsing thousands of INFs on every scan is slow, so each folder gets an index
(`index.json` per folder, rebuilt when a file's size or mtime changes):
INF path → `[Version]` facts plus every (models-section decoration, hardware
or compatible ID, DDInstall section) entry. Matching a device is then a
dictionary lookup on uppercase IDs.

#### 4.3.2 INF parsing (per *General Syntax Rules for INF Files*)

- Encoding: UTF-16 LE with BOM, UTF-8 with BOM, or ANSI (code page 1252 as
  the fallback).
- Lines: `;` starts a comment outside quotes; `\` at the end of a line
  continues it; section names are `[name]`, case-insensitive, and a section
  may appear more than once (the contents merge).
- Values are comma-separated; quoted strings keep commas; `""` is a quote.
- `%token%` substitution from `[Strings]` and the localized `[Strings.<LCID>]`
  matching the user's language (fall back to `[Strings]`); `%%` is a literal `%`.
- `[Version]`: `Signature`, `Class`, `ClassGuid`, `Provider`,
  `DriverVer = mm/dd/yyyy[,w.x.y.z]`, `CatalogFile` (and its `.NTamd64`
  variants), `PnpLockdown`.
- `[Manufacturer]` → models sections, see 4.3.3.
- Models section lines: `device-description = install-section-name, hw-id [, compatible-id, ...]`.
- DDInstall section: the install section name with the most specific platform
  extension present (`.NTamd64`, then `.NT`, then none); read `FeatureScore`
  from it.

#### 4.3.3 TargetOSVersion decorations (per *INF Manufacturer Section*)

Format: `NT[Architecture][.[OSMajorVersion][.[OSMinorVersion][.[ProductType][.[SuiteMask][.[BuildNumber]]]]]]`.

Choosing a models section for this PC:

1. Drop decorations whose architecture is set and differs from ours.
2. Drop decorations whose version is higher than the OS. A BuildNumber is
   only compared when major.minor equals the OS exactly; if the OS
   major.minor is higher, the build is ignored.
3. Drop decorations whose ProductType differs, or whose SuiteMask isn't fully
   contained in ours.
4. Of what's left, take the highest version (major, minor, build); among
   equal versions, prefer the one with ProductType/SuiteMask set.
5. Undecorated `[Models]` applies only when no decoration is listed for
   that manufacturer line, or as the x86 fallback (not on amd64/arm64).
6. An **empty** chosen models section means "not supported on this OS":
   produce no offers from that manufacturer line.

### 4.4 Already in the driver store

Packages already staged in the driver store (`pnputil /enum-drivers`, or
`SetupDiBuildDriverInfoList` with `SPDIT_COMPATDRIVER` on the device) can
produce offers too. They cost nothing to acquire. Source name: "This PC".

---

## 5. Ranking and recommendation

### 5.1 Rank (per *How Windows Ranks Driver Packages*)

A rank is a DWORD `0xSSGGTHHH`; **lower is better**.

- **Signature score `SS`.** Trusted signature (WHQL, inbox, Windows SE,
  valid Authenticode) best; then unsigned with a `.NT` DDInstall section;
  then unsigned without; unknown is `0xFF`. Since Windows 7 the
  **AllSignersEqual** policy is on by default, so all trusted signatures rank
  the same.
- **Feature score `GG`.** `FeatureScore` from the DDInstall section;
  default `0xFF`.
- **Identifier score `THHH`.**

| Device ID | INF entry ID | T | HHH |
|---|---|---|---|
| hardware ID #n | hardware ID | 0 | n |
| hardware ID #n | compatible ID | 1 | n |
| compatible ID #n | hardware ID | 2 | n |
| compatible ID #j | INF compatible ID #k | 3 | j + k·0x100 |

**The authority is Windows itself.** Where possible (local folder, Catalog
cab, driver store), get the real rank from SetupAPI:
`SetupDiBuildDriverInfoList` for the device with `DI_ENUMSINGLEINF` and
`DriverPath` = the INF, then `SetupDiGetDriverInstallParamsW` →
`SP_DRVINSTALL_PARAMS.Rank`. The computed rank (above) is what the folder
index uses to shortlist; SetupAPI confirms it before an offer is shown.
`DriverRank.Compute` in Core implements the table above and is unit-tested.

### 5.2 Selection (per *Overview of the Driver Package Selection Process*)

Among offers for one device, the best is:

1. the lowest rank;
2. on equal rank, the newest `DriverVer` date;
3. on equal date, the highest `DriverVer` version (four 16-bit parts, compared
   numerically);
4. otherwise, any.

`DriverComparer` in Core implements this order.

### 5.3 Is it an update?

Compare the best offer with the installed driver, using the installed
driver's real rank (`DEVPKEY_Device_DriverRank`), date and version:

- **Missing**: the device needs a driver (3.1) and an offer exists.
- **Update**: the offer beats the installed driver by 5.2 **and** its date is
  not older than the installed one. The date guard exists because a closer ID
  match can rank an old driver above a newer installed one; Driver Forge
  never offers a downgrade as an update.
- **Skipped**: the device is ignored, is vendor-managed (5.5) or firmware (5.4), or its best
  offer ranks at least as well as the installed driver but is older (the
  reason says so). A worse-ranked older offer, such as an inbox generic
  driver in the driver store, is no news: the device is up to date.
- **No driver found**: needs a driver, no offers.
- **Up to date**: everything else.

Every offer stays visible in the device's detail pane, and the user may pick
any of them by hand (including an older one: that's how a rollback to a
specific version works).

### 5.4 Firmware

Devices of class `Firmware` (UEFI system and device firmware, offered by WU
as "drivers") are Skipped whenever an offer exists: installing one flashes
firmware. The user can still pick it by hand.

### 5.5 Vendor-managed devices

Display adapters from vendors whose own app keeps them current are Skipped
unless the user picks a driver by hand: `PCI\VEN_10DE` (NVIDIA App),
`PCI\VEN_1002` (AMD Software). The list is data in Core, easy to extend.

---

## 6. Install

### 6.1 Steps per device

1. **Acquire** (`IDriverSource.AcquireAsync`): download and extract.
2. **Back up** the current driver (section 7) unless the device has none.
3. **Install**:
   - WU offers: WUA installer (4.1).
   - Everything else: `DiInstallDriverW(hwnd, infPath, 0, &reboot)` stages the package
     and installs it on the devices where Windows ranks it best. To target **one**
     device with a specific package (needed when the package isn't Windows'
     own best pick, for example a hand-picked older driver):
     `SetupDiBuildDriverInfoList` with `DI_ENUMSINGLEINF` on that device,
     pass that driver node to `DiInstallDevice(hwnd, set, device, node, 0, &reboot)`
     (a non-null node installs exactly that driver). The package must be in
     the driver store first: `SetupCopyOEMInfW(inf, NULL, SPOST_PATH, 0, …)`
     stages it and returns its `oemNN.inf` (packages already under
     `%windir%\INF` skip this).
   - Reboot flag: `NeedsRestart` = the API's reboot out-param, or
     `ERROR_SUCCESS_REBOOT_REQUIRED`.
4. **Verify**: re-read the device's driver properties; the install succeeded
   only if `DriverInfPath` now names the new package (an `oem*.inf`) and the
   problem code is 0 (or a restart is pending).

Install runs elevated (the app manifest requires administrator).

### 6.2 Restore point

Already implemented and verified (`RestorePoints.cs`): Windows reports success
even when it silently skips the point (24-hour rule applied after the call
returns), so the code lifts `SystemRestorePointCreationFrequency`, creates
the point through WMI, and waits until it's listed.

### 6.3 Test mode

Every step runs except 6.1.3. The UI marks results "Simulated". Instead of
installing, test mode checks with `DI_ENUMSINGLEINF` that the package really
has the chosen driver for the device. WU offers stop after the download.

---

## 7. Backup, history, rollback

- Before replacing a driver: `pnputil /export-driver <oemNN.inf> <dir>`, into
  `%ProgramData%\Driver Forge\backups\<yyyyMMdd-HHmmss>-<oemNN>\`. Inbox
  drivers (non-`oem*.inf`) aren't exported: they're always in Windows.
- History is an append-only JSON-lines log (`history.jsonl`): time, device
  instance ID and name, from (inf, version, date), to (inf, version, date),
  source, result, backup folder.
- Rollback = install the backup folder's INF on that device with 6.1.3's
  specific-driver path.

## 8. Cleanup

### 8.1 Uninstall a driver

`pnputil /delete-driver oemNN.inf /uninstall` (removes it from the devices
and the driver store). Refuse for inbox drivers. Back up first.

### 8.2 Ghost devices

Non-present devices (enumerated without `DIGCF_PRESENT`, where
`CM_Get_DevNode_Status` returns `CR_NO_SUCH_DEVINST`) are listed with their
last-seen time (`DEVPKEY_Device_LastArrivalDate` / `LastRemovalDate`).
Removing one: `DiUninstallDevice`. Never offered in bulk by default for
classes that are present-only by nature (software devices, volume
snapshots); show them, don't pre-select them.

## 9. Scheduling and settings

- Scheduled scan: a Task Scheduler task (`schtasks` or the COM API) that runs
  `DriverForge.exe --scan-only` and shows a toast when something needs
  attention. A tray icon is optional.
- Settings, ignore list: `settings.json` (exists, `AppSettings.cs`).
- Data folder: beside the exe for the portable copy, `%ProgramData%\Driver Forge`
  when installed (marker file `installed.txt`, exists).

---

## 10. Milestones

| # | Milestone | Done when |
|---|---|---|
| 1 | Device scan (§3) | Device list on the test box matches Device Manager, IDs in order, installed-driver fields filled. **Done 2026-10-10**: `build/check-scan.sh` matches WMI's PnP view on the test box exactly (155 devices) |
| 2 | Ranking (§5) | `DriverRank`/`DriverComparer`/`Recommender` tests pass; real SetupAPI rank read for staged drivers (§4.4). **Done 2026-10-10**: `DriverNodes` reads Windows' rank; the computed identifier score equals Windows' on every staged node (live test) |
| 3 | Windows Update source (§4.1) | Optional driver updates listed and one installed on the test box. **Done 2026-10-10**: Intel thermal subsystem (problem 28) installed from WU on the test box, now oem32.inf 10.1.1.12, no problem |
| 4 | Install + backup + history (§6, §7) | Install, verify, roll back one device on the test box. **Done 2026-10-10**: on the test box the ISA bridge went machine.inf → oem31.inf (driver store offer), rolled back to machine.inf (oem31 exported first), then back to oem31 from the backup; each verified with PnP properties |
| 5 | Catalog source (§4.2) | A device WU has nothing for gets a Catalog offer, installed from the cab |
| 6 | Local folders + INF parser (§4.3) | INF fixture tests pass; a folder of OEM drivers produces offers |
| 7 | Cleanup, scheduling (§8, §9) | |

Testing rule: driver installs only on the **test box**, never on a daily-use PC.

---

## Sources

All from Microsoft Learn (learn.microsoft.com), Windows driver docs:

- *How Windows Ranks Driver Packages* — `/windows-hardware/drivers/install/how-windows-ranks-driver-packages`
- *Signature Score*, *Feature Score*, *Identifier Score* — `/windows-hardware/drivers/install/signature-score--windows-vista-and-later-` (and `feature-score--…`, `identifier-score--…`)
- *Overview of the Driver Package Selection Process* — `/windows-hardware/drivers/install/overview-of-the-driver-selection-process`
- *How Windows Selects a Driver Package for a Device* — `/windows-hardware/drivers/install/how-windows-selects-a-driver-for-a-device`
- *INF Manufacturer Section* — `/windows-hardware/drivers/install/inf-manufacturer-section`
- *INF Models Section*, *INF Version Section*, *INF Strings Section*, *INF DDInstall Section*, *INF DriverVer Directive*, *INF FeatureScore Directive*, *General Syntax Rules for INF Files*
- *Hardware IDs*, *Compatible IDs*, *Device Identification Strings*
- SetupAPI / CfgMgr32 / newdev reference: `SetupDiGetClassDevsW`, `SetupDiGetDevicePropertyW`, `SetupDiBuildDriverInfoList`, `SetupDiGetDriverInstallParamsW`, `DiInstallDriverW`, `DiInstallDevice`, `DiUninstallDevice`, `CM_Get_DevNode_Status`
- *Device Manager Error Messages* (problem codes)
- *PnPUtil Command Syntax*
- Windows Update Agent API: `IUpdateSearcher`, `IWindowsDriverUpdate`, `IUpdateDownloader`, `IUpdateInstaller`
- System Restore WMI: `SystemRestore.CreateRestorePoint`
