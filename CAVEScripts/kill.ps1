<#
.SYNOPSIS
    Kills every running player instance on the current node.

.DESCRIPTION
    Without -ExeName, kills every process whose exe lives in this script's
    folder or below it, so the same script works next to a build and one level
    above the build folders. With -ExeName (process name without .exe), kills
    every process of that name instead.

    A single pipeline into Stop-Process -Force terminates all instances (one
    per GPU on display nodes, plus the master's instance). After the kill we
    pause briefly and re-check, in case an instance was stuck in a driver wait,
    so the output accurately reflects what actually died.
#>
param(
    [string]$ExeName = ''
)

function Get-Targets {
    if ($ExeName) { return @(Get-Process -Name $ExeName -ErrorAction SilentlyContinue) }
    $root = $PSScriptRoot.TrimEnd('\') + '\'
    return @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase) })
}

$label = if ($ExeName) { $ExeName } else { "players under $PSScriptRoot" }

$procs = Get-Targets
if ($procs.Count -eq 0) {
    Write-Host "$env:COMPUTERNAME : no $label running"
    return
}

$targetCount = $procs.Count
$targetPids  = ($procs | ForEach-Object { $_.Id }) -join ','

$procs | Stop-Process -Force -ErrorAction SilentlyContinue

# Verify - Unity+D3D12+Netcode shutdown can take 1-3s to fully drain.
# Poll up to 3s before declaring anything "still alive".
$remaining = @()
$deadline  = (Get-Date).AddSeconds(3)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
    $remaining = Get-Targets
    if ($remaining.Count -eq 0) { break }
}

if ($remaining.Count -gt 0) {
    $rPids = ($remaining | ForEach-Object { $_.Id }) -join ','
    Write-Host "$env:COMPUTERNAME : tried to kill $targetCount ($targetPids), $($remaining.Count) still alive ($rPids)"
} else {
    Write-Host "$env:COMPUTERNAME : killed $targetCount instance(s) of $label (pids $targetPids)"
}
