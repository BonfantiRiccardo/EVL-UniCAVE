<#
.SYNOPSIS
    Waits, then records which GPU each Unicave-GS instance renders on and which GPU owns the
    monitor its window sits on. A mismatch means Windows copies every frame between adapters,
    which caps throughput while leaving every GPU short of saturation.

    Start it over RDP, disconnect (do NOT sign out), launch the instances, then read the output
    file. Everything is written to disk as well as the console, so nothing is lost if the
    session drops.

.EXAMPLE
    .\gpu_check.ps1
    .\gpu_check.ps1 -DelaySeconds 180 -Samples 10
#>
[CmdletBinding()]
param(
    [int]$DelaySeconds = 120,
    [int]$Samples = 5,
    [int]$SampleIntervalSeconds = 3,
    [string]$ProcessName = 'UniCAVE-GS',
    [string]$OutFile
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Windows.Forms

if (-not $OutFile) {
    $OutFile = Join-Path $env:TEMP "gpu_check-$env:COMPUTERNAME-$(Get-Date -f yyyyMMdd-HHmmss).txt"
}

# nvidia-smi is not on PATH here; modern drivers install it only in the DriverStore.
$smi = @('C:\Windows\System32\nvidia-smi.exe', 'C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $smi) { $smi = (Get-ChildItem 'C:\Windows\System32\DriverStore\FileRepository\nv_*' -Filter nvidia-smi.exe -Recurse -Force -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }

Write-Output "nvidia-smi: $smi"
Write-Output "output: $OutFile"
Write-Output "waiting $DelaySeconds s, launch the instances now..."
Start-Sleep -Seconds $DelaySeconds

$L = New-Object System.Collections.Generic.List[string]
function E($s) { Write-Output $s; $L.Add($s) | Out-Null }

E "gpu_check $env:COMPUTERNAME  $(Get-Date -f 'yyyy-MM-dd HH:mm:ss')"
E ""
E "--- GPUs ---"
if ($smi) { & $smi --query-gpu=index,name,pci.bus_id,utilization.gpu,memory.used --format=csv,noheader | ForEach-Object { E "  $_" } }
E ""

E "--- instances: render GPU vs the GPU that owns its monitor ---"
$pg = @{}
if ($smi) {
    $map = @{}
    & $smi --query-gpu=index,uuid --format=csv,noheader | ForEach-Object { $f = $_ -split ',\s*'; $map[$f[1]] = $f[0] }
    & $smi --query-compute-apps=pid,gpu_uuid --format=csv,noheader | ForEach-Object { $f = $_ -split ',\s*'; if ($f.Count -ge 2) { $pg[[int]$f[0]] = $map[$f[1]] } }
}
$procs = Get-Process $ProcessName -ErrorAction SilentlyContinue
if (-not $procs) { E "  no $ProcessName processes running" }
foreach ($p in $procs) {
    $p.Refresh()
    if ($p.MainWindowHandle -eq [IntPtr]::Zero) { E ("  pid {0,-7} no main window yet" -f $p.Id); continue }
    $sc = [System.Windows.Forms.Screen]::FromHandle($p.MainWindowHandle)
    # launch.ps1 places cylinder GPU N at X = N*1366 and back-wall GPU N at Y = N*768.
    if ($env:COMPUTERNAME -eq 'ORION') { $exp = [int][math]::Round($sc.Bounds.Y / 768) } else { $exp = [int][math]::Round($sc.Bounds.X / 1366) }
    if ($pg.ContainsKey($p.Id)) { $act = $pg[$p.Id] } else { $act = '?' }
    if ($act -eq '?') { $v = 'window only' } elseif ("$act" -eq "$exp") { $v = 'OK' } else { $v = 'CROSS-ADAPTER' }
    E ("  pid {0,-7} {1,-14} at {2},{3}  expect GPU {4}  smi GPU {5}  {6}" -f $p.Id, $sc.DeviceName, $sc.Bounds.X, $sc.Bounds.Y, $exp, $act, $v)
}
E ""

E "--- desktop screens ---"
foreach ($s in ([System.Windows.Forms.Screen]::AllScreens | Sort-Object { $_.Bounds.X }, { $_.Bounds.Y })) {
    if ($s.Primary) { $tag = '  (primary)' } else { $tag = '' }
    E ("  {0,-16} {1},{2} {3}x{4}{5}" -f $s.DeviceName, $s.Bounds.X, $s.Bounds.Y, $s.Bounds.Width, $s.Bounds.Height, $tag)
}
E ""

E "--- utilisation, $Samples samples $SampleIntervalSeconds s apart (index, gpu%, memMiB, smMHz) ---"
foreach ($i in 1..$Samples) {
    if ($smi) { & $smi --query-gpu=index,utilization.gpu,memory.used,clocks.sm --format=csv,noheader,nounits | ForEach-Object { E "  $_" } }
    E ""
    if ($i -lt $Samples) { Start-Sleep -Seconds $SampleIntervalSeconds }
}

$L | Out-File $OutFile -Encoding utf8
Write-Output "wrote $OutFile"
