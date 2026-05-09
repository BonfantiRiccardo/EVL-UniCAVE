<#
.SYNOPSIS
    Kills every running UniCAVE-GS instance on the current node.

.DESCRIPTION
    Get-Process returns every matching process (one per GPU on display nodes,
    plus the master's headless instance on CAVE2MASTER), so a single pipeline
    into Stop-Process -Force terminates all of them. After the kill we pause
    briefly and re-check, in case an instance was stuck in a driver wait, so
    the output accurately reflects what actually died.
#>
param(
    [string]$ExeName = 'UniCAVE-GS'
)

$procs = Get-Process -Name $ExeName -ErrorAction SilentlyContinue
if (-not $procs) {
    Write-Host "$env:COMPUTERNAME : no $ExeName running"
    return
}

$targetCount = @($procs).Count
$targetPids  = ($procs | ForEach-Object { $_.Id }) -join ','

$procs | Stop-Process -Force -ErrorAction SilentlyContinue

# Verify - Unity+D3D12+Netcode shutdown can take 1-3s to fully drain.
# Poll up to 3s before declaring anything "still alive".
$remaining = $null
$deadline  = (Get-Date).AddSeconds(3)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
    $remaining = Get-Process -Name $ExeName -ErrorAction SilentlyContinue
    if (-not $remaining) { break }
}

if ($remaining) {
    $rPids = ($remaining | ForEach-Object { $_.Id }) -join ','
    Write-Host "$env:COMPUTERNAME : tried to kill $targetCount ($targetPids), $(@($remaining).Count) still alive ($rPids)"
} else {
    Write-Host "$env:COMPUTERNAME : killed $targetCount instance(s) of $ExeName (pids $targetPids)"
}
