#!/usr/bin/env bash
# Build, test and publish the WPF app on the Windows build box.
#   build/build-app.sh          test + publish to publish/ on the box
#   build/build-app.sh test     tests only
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(dirname "$HERE")
BOX=${BUILD_HOST:-buildbox}
REMOTE='C:/Users/you/driverforge-dev/app'

tar -C "$ROOT/app" -cf - --exclude='*/bin' --exclude='*/obj' . |
  ssh "$BOX" "powershell -NoProfile -Command \"if (Test-Path '$REMOTE') { Get-ChildItem '$REMOTE' -Recurse -File | Where-Object { \$_.FullName -notmatch '\\\\(bin|obj|publish)\\\\' } | Remove-Item -Force }; New-Item -ItemType Directory -Force '$REMOTE' | Out-Null; tar -xf - -C '$REMOTE'\""

mode=${1:-publish}
WINPS_HOST=$BOX "$HOME/bin/winps" "
\$env:PATH = 'C:\\Program Files\\dotnet;' + \$env:PATH
\$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Set-Location '$REMOTE'
# xunit v3 test projects are their own runner.
dotnet run --project DriverForge.Tests/DriverForge.Tests.csproj -c Release 2>&1 | Select-String -Pattern 'error|\\[FAIL\\]|Assert|Expected|Actual|at DriverForge|Total:' | ForEach-Object { \$_.Line.Trim() } | Select-Object -First 40
if (\$LASTEXITCODE -ne 0) { 'TESTS FAILED'; exit 1 }
if ('$mode' -eq 'test') { exit 0 }
Remove-Item -Recurse -Force publish -ErrorAction SilentlyContinue
dotnet publish DriverForge/DriverForge.csproj -c Release -r win-x64 --self-contained -o publish 2>&1 | Select-String -Pattern 'error|warn|->' | ForEach-Object { \$_.Line.Trim() } | Select-Object -First 30
if (\$LASTEXITCODE -ne 0) { 'PUBLISH FAILED'; exit 1 }
'published: ' + (Get-ChildItem publish -Recurse -File | Measure-Object Length -Sum | ForEach-Object { '{0} files, {1:N0} MB' -f \$_.Count, (\$_.Sum/1MB) })
" 2>&1 | grep -v 'CLIXML\|^<Objs'
