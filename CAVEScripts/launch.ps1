<#
.SYNOPSIS
    Launches one UniCAVE-GS instance per GPU on the current node, or a single
    headless instance on the master.

    Script auto-detects its build folder from $PSScriptRoot, so it finds the
    exe and config next to itself regardless of deploy path.

    UniCAVE custom args: no dash prefix (Util.GetArg parses raw).
    Unity built-in args:  dash prefix.
#>
[CmdletBinding()]
param(
    [string]$ConfigFile
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms

$scriptDir = Split-Path -Parent $PSCommandPath
if (-not $ConfigFile) { $ConfigFile = Join-Path $scriptDir 'nodes.json' }
if (-not (Test-Path $ConfigFile)) { throw "config not found: $ConfigFile" }

$cfg = Get-Content $ConfigFile -Raw | ConvertFrom-Json

$me       = $env:COMPUTERNAME
$isMaster = ($me -eq $cfg.Master.Host)

$buildDir = $scriptDir
$exe      = Join-Path $buildDir $cfg.ExeName
$logDir   = Join-Path $buildDir 'Logs'

if (-not (Test-Path $exe))    { throw "exe not found: $exe - sync the build first." }
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

# Resolve display-number by coordinate: pick the top panel of the target column
# (smallest Y at the column's X). Picking any display on the target column also pins Unity's 
# D3D device to that GPU so compute and swapchain presentation stay aligned.
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

function Start-Instance([string]$virtualName, [int]$gpu = -1, [switch]$Batch, [switch]$SmallWindow) {
    $logFile = Join-Path $logDir "$virtualName.log"
    $argList = @()
    if ($Batch) { $argList += @('-batchmode', '-nographics') }
    $argList += @(
        'overrideMachineName', $virtualName,
        'serverAddress',       $cfg.ServerAddress,
        'serverPort',          $cfg.ServerPort,
        '-logFile',            $logFile
    )
    $monitor = -1
    if ($SmallWindow) {
        $argList += @('-popupwindow', '-screen-fullscreen', '0',
                      '-screen-width', '640', '-screen-height', '360')
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
}

if ($isMaster) {
    if ($cfg.Master.Headless) {
        Start-Instance $cfg.Master.VirtualName -Batch
    } else {
        Start-Instance $cfg.Master.VirtualName -SmallWindow
    }
    return
}

$insts = $cfg.DisplayNodes.$me
if (-not $insts) { Write-Host "$me : no entry in nodes.json - skipping"; exit 0 }

foreach ($i in $insts) {
    Start-Instance $i.name $i.gpu
    Start-Sleep -Milliseconds 2000   # stagger to avoid DX device creation race
}
