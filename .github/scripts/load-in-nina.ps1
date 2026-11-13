# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

# Installs the current stable N.I.N.A. from nighttime-imaging.eu, puts the StarCadence plug-in zip where the
# README says (one folder below Plugins\3.0.0), starts N.I.N.A. and reads its log to see whether it loaded
# the plug-in and its sequencer items.

param([Parameter(Mandatory)] [string] $PluginZip, [Parameter(Mandatory)] [string] $Version)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$evidence = New-Item -ItemType Directory -Force (Join-Path $PWD 'evidence')
$logs = Join-Path $env:LOCALAPPDATA 'NINA\Logs'

function Shot([string] $name) {
    try {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
        $bmp.Save((Join-Path $evidence "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
    } catch { "(no screenshot: $($_.Exception.Message))" }
}

Write-Host "::group::Install N.I.N.A. (stable)"
$url = 'https://f002.backblazeb2.com/file/ninasetup/Releases/3.2.0.9001/NINASetupBundle_3.2.0.9001.zip'
try {
    $html = (Invoke-WebRequest 'https://nighttime-imaging.eu/download/' -TimeoutSec 60 -UserAgent 'Mozilla/5.0').Content
    $m = [regex]::Match($html, 'https://[^"''\s]+/Releases/[\d.]+/NINASetupBundle_[\d.]+\.zip')
    if ($m.Success) { $url = $m.Value }
} catch { "download page not reachable ($($_.Exception.Message)); using $url" }
"installer: $url"
Invoke-WebRequest $url -OutFile nina-setup.zip -TimeoutSec 600
Expand-Archive nina-setup.zip -DestinationPath nina-setup -Force
Get-ChildItem nina-setup -Recurse -File | ForEach-Object { "  $($_.Name) $([math]::Round($_.Length / 1MB, 1)) MB" }
$setup = Get-ChildItem nina-setup -Recurse -File -Include *.exe | Select-Object -First 1
$msi = Get-ChildItem nina-setup -Recurse -File -Include *.msi | Select-Object -First 1
$installLog = Join-Path $evidence 'nina-install.log'
if ($setup) { $p = Start-Process $setup.FullName -ArgumentList '/quiet', '/norestart', '/log', "`"$installLog`"" -Wait -PassThru }
elseif ($msi) { $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$($msi.FullName)`"", '/qn', '/norestart', '/l*v', "`"$installLog`"" -Wait -PassThru }
else { throw "no installer in $url" }
"installer exit code: $($p.ExitCode)"
$nina = Get-ChildItem $env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs') -Filter NINA.exe -Recurse -Depth 3 -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $nina) { Get-Content $installLog -Tail 60 -ErrorAction SilentlyContinue; throw "N.I.N.A. did not install (NINA.exe not found)" }
"N.I.N.A. $($nina.VersionInfo.ProductVersion) at $($nina.FullName)"
Write-Host "::endgroup::"

Write-Host "::group::Put the plug-in where the README says"
# README: Extract All, then move the StarCadence folder into %LOCALAPPDATA%\NINA\Plugins\3.0.0\
$plugins = Join-Path $env:LOCALAPPDATA 'NINA\Plugins\3.0.0'
$target = Join-Path $plugins 'StarCadence'
New-Item -ItemType Directory -Force $target | Out-Null
Expand-Archive $PluginZip -DestinationPath $target -Force
Get-ChildItem $target | ForEach-Object { "  $($_.Name)  $($_.VersionInfo.FileVersion)" }
Write-Host "::endgroup::"

Write-Host "::group::Start N.I.N.A. and read its log"
$started = Get-Date
$proc = Start-Process $nina.FullName -WorkingDirectory $nina.DirectoryName -PassThru
$ok = $null; $bad = @()
for ($i = 0; $i -lt 300 -and -not $ok; $i += 5) {
    Start-Sleep 5
    if ($proc.HasExited) { break }
    $log = Get-ChildItem $logs -Filter *.log -ErrorAction SilentlyContinue | Where-Object LastWriteTime -ge $started.AddSeconds(-5) |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $log) { continue }
    $text = Get-Content $log.FullName -Raw -ErrorAction SilentlyContinue
    if ($text -match 'Successfully loaded plugin StarCadence version [^\r\n]*') { $ok = $Matches[0] }
    $bad = @(($text -split "`r?`n") | Where-Object { ($_ -match 'StarCadence' -and $_ -match '\|(EROR|ERROR|FATL|WARN|WARNING)\|') -or $_ -match 'Failed to load plugin|Plugin Item failed to load' })
    if ($bad) { break }
}
Start-Sleep 20      # let it finish building the sequencer's item list, which is where item templates fail
Shot 'nina-running'
$log = Get-ChildItem $logs -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
if ($log) {
    Copy-Item $log.FullName (Join-Path $evidence 'nina.log')
    $text = Get-Content $log.FullName -Raw
    $bad = @(($text -split "`r?`n") | Where-Object { ($_ -match 'StarCadence' -and $_ -match '\|(EROR|ERROR|FATL|WARN|WARNING)\|') -or $_ -match 'Failed to load plugin|Plugin Item failed to load' })
    ($text -split "`r?`n") | Where-Object { $_ -match 'plugin|StarCadence' } | Select-Object -First 40 | ForEach-Object { "  $_" }
}
if (-not $proc.HasExited) { $null = $proc.CloseMainWindow(); if (-not $proc.WaitForExit(30000)) { Stop-Process -Id $proc.Id -Force } }
Write-Host "::endgroup::"

if ($bad) { $bad | ForEach-Object { Write-Host "::error::$_" }; throw "N.I.N.A. reported problems with the StarCadence plug-in" }
if (-not $ok) { throw "N.I.N.A. did not report loading the StarCadence plug-in within 5 minutes (see the evidence artifact)" }
if ($ok -notmatch [regex]::Escape($Version)) { "::warning::N.I.N.A. loaded a StarCadence version other than $Version`: $ok" }
"N.I.N.A. $($nina.VersionInfo.ProductVersion): $ok"
$global:LASTEXITCODE = 0
exit 0
