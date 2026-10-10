# Driver Forge

Finds, installs, backs up and rolls back Windows device drivers. A modern
WPF app (Win11 Fluent look) over an engine built on Windows' own driver APIs,
with Windows Update and the Microsoft Update Catalog as the driver sources.

MIT License, © 2026 Hillyard Tech.

> Status: **specification + UI shell.** The front end works against demo data
> (`--demo`); the engine is built milestone by milestone, see
> [docs/SPEC.md §10](docs/SPEC.md#10-milestones).

## Documents

- [docs/SPEC.md](docs/SPEC.md): what the engine does and how, from Microsoft's documentation.
- [docs/CLEAN-ROOM.md](docs/CLEAN-ROOM.md): **read before touching the engine.** Driver
  Forge replaces an SDIO-based prototype; no SDIO code, formats or binaries may come in.
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md): every shipped dependency and its license.

## Layout

| Path | What |
|---|---|
| `app/DriverForge` | WPF front end (.NET 10, WPF-UI, CommunityToolkit.Mvvm) |
| `app/DriverForge.Core` | Engine: model, ranking, recommender, restore points; scan/sources/install to come |
| `app/DriverForge.Tests` | xunit v3 tests (the project is its own runner) |
| `build/` | build, installer and GUI-test scripts (run from Linux, build on Windows) |
| `installer/DriverForge.iss` | Inno Setup 6 script |

## Build

Builds run on the Windows build box (the build box, `buildbox`) over SSH:

| Command | Does |
|---|---|
| `build/build-app.sh test` | run the tests |
| `build/build-app.sh` | tests + self-contained publish (on the box, `publish/`) |
| `build/build-installer.sh` | the above, then `dist/DriverForge-Setup-<ver>.exe` + portable zip |
| `build/gui.sh deploy` | copy the published app to the test box (the test box) |
| `build/gui.sh appshot out.png [page] [device] [--demo]` | the app renders itself to a PNG on the test box |

**Driver installs are tested on the test box only, never on a daily-use PC.**

## App flags

| Flag | Effect |
|---|---|
| `--demo` | made-up devices and offers; installs are simulated |
| `--screenshot <png> [--page <nav label>] [--select <device>] [--test-install]` | render the window off-screen and exit |
| `--restore-point-test` | with `--screenshot`: create a restore point, write the result next to the PNG |

## Data

Portable copy: beside the exe. Installed copy (installer drops `installed.txt`):
`%ProgramData%\Driver Forge` — `settings.json`, and later `backups\`, `history.jsonl`, `cache\`.
