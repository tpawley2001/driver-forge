#!/usr/bin/env bash
# Milestone A7: check every curated app's silent install on the test box.
#   build/apps-verify.sh start [id...]   copy the build box's publish/ to its own folder
#                                        on the test box and start the run (all curated
#                                        IDs, or the ones named)
#   build/apps-verify.sh status          print verify.jsonl so far
# The run lives in its own folder and scheduled task, so `gui.sh deploy` of
# the UI doesn't touch it.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
. "$HERE/common.sh"
ROOT=$(dirname "$HERE")
A7="$TEST_DIR\\a7"

case "${1:-}" in
  start)
    shift
    if [ $# -gt 0 ]; then printf '%s\n' "$@"; else
      python3 -c 'import json,re,sys; t=re.sub(r"^\s*//.*$","",open(sys.argv[1]).read(),flags=re.M); [print(a["id"]) for a in json.loads(t)["apps"]]' \
        "$ROOT/app/DriverForge.Core/Apps/catalog.json"
    fi > /tmp/apps-verify-ids.$$
    winps "$TEST_HOST" "
Get-Process DriverForge -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '$A7\\*' } | Stop-Process -Force
Remove-Item '$A7' -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force '$A7' | Out-Null" 2>&1 | grep -v '^#< CLIXML\|^<Objs' || true
    ssh "$BUILD_HOST" "tar -cf - -C $BUILD_DIR/app/publish ." | ssh "$TEST_HOST" "tar -xf - -C C:/Users/$WIN_USER/driverforge-test/a7"
    scp -q "$HERE/apps-verify.ps1" "$TEST_HOST:driverforge-test/a7/apps-verify.ps1"
    scp -q /tmp/apps-verify-ids.$$ "$TEST_HOST:driverforge-test/a7/ids.txt"; rm -f /tmp/apps-verify-ids.$$
    winps "$TEST_HOST" "
\$a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -ExecutionPolicy Bypass -File $A7\\apps-verify.ps1 -App $A7 -Ids $A7\\ids.txt' -WorkingDirectory '$A7'
\$p = New-ScheduledTaskPrincipal -UserId '$WIN_USER' -LogonType Interactive -RunLevel Highest
\$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 6)
Register-ScheduledTask -TaskName DriverForgeAppsVerify -Action \$a -Principal \$p -Settings \$s -Force | Out-Null
Start-ScheduledTask -TaskName DriverForgeAppsVerify
'started: ' + (Get-Content '$A7\\ids.txt').Count + ' apps'" 2>&1 | grep -v '^#< CLIXML\|^<Objs' ;;
  status)
    ssh "$TEST_HOST" "type driverforge-test\\a7\\verify.jsonl" 2>/dev/null || echo "(no results yet)" ;;
  *) sed -n '2,8p' "$0"; exit 1 ;;
esac
