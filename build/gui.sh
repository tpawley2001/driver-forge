#!/usr/bin/env bash
# Drive the app's GUI on the test box (TEST_HOST, see common.sh), whose console session is
# signed in. SSH runs in session 0, which has no desktop, so launches and
# screenshots go through scheduled tasks in the user's interactive session.
#   build/gui.sh launch            start DriverForge.exe (elevated, no UAC prompt)
#   build/gui.sh shot out.png      screenshot the desktop to a local file
#   build/gui.sh stop              close the app
#   build/gui.sh deploy            copy the build box's publish/ over (keeps settings and data)
#   build/gui.sh appshot out.png [page] [device] [--test-install|--real-install] [--demo|--restore-point-test]
#                                  the app renders itself off-screen to a PNG
set -euo pipefail
. "$(dirname "$0")/common.sh"
TMP=$TEST_DIR
APP=${APP:-"$TMP\\app"}

run_task() {  # name, exe, args, highest(0/1)
  # -Argument '' is rejected, so pass it only when there are arguments.
  local argpart=""
  [ -n "$3" ] && argpart="-Argument '$3'"
  winps "$TEST_HOST" "
try {
\$a = New-ScheduledTaskAction -Execute '$2' $argpart -WorkingDirectory '$APP'
\$p = New-ScheduledTaskPrincipal -UserId '$WIN_USER' -LogonType Interactive -RunLevel $([ "$4" = 1 ] && echo Highest || echo Limited)
Register-ScheduledTask -TaskName '$1' -Action \$a -Principal \$p -Force -ErrorAction Stop | Out-Null
Start-ScheduledTask -TaskName '$1' -ErrorAction Stop
} catch { 'ERROR: ' + \$_.Exception.Message }
" 2>&1 | grep -v '^#< CLIXML\|^<Objs' || true
}

case "${1:-}" in
  launch)
    run_task DriverForgeGui "$APP\\DriverForge.exe" "" 1
    echo launched ;;
  shot)
    out=${2:?local png path}
    winps "$TEST_HOST" "
Set-Content -Encoding UTF8 '$TMP\\shot.ps1' @'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport(\"user32.dll\")] public static extern bool SetProcessDPIAware();'
[W.U]::SetProcessDPIAware() | Out-Null
\$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
\$bmp = New-Object System.Drawing.Bitmap \$b.Width, \$b.Height
\$g = [System.Drawing.Graphics]::FromImage(\$bmp)
\$g.CopyFromScreen(\$b.Left, \$b.Top, 0, 0, \$bmp.Size)
\$bmp.Save('$TMP\\shot.tmp.png', [System.Drawing.Imaging.ImageFormat]::Png)
Move-Item -Force '$TMP\\shot.tmp.png' '$TMP\\shot.png'
'@
Remove-Item '$TMP\\shot.png' -ErrorAction SilentlyContinue
" 2>&1 | grep -v 'CLIXML\|^<Objs' || true
    run_task DriverForgeShot powershell.exe "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File $TMP\\shot.ps1" 0
    for _ in $(seq 1 20); do
      if scp -q "$TEST_HOST:driverforge-test/shot.png" "$out" 2>/dev/null; then echo "saved $out"; exit 0; fi
      sleep 1
    done
    echo "no screenshot" >&2; exit 1 ;;
  window)
    # Capture just the app's window (PrintWindow renders it even when covered),
    # so nothing on the user's desktop has to move.
    out=${2:?local png path}
    winps "$TEST_HOST" "
Set-Content -Encoding UTF8 '$TMP\\win.ps1' @'
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace W -Name U -MemberDefinition @\"
[DllImport(\"user32.dll\")] public static extern bool SetProcessDPIAware();
[DllImport(\"user32.dll\")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
[DllImport(\"user32.dll\")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
public struct RECT { public int L, T, R, B; }
\"@
[W.U]::SetProcessDPIAware() | Out-Null
\$h = (Get-Process DriverForge -ErrorAction Stop | Select-Object -First 1).MainWindowHandle
\$r = New-Object W.U+RECT
[W.U]::GetWindowRect(\$h, [ref]\$r) | Out-Null
\$bmp = New-Object System.Drawing.Bitmap (\$r.R - \$r.L), (\$r.B - \$r.T)
\$g = [System.Drawing.Graphics]::FromImage(\$bmp)
\$dc = \$g.GetHdc(); [W.U]::PrintWindow(\$h, \$dc, 2) | Out-Null; \$g.ReleaseHdc(\$dc)
\$bmp.Save('$TMP\\win.tmp.png', [System.Drawing.Imaging.ImageFormat]::Png)
Move-Item -Force '$TMP\\win.tmp.png' '$TMP\\win.png'
'@
Remove-Item '$TMP\\win.png' -ErrorAction SilentlyContinue
" 2>&1 | grep -v 'CLIXML\\|^<Objs' || true
    run_task DriverForgeShot powershell.exe "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File $TMP\\win.ps1" 0
    for _ in $(seq 1 20); do
      if scp -q "$TEST_HOST:driverforge-test/win.png" "$out" 2>/dev/null; then echo "saved $out"; exit 0; fi
      sleep 1
    done
    echo "no window capture" >&2; exit 1 ;;
  run)
    # build/gui.sh run "<args>" [file...]: run DriverForge.exe with args in the
    # signed-in session, elevated, wait for it to exit, print its exit code and
    # then each named file from the app folder (e.g. last-apps.json).
    args=${2:?arguments}; shift 2
    run_task DriverForgeRun "$APP\\DriverForge.exe" "$args" 1
    winps "$TEST_HOST" "
Start-Sleep 2
for (\$i = 0; \$i -lt 3600 -and (Get-ScheduledTask -TaskName DriverForgeRun).State -eq 'Running'; \$i++) { Start-Sleep 1 }
'exit ' + (Get-ScheduledTaskInfo -TaskName DriverForgeRun).LastTaskResult
" 2>&1 | grep -v '^#< CLIXML\|^<Objs' || true
    # ssh+type, not scp to /dev/stdout: scp truncates a redirected stdout.
    for f in "$@"; do echo "== $f"; ssh "$TEST_HOST" "type driverforge-test\\app\\$f" 2>/dev/null || echo "(missing)"; done ;;
  apps-probe)
    # Milestone A1 (docs/APPS.md §10): run --apps-probe in the signed-in
    # session, elevated like the real app, and print its log.
    id=${2:?winget package id}
    winps "$TEST_HOST" "Remove-Item '$APP\\apps-probe.log' -ErrorAction SilentlyContinue" 2>&1 | grep -v '^#< CLIXML\|^<Objs' || true
    run_task DriverForgeAppsProbe "$APP\\DriverForge.exe" "--apps-probe $id ${3:-}" 1
    for _ in $(seq 1 600); do
      if ssh "$TEST_HOST" "type driverforge-test\\app\\apps-probe.log" 2>/dev/null; then exit 0; fi
      sleep 1
    done
    echo "no probe log after 10 minutes" >&2; exit 1 ;;
  deploy)
    # Replace the app but keep its settings and data.
    winps "$TEST_HOST" "
Get-Process DriverForge -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '$APP\\*' } | Stop-Process -Force
Get-ChildItem '$APP' -Force -ErrorAction SilentlyContinue | Where-Object { \$_.Name -notin 'settings.json','backups','cache','history.jsonl' } | Remove-Item -Recurse -Force
New-Item -ItemType Directory -Force '$APP' | Out-Null
" 2>&1 | grep -v '^#< CLIXML\|^<Objs' || true
    ssh "$BUILD_HOST" "tar -cf - -C $BUILD_DIR/app/publish ." | ssh "$TEST_HOST" "tar -xf - -C C:/Users/$WIN_USER/driverforge-test/app"
    echo deployed ;;
  appshot)
    out=${2:?local png path}
    args="--screenshot $TMP\\app.png"
    [ -n "${3:-}" ] && args="$args --page \"${3}\""
    [ -n "${4:-}" ] && args="$args --select \"${4}\""
    [ -n "${5:-}" ] && args="$args ${5}"
    [ -n "${6:-}" ] && args="$args ${6}"
    winps "$TEST_HOST" "Remove-Item '$TMP\\app.png' -ErrorAction SilentlyContinue" >/dev/null 2>&1 || true
    run_task DriverForgeGui "$APP\\DriverForge.exe" "$args" 1
    for _ in $(seq 1 900); do
      if scp -q "$TEST_HOST:driverforge-test/app.png" "$out" 2>/dev/null; then echo "saved $out"; exit 0; fi
      sleep 2
    done
    echo "the app produced no screenshot" >&2; exit 1 ;;
  stop)
    winps "$TEST_HOST" "Get-Process DriverForge -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '$APP\\*' } | Stop-Process -Force; Unregister-ScheduledTask -TaskName DriverForgeGui -Confirm:\$false -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName DriverForgeShot -Confirm:\$false -ErrorAction SilentlyContinue; 'stopped'" 2>&1 | grep -v 'CLIXML\|^<Objs' ;;
  *) sed -n 2,8p "$0"; exit 2 ;;
esac
