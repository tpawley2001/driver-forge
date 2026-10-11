# Driver Forge

Finds, installs, backs up and rolls back Windows device drivers. A modern
WPF app (Windows 11 Fluent look) over an engine built on Windows' own driver
APIs. Windows itself decides which driver fits each device; Driver Forge
finds the candidates and does the work around the install.

MIT License, © 2026 Hillyard Tech.

## What it does

- **Scans** every device through SetupAPI and shows which ones are missing a
  driver or have a newer one available.
- **Driver sources:** Windows Update (optional driver updates included), the
  Microsoft Update Catalog, the drivers already in this PC's driver store, and
  folders you add (an OEM's download, a USB stick; `.zip`, `.cab` and `.7z`
  archives inside are read).
- **Safe matching:** every offer is checked by Windows against the device
  before it is shown. Catalog and folder drivers aren't targeted to a PC
  model the way Windows Update's are, so for a device that already works only
  same-maker drivers are recommended. It never offers a downgrade.
- **Install with a way back:** a restore point first, the old driver backed
  up, the result verified afterwards, and one-click rollback from the history.
- **Cleanup:** uninstall a third-party driver (backed up first, so it rolls
  back too) and remove devices Windows remembers that aren't connected.
- **System info:** model, board, BIOS/UEFI and Secure Boot, Windows, CPU,
  memory, graphics, disks with health, laptop battery wear, and every driver
  by install date. Copies as text without serial numbers.
- **Scheduled scans** that notify you when something needs attention.

## Install

Download `DriverForge-Setup-<version>.exe` (or the portable zip) from
[Releases](../../releases). It needs Windows 10 1809 or later, x64, and runs
as administrator because installing drivers does.

The installer isn't code-signed, so Windows SmartScreen will warn that it's
from an unknown publisher: choose **More info → Run anyway**. Every release
lists the SHA-256 of its files so you can check the download first
(`Get-FileHash .\DriverForge-Setup-<version>.exe` in PowerShell).

Try it without touching anything: run with `--demo` for made-up devices, or
turn on **test mode** in Settings, which goes through every step of an
install except the install itself.

## Documents

- [docs/SPEC.md](docs/SPEC.md): what the engine does and how, from Microsoft's documentation.
- [docs/CLEAN-ROOM.md](docs/CLEAN-ROOM.md): **read before touching the engine.** Driver
  Forge replaces an SDIO-based prototype; no SDIO code, formats or binaries may come in.
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md): every shipped dependency and its license.

## Layout

| Path | What |
|---|---|
| `app/DriverForge` | WPF front end (.NET 10, WPF-UI, CommunityToolkit.Mvvm) |
| `app/DriverForge.Core` | Engine: scan, sources, ranking, recommender, install, backup, history, cleanup, system info |
| `app/DriverForge.Tests` | xunit v3 tests (the project is its own runner) |
| `build/` | build, installer and GUI-test scripts (run from Linux, build on Windows) |
| `installer/DriverForge.iss` | Inno Setup 6 script |

## Build

On Windows: `dotnet run --project app/DriverForge.Tests -c Release` runs the
tests, `dotnet publish app/DriverForge -c Release -r win-x64 --self-contained`
builds the app.

The scripts in `build/` drive two Windows boxes over SSH from Linux: a build
box (.NET 10 SDK, Inno Setup 6) and a test box for real driver installs.
Copy `build/hosts.env.example` to `build/hosts.env` and name them there.

| Command | Does |
|---|---|
| `build/build-app.sh test` | run the tests |
| `build/build-app.sh` | tests + self-contained publish (on the build box, `publish/`) |
| `build/build-installer.sh` | the above, then `dist/DriverForge-Setup-<ver>.exe` + portable zip |
| `build/gui.sh deploy` | copy the published app to the test box |
| `build/gui.sh appshot out.png [page] [device] [--demo]` | the app renders itself to a PNG on the test box |
| `build/check-scan.sh` | compare the device scan with Windows' WMI view on the test box |

**Driver installs are tested on the test box only, never on a daily-use PC.**

## App flags

| Flag | Effect |
|---|---|
| `--demo` | made-up devices and offers; installs are simulated |
| `--screenshot <png> [--page <nav label>] [--select <device>] [--test-install]` | render the window off-screen and exit |
| `--restore-point-test` | with `--screenshot`: create a restore point, write the result next to the PNG |
| `--scan-only` | scan with no window, write `last-scan.json`, notify if something needs attention (what the scheduled task runs) |
| `--schedule Off\|Daily\|Weekly` | set the scheduled scan and exit |
| `--scan-json <file>` | write the full scan (devices and offers) as JSON and exit |
| `--real-install`, `--rollback <device>`, `--uninstall <device>`, `--remove-ghost <id>` | with `--screenshot`: automation for the test box |

## Data

Portable copy: beside the exe. Installed copy (installer drops `installed.txt`):
`%ProgramData%\Driver Forge` — `settings.json`, `backups\`, `history.jsonl`, `cache\`.
