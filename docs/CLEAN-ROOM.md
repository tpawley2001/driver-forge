# Clean-room rules

Driver Forge is MIT-licensed. It does what Snappy Driver Installer Origin
(SDIO, GPL-3.0) does, so its code must never come from SDIO. Ideas and
behavior aren't covered by copyright; code is. These rules keep the line clear.

## Allowed sources

- Microsoft's public documentation (see the Sources list in [SPEC.md](SPEC.md)).
- Public, permissively licensed libraries (MIT, BSD, Apache-2.0, ISC). Check
  the license before adding any dependency and list it in
  `THIRD-PARTY-NOTICES.md`.
- This repository's own code.

## Not allowed

- Reading SDIO source while working on this repo: the `engine/` folder of
  `~/sdio-modern`, the SDIO SVN or any copy of it. If something about SDIO's
  behavior matters, describe the *behavior* and find the Windows mechanism in
  Microsoft's docs.
- Copying SDIO's data formats (index `.bin` files, its JSON report schema),
  its log phrases, its scripting commands, its names or its artwork.
- Bundling SDIO's binaries, or GPL/AGPL/LGPL libraries (including 7-Zip's
  LGPL code and libtorrent builds that pull them in).

## Allowed use of SDIO as a black box

SDIO's **output** may be used as a test oracle on the test box: run the
official SDIO binary, compare which devices it finds and which drivers it
would pick with what Driver Forge finds. That compares behavior, not code.
Don't commit SDIO's output to this repo; write down the conclusions instead.

## Provenance of the starting code

The WPF front end and these Core files were carried over from the private
`~/sdio-modern` repo, where they were **new code by Hillyard Tech** (written
for that project, not derived from SDIO's source):

- `app/DriverForge/**` (views, view models, styles, icon, App/MainWindow)
- `app/DriverForge.Core/AppSettings.cs`, `RestorePoints.cs`
- `build/*.sh`, `installer/*.iss`

Not carried over, because they were tied to SDIO's engine (its report format,
its log lines, its scripting): `Engine.cs`, `Report.cs`, `InstallLog.cs`,
`Recommender.cs`, the test fixture made of SDIO output, the driver-packs page.
`Recommender.cs` and `Model.cs` were rewritten here against SPEC.md.
