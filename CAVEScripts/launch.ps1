<#
.SYNOPSIS
    Launches one player instance per GPU on the current node, or a single
    instance on the master.

    The script finds the exe and nodes.json next to itself, so it works from
    any deploy path. If nodes.json leaves ExeName empty, the only player exe in
    the folder is used. TrackerHost in nodes.json replaces TRACKER_HOST in the
    VRPN addresses of the prefabs (passed as the trackerHost argument).

    UniCAVE custom args: no dash prefix (Util.GetArg parses raw).
    Unity built-in args:  dash prefix.

.PARAMETER ExtraArgs
    Extra command-line arguments for every instance, as one string,
    for example -ExtraArgs "-logStats -myFlag 3".
#>
[CmdletBinding()]
param(
    [string]$ConfigFile,
    [string]$ExtraArgs = ''
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms

$scriptDir = Split-Path -Parent $PSCommandPath
if (-not $ConfigFile) { $ConfigFile = Join-Path $scriptDir 'nodes.json' }
if (-not (Test-Path $ConfigFile)) { throw "config not found: $ConfigFile" }

$cfg = Get-Content $ConfigFile -Raw | ConvertFrom-Json

$me       = $env:COMPUTERNAME
$isMaster = ($me -eq $cfg.Master.Host)

# The player exe in a build folder, ignoring Unity's crash handler.
function Find-PlayerExe([string]$dir) {
    $exes = @(Get-ChildItem -Path $dir -Filter '*.exe' -File |
        Where-Object { $_.Name -notlike 'UnityCrashHandler*' })
    if ($exes.Count -ne 1) { throw "expected one player exe in $dir, found $($exes.Count); set ExeName in nodes.json" }
    return $exes[0].FullName
}

$buildDir = $scriptDir
$exe      = if ($cfg.ExeName) { Join-Path $buildDir $cfg.ExeName } else { Find-PlayerExe $buildDir }
$logDir   = Join-Path $buildDir 'Logs'
$extra    = @($ExtraArgs -split '\s+' | Where-Object { $_ })

if (-not (Test-Path $exe))    { throw "exe not found: $exe - sync the build first." }
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

# Monitor number of the top panel of a column (smallest Y at the column's X). A display on the
# GPU's own column also pins Unity's D3D device to that GPU, so rendering and presentation match.
function Get-TopMonitorOnColumn([int]$columnX) {
    $top = [System.Windows.Forms.Screen]::AllScreens |
        Where-Object { $_.Bounds.X -eq $columnX } |
        Sort-Object { $_.Bounds.Y } |
        Select-Object -First 1
    if (-not $top) { return -1 }
    # DeviceName is like "\\.\DISPLAY4" - extract the trailing number
    return [int]($top.DeviceName -replace '\\\\\.\\DISPLAY', '')
}

function Get-LeftMonitorOnRow([int]$rowY) {
    $left = [System.Windows.Forms.Screen]::AllScreens |
        Where-Object { $_.Bounds.X -eq 0 -and $_.Bounds.Y -eq $rowY } |
        Select-Object -First 1
    if (-not $left) { return -1 }
    return [int]($left.DeviceName -replace '\\\\\.\\DISPLAY', '')
}

function Get-MonitorForGpu([string]$node, [int]$gpu) {
    if ($node -eq 'ORION') {
        # Backwall row layout: GPU index N drives row N from top
        # (Y = 0, 768, 1536, 2304). Pick the leftmost panel of that row so
        # a 5440-wide window grows rightward across all 4 row panels.
        $rowY = $gpu * 768
        return Get-LeftMonitorOnRow $rowY
    }
    # Cylinder: top panel so a 3072-tall window grows downward over all 4.
    $colX = $gpu * 1366        # cylinder GPU 0..2 -> X 0, 1366, 2732
    return Get-TopMonitorOnColumn $colX
}

# Move a launched instance's main window to the top-left corner (0,0) at the given size.
# Unity's -popupwindow opens the window centered and has no -screen-x/-y arg, so the window
# is moved with Win32 MoveWindow once its handle exists (polling for up to 10 s).
function Move-WindowTopLeft([int]$procId, [int]$w, [int]$h) {
    if (-not ('UniCaveNative.Win' -as [type])) {
        Add-Type -Namespace 'UniCaveNative' -Name 'Win' -MemberDefinition @'
[DllImport("user32.dll")]
public static extern bool MoveWindow(System.IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
'@
    }
    for ($i = 0; $i -lt 50; $i++) {
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if (-not $proc) { return }
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [System.IntPtr]::Zero) {
            [void][UniCaveNative.Win]::MoveWindow($proc.MainWindowHandle, 0, 0, $w, $h, $true)
            return
        }
        Start-Sleep -Milliseconds 200
    }
}

function Start-Instance([string]$virtualName, [int]$gpu = -1, [switch]$Batch, [switch]$MasterWindow) {
    $logFile = Join-Path $logDir "$virtualName.log"
    $argList = @()
    if ($Batch) { $argList += @('-batchmode', '-nographics') }
    $argList += @(
        'overrideMachineName', $virtualName,
        'serverAddress',       $cfg.ServerAddress,
        'serverPort',          $cfg.ServerPort,
        '-logFile',            $logFile
    )
    if ($cfg.TrackerHost) { $argList += @('trackerHost', $cfg.TrackerHost) }
    $argList += $extra
    $monitor = -1
    $winW = 0
    $winH = 0
    if ($MasterWindow) {
        # Left third of the master's primary screen, full height, so a tall on-screen
        # readout fits. Moved to (0,0) after launch (see Move-WindowTopLeft).
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $winW = [int]($bounds.Width / 3)
        $winH = $bounds.Height
        $argList += @('-popupwindow', '-screen-fullscreen', '0',
                      '-screen-width', $winW, '-screen-height', $winH)
    } elseif ($gpu -ge 0) {
        $monitor = Get-MonitorForGpu $me $gpu
        $argList += @('-force-device-index', $gpu, '-monitor', $monitor, '-popupwindow')
        if ($me -eq 'ORION') {
            # Backwall row layout: 4 panels horizontally at 1360x768 each.
            $argList += @('-screen-width', 5440, '-screen-height', 768)
        } else {
            # Cylinder column layout: 4 panels stacked at 1366x768 each.
            $argList += @('-screen-width', 1366, '-screen-height', 3072)
        }
    }
    $p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru
    Write-Host "[$me] $virtualName pid=$($p.Id) gpu=$gpu monitor=$monitor log=$logFile"
    if ($MasterWindow) { Move-WindowTopLeft $p.Id $winW $winH }
}

if ($isMaster) {
    if ($cfg.Master.Headless) {
        Start-Instance $cfg.Master.VirtualName -Batch
    } else {
        Start-Instance $cfg.Master.VirtualName -MasterWindow
    }
    return
}

$insts = $cfg.DisplayNodes.$me
if (-not $insts) { Write-Host "$me : no entry in nodes.json - skipping"; exit 0 }

foreach ($i in $insts) {
    Start-Instance $i.name $i.gpu
    Start-Sleep -Milliseconds 2000   # stagger to avoid DX device creation race
}
