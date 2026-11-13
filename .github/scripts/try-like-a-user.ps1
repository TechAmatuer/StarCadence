# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.

# Takes a download exactly as it will be attached to the release, unpacks it the way a person would on a
# fresh Windows or Mac, opens it the way a double-click does (no arguments, settings in Documents), and uses
# the app window: first-run page in a real browser, fetch, plan, rehearsal run, second launch, Quit.

param([Parameter(Mandatory)] [string] $Download, [Parameter(Mandatory)] [string] $Version)

$ErrorActionPreference = 'Stop'
$base = 'http://127.0.0.1:8711'
$downloads = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Downloads'
$data = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'StarCadence'
$evidence = New-Item -ItemType Directory -Force (Join-Path $PWD 'evidence')
New-Item -ItemType Directory -Force $downloads | Out-Null

function Fail([string] $why) {
    Write-Host "::error::$why"
    foreach ($f in @((Join-Path $data 'startup-error.txt'), (Join-Path ([IO.Path]::GetTempPath()) 'startup-error.txt'))) {
        if (Test-Path $f) { Write-Host "--- $f"; Get-Content $f | Write-Host }
    }
    throw $why
}
function Get-Text([string] $path) { (Invoke-WebRequest "$base$path" -TimeoutSec 30).Content }
function Post([string] $path, $body = @{}) {
    $r = Invoke-WebRequest "$base$path" -Method Post -TimeoutSec 180 -ContentType 'application/json' -Headers @{ 'X-StarCadence' = $script:token } `
        -Body ($body | ConvertTo-Json -Compress) -SkipHttpErrorCheck
    if ($r.StatusCode -ge 400) { Fail "POST $path answered $($r.StatusCode): $($r.Content)" }
    $r.Content
}
function Wait-Ping([int] $seconds) {
    for ($i = 0; $i -lt $seconds; $i++) {
        try { if ((Invoke-WebRequest "$base/api/ping" -TimeoutSec 2).Content -eq 'starcadence') { return $true } } catch { }
        Start-Sleep 1
    }
    $false
}

function Ping-Once { try { (& curl -s -m 2 "$base/api/ping") -eq 'starcadence' } catch { $false } }
function Show-Who {
    Write-Host "--- who is on port 8711"
    if ($IsWindows) {
        Get-NetTCPConnection -LocalPort 8711 -ErrorAction SilentlyContinue | Select-Object LocalAddress, LocalPort, RemotePort, State, OwningProcess | Format-Table -AutoSize | Out-String | Write-Host
        Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'starcadence' } | Select-Object ProcessId, ParentProcessId, CreationDate, CommandLine | Format-List | Out-String | Write-Host
    } else {
        lsof -nP -iTCP:8711 2>$null | Write-Host
        ps -axo pid,ppid,lstart,command | Select-String -Pattern 'starcadence' -SimpleMatch | Write-Host
        if ($appOut -and (Test-Path $appOut)) { Write-Host "--- what the app printed"; Get-Content $appOut | Write-Host }
    }
    & curl -s -i -m 2 "$base/api/ping" | Write-Host
}

if (Test-Path $data) { Fail "$data already exists; this check needs a computer that has never run StarCadence" }

# ---- unpack ----
Write-Host "::group::Unpack $([IO.Path]::GetFileName($Download)) into Downloads"
if ($IsWindows) {
    Expand-Archive $Download -DestinationPath $downloads          # what Extract All does
    $exe = Join-Path $downloads 'StarCadence\StarCadence.exe'
    if (-not (Test-Path $exe)) { Fail "StarCadence.exe is not where the README says (StarCadence\StarCadence.exe)" }
    $sig = Get-AuthenticodeSignature $exe
    "signature: $($sig.Status) (not code-signed, so Windows SmartScreen will ask once: More info > Run anyway)"
} else {
    $zip = Join-Path $downloads 'StarCadence-mac.zip'
    Copy-Item $Download $zip
    $mark = "0083;{0:x};Safari;" -f [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    xattr -w com.apple.quarantine $mark $zip                      # a browser download is marked
    ditto -x -k $zip $downloads                                   # what double-clicking the zip does
    $app = Join-Path $downloads 'StarCadence/StarCadence.app'
    if (-not (Test-Path $app)) { Fail "StarCadence.app is not where the README says (StarCadence/StarCadence.app)" }
    xattr -r -w com.apple.quarantine $mark (Join-Path $downloads 'StarCadence')   # Archive Utility carries the mark over
    $bin = Join-Path $app 'Contents/MacOS/starcadence'
    file $bin
    if (-not ((file $bin) -match 'arm64')) { Fail "the Mac program is not built for Apple Silicon" }
    # Apple Silicon refuses to start a program whose signature broke on the way (killed: 9), so this matters.
    codesign --verify --deep --strict --verbose=2 $app 2>&1 | Write-Host
    if ($LASTEXITCODE -ne 0) { Fail "the app's signature did not survive the zip" }
    codesign -dv $app 2>&1 | Write-Host
    $gk = (spctl --assess --type execute -vv $app 2>&1) -join ' '
    "Gatekeeper before Open Anyway: $gk"
    if ($gk -notmatch 'rejected') { "::notice::Gatekeeper did not reject the downloaded app (expected it to ask once)" }
    # The person clicks Open Anyway (README); the Terminal way in the README does the same:
    xattr -dr com.apple.quarantine (Join-Path $downloads 'StarCadence')
}
Write-Host "::endgroup::"

# ---- double-click ----
Write-Host "::group::Double-click it"
if ($IsWindows) {
    # Explorer starts it in its own folder with no arguments and gives it its own console window.
    $proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
} else {
    $appOut = Join-Path $evidence 'app-output.txt'
    open --stdout $appOut --stderr $appOut $app      # same launch as a double-click, but keeps what it prints
    if ($LASTEXITCODE -ne 0) { Fail "macOS would not open StarCadence.app" }
}
if (-not (Wait-Ping 90)) {
    if (-not $IsWindows) {
        log show --last 3m --style compact --predicate 'process == "tccd" OR process == "syspolicyd" OR eventMessage CONTAINS[c] "starcadence"' 2>$null |
            Select-Object -Last 60 | Write-Host
        screencapture -x (Join-Path $evidence 'mac-after-open.png') 2>$null
    }
    Fail "the app window did not start within 90 seconds of the double-click"
}
"answering at $base"
if (-not (Test-Path (Join-Path $data 'config.json'))) { Fail "no settings were written to $data" }
"settings folder: $data"
Write-Host "::endgroup::"

# ---- the page, in a real browser ----
Write-Host "::group::First-run page in a real browser"
$page = Get-Text '/'
$script:token = [regex]::Match($page, '[0-9A-F]{32}').Value
if (-not $token) { Fail "the page carries no session token" }
$state = Get-Text '/api/state' | ConvertFrom-Json
if (-not $state.firstRun) { Fail "the first launch was not treated as a first run" }
if ($state.version -ne $Version) { Fail "the app says version $($state.version), expected $Version" }
$browser = if ($IsWindows) { @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Google\Chrome\Application\chrome.exe") }
           else { @('/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge') }
$browser = $browser | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($browser) {
    $browserDir = Join-Path ([IO.Path]::GetTempPath()) ("sc-browser-" + [guid]::NewGuid())
    $dom = & $browser --headless=new --disable-gpu --no-first-run --user-data-dir="$browserDir" --virtual-time-budget=10000 --dump-dom "$base/" 2>$null | Out-String
    Set-Content (Join-Path $evidence 'page-after-scripts.html') $dom
    "browser: $browser, page after its scripts ran: $($dom.Length) characters"
    if ($dom -notmatch "v$([regex]::Escape($Version))") { Fail "the page did not show the version after its scripts ran (see evidence/page-after-scripts.html)" }
    if ($dom -notmatch 'Save changes') { Fail "the page did not open on Setup for a first run" }
    # (the page's own script contains the error text as a template, so look for a filled-in one)
    if ($dom -match 'StarCadence is not answering: [^$]') { Fail "the page could not reach the app" }
    "header shows: " + [regex]::Match($dom, 'id="site">([^<]*)').Groups[1].Value
    "tabs: " + (([regex]::Matches($dom, '<button[^>]*data-tab="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }) -join ', ')
} else { "::warning::no browser found to load the page with" }
Write-Host "::endgroup::"

# ---- what a person does next ----
Write-Host "::group::Download target lists, plan tonight, rehearse"
$fetch = Post '/api/fetch' | ConvertFrom-Json
$fetch.messages | ForEach-Object { "  $_" }
$plan = Post '/api/plan' @{ fromNow = $false } | ConvertFrom-Json
"plan for $($plan.date): $($plan.plan.blocks.Count) blocks, dark $($plan.dark.start) to $($plan.dark.end), $($plan.files.Count) files"
$plan.sources | ForEach-Object { "  $($_.source): $($_.count) targets" }
foreach ($f in $plan.files) { if (-not (Test-Path (Join-Path $data $f))) { Fail "plan file $f was not written" } }
if (($plan.sources | Measure-Object -Property count -Sum).Sum -gt 0 -and $plan.plan.blocks.Count -eq 0) { "::warning::targets were downloaded but tonight's plan is empty" }
Post '/api/run/start' @{ mode = 'simulate'; speed = 600 } | Out-Null
Start-Sleep 8
$run = Get-Text '/api/run/status' | ConvertFrom-Json
if (-not $run.running) { Fail "the rehearsal did not keep running: $($run | ConvertTo-Json -Compress)" }
"rehearsal running: $($run.phase) $($run.message)"
$quit = Invoke-WebRequest "$base/api/quit" -Method Post -Headers @{ 'X-StarCadence' = $token } -SkipHttpErrorCheck
if ($quit.StatusCode -lt 400) { Fail "Quit was allowed while a night was being run" }
"Quit during a run is refused: $($quit.Content)"
$stopped = Post '/api/run/stop' | ConvertFrom-Json
if ($stopped.running) { Fail "the rehearsal did not stop" }
Write-Host "::endgroup::"

# ---- double-click again, then Quit ----
Write-Host "::group::Double-click again while it is open, then Quit"
if ($IsWindows) {
    $second = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -PassThru
    if (-not $second.WaitForExit(30000)) { Fail "a second double-click started a second copy instead of reopening the first" }
    "second copy handed over to the first and exited ($($second.ExitCode))"
} else {
    open $app
    Start-Sleep 3
}
if (-not (Wait-Ping 5)) { Fail "the app stopped after the second double-click" }
if ($IsWindows) { "first copy is process $($proc.Id), still running: $(-not $proc.HasExited)" }
Post '/api/quit' | Out-Null
$clock = [Diagnostics.Stopwatch]::StartNew(); $down = $null
while ($clock.Elapsed.TotalSeconds -lt 90) {
    if (-not (Ping-Once)) { $down = $clock.Elapsed.TotalSeconds; break }
    if ([int]$clock.Elapsed.TotalSeconds % 15 -eq 0) { "  still answering after $([int]$clock.Elapsed.TotalSeconds) s" }
    Start-Sleep 1
}
if ($null -eq $down) { Show-Who; Fail "the app is still answering 90 seconds after Quit" }
"stopped answering $([math]::Round($down, 1)) s after Quit"
if ($IsWindows) {
    if (-not $proc.WaitForExit(30000)) { Fail "StarCadence.exe did not exit after Quit" }
    if ($proc.ExitCode -ne 0) { Fail "StarCadence.exe exited with code $($proc.ExitCode)" }
} else {
    Start-Sleep 2
    $left = pgrep -f 'StarCadence.app/Contents/MacOS/starcadence'
    if ($left) { Fail "StarCadence.app is still running after Quit" }
}
"stopped cleanly"
Get-ChildItem $data -Recurse -File | ForEach-Object { "  " + [IO.Path]::GetRelativePath($data, $_.FullName) }
Write-Host "::endgroup::"
"All good: $([IO.Path]::GetFileName($Download)) unpacks, opens by double-click, plans, rehearses and quits."
