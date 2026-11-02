<#
.SYNOPSIS
  One game session with the development build of the plugin.

.DESCRIPTION
  Installs the Ultimate ASI Loader, the plugin and an INI into the game's Bin64
  folder for the duration of the run only, starts the game through Steam, lets
  the plugin load a test world and quit after -Seconds (unattended runs), or
  waits for the player to quit (-Seconds 0, the queue app's play profile),
  collects the plugin and game logs into research\runs\<stamp>-<Name>, and
  removes everything it installed again (also on failure or timeout).

  Run it through the shared queue (D:\Githubs\_agents\queue_executor): it
  launches the game.

.EXAMPLE
  scripts\run_session.ps1 -Name spike -World RedShip -Seconds 30 -Set 'FrameRate.TargetFPS=-1'
#>
param(
    [string]$Name = 'session',
    # config\SpaceEngineers.HighFPSFix.<Config>.ini; empty = the development INI.
    [string]$Config = '',
    # Folder under research\worlds (copied fresh for each run); empty or 'menu' =
    # main menu only.
    [string]$World = 'RedShip',
    # Seconds in the loaded world (or in the menu) before the plugin quits the
    # game; 0 = the player quits.
    [double]$Seconds = 30,
    # INI overrides, 'Section.Key=Value;Section.Key=Value'.
    [string]$Set = '',
    # Shortcuts for the play profile: [FrameRate] TargetFPS, [Interpolation]
    # Enabled and MouseLook.
    [string]$TargetFps = '',
    [ValidateSet('', 'on', 'off')][string]$Interpolation = '',
    [ValidateSet('', 'on', 'off')][string]$MouseLook = '',
    # [Debug] AutoMouseSpeed: synthetic mouse turns at up to this many counts
    # per second once the world is ready (0 = off).
    [double]$MouseSpeed = 0,
    # No per-draw trace (long play sessions).
    [switch]$NoTrace,
    # Install nothing: the stock game (with -CloseAfterSeconds, to compare exits).
    [switch]$Vanilla,
    # Close the game's window (as the player clicking X) after this many
    # seconds, and report how long the game takes to exit; 0 = off.
    [double]$CloseAfterSeconds = 0,
    [string]$BuildDir = 'build-x64',
    # Install a release folder (dist\release\...) instead of the build: its
    # files, its INI as the base, and its bundled loader if it has one.
    [string]$PackageDir = '',
    [string]$GameBin64 = 'D:\Program Files\Games\Steam\steamapps\common\SpaceEngineers\Bin64',
    [string]$Loader = '',
    # File names the loader is installed under (a .NET game resolves version.dll
    # from System32 only, see third_party\ultimate-asi-loader\UPSTREAM.md).
    [string[]]$LoaderNames = @('dinput8.dll'),
    [int]$TimeoutSeconds = 420,
    [switch]$PresentMon
)

$ErrorActionPreference = 'Stop'
if ($World -eq 'menu') { $World = '' }
$root = Split-Path -Parent $PSScriptRoot
$pluginName = 'SpaceEngineers.HighFPSFix'
if (-not $Loader) { $Loader = Join-Path $root 'research\loader\version.dll' }
$bin = Join-Path $root "$BuildDir\bin"
if ($PackageDir) {
    $bin = (Resolve-Path $PackageDir).Path
    $bundled = Get-ChildItem $bin -Filter '*.dll' | Where-Object { $_.Name -notlike "$pluginName*" } | Select-Object -First 1
    if ($bundled) { $Loader = $bundled.FullName; $LoaderNames = @($bundled.Name) }
}
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $root "research\runs\$stamp-$Name"
New-Item -ItemType Directory -Force $runDir | Out-Null
$appData = Join-Path $env:APPDATA 'SpaceEngineers'

function Write-Step([string]$text) { Write-Output ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $text) }

function Set-IniValue([string[]]$lines, [string]$section, [string]$key, [string]$value) {
    $result = New-Object System.Collections.Generic.List[string]
    $inSection = $false; $done = $false; $sawSection = $false
    foreach ($line in $lines) {
        if ($line -match '^\s*\[(.+)\]\s*$') {
            if ($inSection -and -not $done) { $result.Add("$key = $value"); $done = $true }
            $inSection = ($Matches[1] -eq $section)
            if ($inSection) { $sawSection = $true }
        } elseif ($inSection -and -not $done -and $line -match "^\s*$([regex]::Escape($key))\s*=") {
            $result.Add("$key = $value"); $done = $true; continue
        }
        $result.Add($line)
    }
    if (-not $done) {
        # Either the section is missing, or it is the last one and lacks the key.
        if (-not $sawSection) { $result.Add("[$section]") }
        $result.Add("$key = $value")
    }
    return ,$result.ToArray()
}

# An interrupted run (a force-cancelled job ends this script without its
# finally block) leaves this file behind; undo what it records first.
$stateFile = Join-Path $root 'research\runs\.session-state.json'

function Restore-Session([string]$path) {
    $state = Get-Content $path -Raw | ConvertFrom-Json
    if ($state.GamePid) {
        $process = Get-Process -Id $state.GamePid -ErrorAction SilentlyContinue
        if ($process -and $process.ProcessName -eq 'SpaceEngineers' -and
            $process.StartTime.ToString('o') -eq $state.GameStart) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
    }
    foreach ($file in @($state.Installed)) {
        if ($file -and (Test-Path $file)) { Remove-Item -Force $file -ErrorAction SilentlyContinue }
    }
    foreach ($backup in @($state.LastSession)) {
        if ($backup -and (Test-Path $backup.Backup)) { Copy-Item -Force $backup.Backup $backup.Path }
    }
    Remove-Item -Force $path
}

if (Test-Path $stateFile) {
    Write-Step 'Undoing an interrupted earlier run.'
    Restore-Session $stateFile
}
if (Get-Process -Name SpaceEngineers -ErrorAction SilentlyContinue) {
    throw 'SpaceEngineers.exe is already running; not starting a session.'
}
foreach ($required in @("$bin\$pluginName.asi", "$bin\$pluginName.Managed.dll", $Loader)) {
    if (-not (Test-Path $required)) { throw "Missing $required" }
}

# INI: profile + test-run settings + overrides.
$iniSource = if ($PackageDir) { Join-Path $bin "$pluginName.ini" } elseif ($Config) { Join-Path $root "config\$pluginName.$Config.ini" } else { Join-Path $root "config\$pluginName.ini" }
$ini = Get-Content $iniSource
$worldCopy = ''
if ($World) {
    $worldSource = Join-Path $root "research\worlds\$World"
    if (-not (Test-Path $worldSource)) { throw "Unknown test world $World" }
    $worldCopy = Join-Path $env:TEMP "$pluginName\worlds\$World"
    if (Test-Path $worldCopy) { Remove-Item -Recurse -Force $worldCopy }
    New-Item -ItemType Directory -Force (Split-Path $worldCopy) | Out-Null
    Copy-Item -Recurse $worldSource $worldCopy
}
$ini = Set-IniValue $ini 'Debug' 'AutoLoadWorld' $worldCopy
$ini = Set-IniValue $ini 'Debug' 'AutoQuitSeconds' ([string]$Seconds)
$ini = Set-IniValue $ini 'Debug' 'TracePath' $(if ($NoTrace) { '' } else { Join-Path $runDir 'trace' })
if ($TargetFps) { $ini = Set-IniValue $ini 'FrameRate' 'TargetFPS' $TargetFps }
if ($Interpolation) { $ini = Set-IniValue $ini 'Interpolation' 'Enabled' $(if ($Interpolation -eq 'on') { 'true' } else { 'false' }) }
if ($MouseLook) { $ini = Set-IniValue $ini 'Interpolation' 'MouseLook' $(if ($MouseLook -eq 'on') { 'true' } else { 'false' }) }
if ($MouseSpeed -gt 0) { $ini = Set-IniValue $ini 'Debug' 'AutoMouseSpeed' ([string]$MouseSpeed) }
foreach ($assignment in ($Set -split ';' | Where-Object { $_.Trim() })) {
    if ($assignment -notmatch '^\s*([^.=]+)\.([^=]+?)\s*=\s*(.*)$') { throw "Bad -Set entry '$assignment'" }
    $ini = Set-IniValue $ini $Matches[1].Trim() $Matches[2].Trim() $Matches[3].Trim()
}

$state = [ordered]@{ Installed = @(); LastSession = @(); GamePid = 0; GameStart = '' }
function Save-State { $state | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $stateFile }
function Add-Installed([string]$path) { $state.Installed += $path; Save-State }

# The game records the loaded test world as the last session; keep the
# player's "Continue" pointing at their own world. (Not when the player loads
# their own world: then the game's record is the right one.)
$index = 0
$lastSessionFiles = if ($World) { Get-ChildItem -Path (Join-Path $appData 'Saves') -Filter LastSession.sbl -Recurse -ErrorAction SilentlyContinue } else { @() }
foreach ($file in $lastSessionFiles) {
    $backup = Join-Path $runDir ("LastSession.{0}.sbl.bak" -f $index++)
    Copy-Item $file.FullName $backup
    $state.LastSession += [ordered]@{ Path = $file.FullName; Backup = $backup }
}
Save-State
$gameProcess = $null
$presentMonProcess = $null
$started = Get-Date

try {
    # Install for this run only.
    if ($Vanilla) { $LoaderNames = @() }
    foreach ($loaderName in $LoaderNames) {
        $loaderTarget = Join-Path $GameBin64 $loaderName
        if (Test-Path $loaderTarget) {
            if ((Get-FileHash $loaderTarget).Hash -ne (Get-FileHash $Loader).Hash) {
                throw "A different $loaderName is already in $GameBin64; not replacing it."
            }
            Write-Step "$loaderName (same loader) already present; leaving it."
        } else {
            Add-Installed $loaderTarget; Copy-Item $Loader $loaderTarget
        }
    }
    foreach ($file in @("$pluginName.asi", "$pluginName.Managed.dll", "$pluginName.Managed.pdb")) {
        $source = Join-Path $bin $file
        if (-not $Vanilla -and (Test-Path $source)) {
            $target = Join-Path $GameBin64 $file
            Add-Installed $target; Copy-Item -Force $source $target
        }
    }
    $iniTarget = Join-Path $GameBin64 "$pluginName.ini"
    if (-not $Vanilla) {
        Add-Installed $iniTarget; [System.IO.File]::WriteAllLines($iniTarget, $ini)
        Copy-Item $iniTarget (Join-Path $runDir "$pluginName.ini")
    }
    foreach ($log in @("$pluginName.log", "$pluginName.previous.log")) {
        $path = Join-Path $GameBin64 $log
        if (Test-Path $path) { Remove-Item -Force $path }
        Add-Installed $path
    }

    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -Name SteamExe).SteamExe
    Write-Step "Starting the game through Steam ($steam)."
    Start-Process -FilePath $steam -ArgumentList @('-applaunch', '244850') | Out-Null
    $deadline = (Get-Date).AddSeconds(90)
    while (-not $gameProcess -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $gameProcess = Get-Process -Name SpaceEngineers -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if (-not $gameProcess) { throw 'SpaceEngineers.exe did not start.' }
    $state.GamePid = $gameProcess.Id
    $state.GameStart = $gameProcess.StartTime.ToString('o')
    Save-State
    Write-Step "Game running (pid $($gameProcess.Id))."

    if ($PresentMon) {
        # Read-only use of the shared tools folder (tools\ is a link to it).
        $presentMonExe = Join-Path $root 'tools\PresentMon-2.5.1-x64.exe'
        $presentMonProcess = Start-Process -FilePath $presentMonExe -PassThru -WindowStyle Hidden -ArgumentList @(
            '--process_id', $gameProcess.Id, '--output_file', (Join-Path $runDir 'presentmon.csv'),
            '--no_console_stats', '--stop_existing_session', '--session_name', "$pluginName-$stamp")
    }

    if ($CloseAfterSeconds -gt 0 -and -not $gameProcess.WaitForExit([int]($CloseAfterSeconds * 1000))) {
        $closing = Get-Date
        $gameProcess.Refresh()
        Write-Step "Closing the game window ($($gameProcess.MainWindowTitle))."
        [void]$gameProcess.CloseMainWindow()
        if ($gameProcess.WaitForExit(60000)) {
            Write-Step ("Exit after closing the window: {0:F1} s." -f ((Get-Date) - $closing).TotalSeconds)
        } else {
            Write-Step 'The game did not exit within 60 s of closing its window.'
        }
    }
    $exited = $gameProcess.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) {
        Write-Step "Timeout after $TimeoutSeconds s; ending the game."
        Stop-Process -Id $gameProcess.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    } else {
        Write-Step ("Game exited after {0:F0} s." -f ((Get-Date) - $started).TotalSeconds)
    }
}
finally {
    if ($presentMonProcess -and -not $presentMonProcess.HasExited) {
        Stop-Process -Id $presentMonProcess.Id -ErrorAction SilentlyContinue
    }
    Get-Process -Name SpaceEngineers -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    foreach ($log in @("$pluginName.log", "$pluginName.previous.log")) {
        $path = Join-Path $GameBin64 $log
        if (Test-Path $path) { Copy-Item $path (Join-Path $runDir $log) }
    }
    Get-ChildItem $appData -Filter 'SpaceEngineers_*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $started } | Sort-Object LastWriteTime | Select-Object -Last 1 |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $runDir 'game.log') }
    Restore-Session $stateFile
    Write-Step "Uninstalled; outputs in $runDir"
}

$pluginLog = Join-Path $runDir "$pluginName.log"
if ($Vanilla) {
    Write-Output 'Stock game (nothing installed).'
} elseif (Test-Path $pluginLog) {
    Write-Output '--- plugin log ---'
    Get-Content $pluginLog | Select-Object -Last 60
} else {
    Write-Output 'No plugin log was written (the loader did not load the plugin?).'
    exit 2
}
