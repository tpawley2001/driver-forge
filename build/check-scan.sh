#!/usr/bin/env bash
# Milestone 1 check (docs/SPEC.md §10): compare Driver Forge's device scan on
# the test box with Windows' own view through WMI (Win32_PnPEntity and
# Win32_PnPSignedDriver), a separate path to the same PnP data.
#   build/check-scan.sh        (deploy first: build/gui.sh deploy)
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT
T='C:\Users\you\driverforge-test'

"$HOME/bin/winps" "
Remove-Item '$T\\scan.json','$T\\scan.json.error.txt','$T\\wmi.json' -ErrorAction SilentlyContinue
\$p = Start-Process '$T\\app\\DriverForge.exe' -ArgumentList '--scan-json','$T\\scan.json' -PassThru -Wait
'exit ' + \$p.ExitCode
\$drv = @{}
Get-CimInstance Win32_PnPSignedDriver | Where-Object DeviceID | ForEach-Object { \$drv[\$_.DeviceID] = \$_ }
Get-CimInstance Win32_PnPEntity | Where-Object Present | ForEach-Object {
  \$d = \$drv[\$_.PNPDeviceID]
  [pscustomobject]@{
    id = \$_.PNPDeviceID; name = \$_.Name; cls = \$_.PNPClass; problem = [int]\$_.ConfigManagerErrorCode
    hw = @(\$_.HardwareID); compat = @(\$_.CompatibleID)
    inf = \$d.InfName; ver = \$d.DriverVersion; date = if (\$d.DriverDate) { \$d.DriverDate.ToUniversalTime().ToString('yyyy-MM-dd') } else { \$null }
  }
} | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 '$T\\wmi.json'
" 2>&1 | grep -v 'CLIXML\|^<Objs' || true

scp -q testbox:driverforge-test/wmi.json "$OUT/wmi.json"
if ! scp -q testbox:driverforge-test/scan.json "$OUT/scan.json" 2>/dev/null; then
  scp -q testbox:driverforge-test/scan.json.error.txt /dev/stdout || echo "no scan output"; exit 1
fi
python3 -I "$HERE/compare-scan.py" "$OUT/scan.json" "$OUT/wmi.json"
