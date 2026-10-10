#!/usr/bin/env bash
# Build the Windows installer on the build box and bring the results back here:
#   dist/DriverForge-Setup-<version>.exe   the installer
#   dist/DriverForge-<version>-portable.zip  the same app, run-from-anywhere
# Runs build/build-app.sh first (tests + publish).
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(dirname "$HERE")
BOX=${BUILD_HOST:-buildbox}
REMOTE='C:/Users/you/driverforge-dev/installer'
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/app/DriverForge/DriverForge.csproj")

"$HERE/build-app.sh"

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
cp "$ROOT/installer/DriverForge.iss" "$ROOT/app/DriverForge/Assets/app.ico" "$stage/"
cp "$ROOT/LICENSE" "$stage/LICENSE.txt"
printf 'Installed by the Driver Forge installer. Data lives in C:\\ProgramData\\Driver Forge.\r\n' > "$stage/installed.txt"
tar -C "$stage" -cf - . |
  ssh "$BOX" "powershell -NoProfile -Command \"Remove-Item -Recurse -Force '$REMOTE' -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force '$REMOTE' | Out-Null; tar -xf - -C '$REMOTE'\""

WINPS_HOST=$BOX "$HOME/bin/winps" "
Set-Location '$REMOTE'
& \"\$env:LOCALAPPDATA\\Programs\\Inno Setup 6\\ISCC.exe\" /Q /DAppVersion=$VERSION /DPublishDir=C:\\Users\\you\\driverforge-dev\\app\\publish DriverForge.iss 2>&1 | ForEach-Object { \"\$_\" }
if (\$LASTEXITCODE -ne 0) { 'ISCC FAILED'; exit 1 }
Compress-Archive -Path C:\\Users\\you\\driverforge-dev\\app\\publish\\* -DestinationPath output\\DriverForge-$VERSION-portable.zip -Force
Get-ChildItem output | ForEach-Object { '{0}  {1:N1} MB' -f \$_.Name, (\$_.Length/1MB) }
" 2>&1 | grep -v '^#< CLIXML\|^<Objs'

mkdir -p "$ROOT/dist"
scp -q "$BOX:driverforge-dev/installer/output/DriverForge-Setup-$VERSION.exe" "$BOX:driverforge-dev/installer/output/DriverForge-$VERSION-portable.zip" "$ROOT/dist/"
ls -l "$ROOT/dist"
