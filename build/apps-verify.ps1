# Milestone A7 (docs/APPS.md §4, §10): run every curated app's silent install
# on the TEST BOX and record whether it was clean. Runs in the signed-in
# session (started by build/apps-verify.sh through a scheduled task), because
# only there can it see a window an installer leaves open.
#
#   apps-verify.ps1 -App <folder with DriverForge.exe> -Ids <file, one ID per line>
#
# Per app: install (DriverForge --apps-install, one-app list), wait, look for
# new top-level windows, uninstall (--apps-uninstall) unless it was already
# installed or is a runtime, and append one JSON line to verify.jsonl.
# Clean = Installed, no flagged extras, no window left open, uninstalled.
param([Parameter(Mandatory)] [string] $App, [Parameter(Mandatory)] [string] $Ids)
$ErrorActionPreference = 'Continue'
$exe = Join-Path $App 'DriverForge.exe'
$out = Join-Path $App 'verify.jsonl'
# ⚠️ Not $ids: PowerShell names are case-insensitive, so that IS the [string]
# $Ids parameter and the list would be coerced into one space-joined string.
$packages = @(Get-Content $Ids | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() })
$keep = @('Microsoft.DotNet.DesktopRuntime.8', 'Microsoft.DotNet.DesktopRuntime.10', 'Microsoft.VCRedist.2015+.x64',
          'EclipseAdoptium.Temurin.21.JRE', 'Microsoft.EdgeWebView2Runtime')   # runtimes: never uninstalled here

function Windows() {
    Get-Process | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle } |
        ForEach-Object { "$($_.Id)|$($_.ProcessName)|$($_.MainWindowTitle)" }
}

# ⚠️ Not Start-Process -Wait: in Windows PowerShell it waits for the whole
# process tree, and an uninstaller that opens a web page (Firefox's does)
# leaves a browser running as a descendant, so it never returns.
# Some uninstallers ignore winget's silent switch and wait on a prompt nobody
# sees (Brave's did); give up after $minutes and stop that process tree.
function Run([string] $arguments, [int] $minutes = 30) {
    $info = New-Object Diagnostics.ProcessStartInfo $exe, $arguments
    $info.WorkingDirectory = $App
    $info.UseShellExecute = $false
    $p = [Diagnostics.Process]::Start($info)
    if (-not $p.WaitForExit($minutes * 60000)) {
        & taskkill.exe /PID $p.Id /T /F | Out-Null
        return 'timeout'
    }
    return $p.ExitCode
}

function CloseNew([string[]] $before) {
    $new = @(Windows | Where-Object { $before -notcontains $_ })
    foreach ($w in $new) { Stop-Process -Id ([int]($w -split '\|')[0]) -Force -ErrorAction SilentlyContinue }
    return $new
}

foreach ($id in $packages) {
    $started = Get-Date
    $list = Join-Path $App 'verify-one.json'
    @{ '$schema' = 'https://aka.ms/winget-packages.schema.2.0.json'
       Sources = @(@{ Packages = @(@{ PackageIdentifier = $id }); SourceDetails = @{ Name = 'winget' } })
       WinGetVersion = '1.29.380' } | ConvertTo-Json -Depth 6 | Set-Content $list -Encoding UTF8

    $before = @(Windows)
    $code = Run "--apps-install `"$list`" --accept-agreements"
    $install = Get-Content (Join-Path $App 'last-apps.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
    Start-Sleep 8                                   # installers that launch the app do it right at the end
    $opened = @(CloseNew $before)

    $r = $install | Select-Object -First 1
    $uninstall = $null
    $uninstallWindows = @()
    if ($r.Outcome -eq 'Installed' -and $keep -notcontains $id) {
        $beforeUninstall = @(Windows)
        $u = Run "--apps-uninstall $id" 10
        $uninstall = if ($u -eq 'timeout') { 'TimedOut' } else { (Get-Content (Join-Path $App 'last-apps.json') -Raw | ConvertFrom-Json | Select-Object -First 1).Outcome }
        Start-Sleep 3
        $uninstallWindows = @(CloseNew $beforeUninstall)      # not held against the install
    }
    $clean = ($r.Outcome -eq 'Installed') -and (@($r.Extras).Count -eq 0) -and ($opened.Count -eq 0) -and
             ($uninstall -eq 'Uninstalled' -or $keep -contains $id)
    [ordered]@{
        id = $id; exit = $code; outcome = $r.Outcome; version = $r.ToVersion; message = $r.Message
        extras = @($r.Extras | ForEach-Object { $_.Name }); windows = $opened; uninstall = $uninstall
        uninstallWindows = @($uninstallWindows)
        clean = $clean; seconds = [int]((Get-Date) - $started).TotalSeconds; at = (Get-Date -Format s)
    } | ConvertTo-Json -Compress -Depth 4 | Add-Content $out -Encoding UTF8
}
Add-Content $out '{"done":true}' -Encoding UTF8
