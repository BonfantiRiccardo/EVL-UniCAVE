<#
.SYNOPSIS
    Starts ARCADE2 with both screens locked to the same frame: runs
    launch_arcade.ps1 -Sync (pose lockstep plus the NVIDIA present barrier).
    Works with right-click > Run with PowerShell.

    The frame rate is capped at the display refresh, so use launch_arcade.ps1
    for timing runs.
#>
& (Join-Path $PSScriptRoot 'launch_arcade.ps1') -Sync
