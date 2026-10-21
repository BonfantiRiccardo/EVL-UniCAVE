<#
.SYNOPSIS
    Launches CAVERN as ONE Unity instance that renders all three screens in a
    single window spanning the three monitors (the DisplayManager in
    CAVERN.prefab), so the screens never compete for the shared GPU. The
    instance is also the Netcode server and reads the input. Works with
    right-click > Run with PowerShell.

    UniCAVE custom args: no dash prefix (Util.GetArg parses raw tokens).
    Unity built-in args:  dash prefix.

.PARAMETER ExtraArgs
    Extra command-line arguments for the instance, as one string,
    for example -ExtraArgs "-logStats -myFlag 3".
#>
[CmdletBinding()]
param(
    [string]$ExtraArgs = ''
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# CONFIG
#
#   instanceName : overrideMachineName. Must match the DisplayManager's
#                  MachineName in CAVERN.prefab (also its headMachineAsset).
#   screenX      : desktop X of the left, center and right monitors. They MUST
#                  match the displays' baked windowBounds.x in CAVERN.prefab.
# ---------------------------------------------------------------------------
$serverAddress = '127.0.0.1'
$serverPort    = 7568
$exeName       = ''   # empty: the only player exe next to this script
$readyWaitSec  = 120  # max wait for the instance to place its window
$gpu           = 0    # one GPU drives all three monitors
$instanceName  = 'CAVERN-HEAD'
$screenX       = @(0, 1920, 3840)

# Finds the head window by PID + class 'UnityWndClass*' and forces it foreground for keyboard input.
Add-Type -Namespace Cavern -Name Fg -Language CSharp -MemberDefinition @'
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int max);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    static IntPtr _found;
    static uint _wantPid;

    static bool Match(IntPtr h, IntPtr l) {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if (pid != _wantPid) return true;
        if (!IsWindowVisible(h)) return true;
        System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        if (sb.ToString().StartsWith("UnityWndClass")) { _found = h; return false; }
        return true;
    }

    public static bool FocusProcess(int pid, int timeoutMs) {
        _wantPid = (uint)pid;
        EnumWindowsProc cb = new EnumWindowsProc(Match);
        System.DateTime end = System.DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (System.DateTime.UtcNow < end) {
            _found = IntPtr.Zero;
            EnumWindows(cb, IntPtr.Zero);
            if (_found != IntPtr.Zero) { ForceForeground(_found); return true; }
            System.Threading.Thread.Sleep(200);
        }
        return false;
    }

    static void ForceForeground(IntPtr h) {
        IntPtr fg = GetForegroundWindow();
        uint dummy;
        uint fgThread = GetWindowThreadProcessId(fg, out dummy);
        uint thisThread = GetCurrentThreadId();
        ShowWindow(h, 5); // SW_SHOW
        BringWindowToTop(h);
        if (fgThread != thisThread) AttachThreadInput(thisThread, fgThread, true);
        SetForegroundWindow(h);
        if (fgThread != thisThread) AttachThreadInput(thisThread, fgThread, false);
    }
'@

# Monitor bounds in physical pixels; Windows PowerShell is DPI-unaware and gets scaled bounds otherwise.
Add-Type -Namespace Cavern -Name Monitors -Language CSharp -UsingNamespace System.Collections.Generic -MemberDefinition @'
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public class Mon { public string Name; public int X, Y, W, H; }

    delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX mi);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

    public static Mon[] All() {
        List<Mon> list = new List<Mon>();
        IntPtr old = SetThreadDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2
        try {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr dc, ref RECT r, IntPtr d) {
                MONITORINFOEX mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (GetMonitorInfo(h, ref mi)) {
                    Mon m = new Mon();
                    m.Name = mi.szDevice;
                    m.X = mi.rcMonitor.Left;
                    m.Y = mi.rcMonitor.Top;
                    m.W = mi.rcMonitor.Right - mi.rcMonitor.Left;
                    m.H = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                    list.Add(m);
                }
                return true;
            }, IntPtr.Zero);
        } finally {
            if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old);
        }
        return list.ToArray();
    }
'@

# ---------------------------------------------------------------------------
$scriptDir = Split-Path -Parent $PSCommandPath
if ($exeName) {
    $exe = Join-Path $scriptDir $exeName
} else {
    $found = @(Get-ChildItem -Path $scriptDir -Filter '*.exe' -File |
        Where-Object { $_.Name -notlike 'UnityCrashHandler*' })
    if ($found.Count -ne 1) { throw "expected one player exe in $scriptDir, found $($found.Count); set `$exeName" }
    $exe = $found[0].FullName
}
$logDir = Join-Path $scriptDir 'Logs'
$extra  = @($ExtraArgs -split '\s+' | Where-Object { $_ })

if (-not (Test-Path $exe))    { throw "exe not found: $exe - build/sync first." }
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

# Unity's -monitor number (1-based position in Windows' monitor enumeration) and the screen size for a desktop X.
function Resolve-Screen([int]$targetX) {
    $all = @([Cavern.Monitors]::All())
    for ($i = 0; $i -lt $all.Count; $i++) {
        if ($all[$i].X -eq $targetX) { return @{ monitor = $i + 1; w = $all[$i].W; h = $all[$i].H } }
    }
    return $null
}

# Waits until the instance logs its first WindowsUtils line (window placed); returns $null or the problem.
function Wait-InstanceReady($proc, [string]$logFile) {
    $deadline = (Get-Date).AddSeconds($readyWaitSec)
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { return 'exited during startup' }
        try {
            $fs = [System.IO.File]::Open($logFile, 'Open', 'Read', 'ReadWrite')
            try { $text = (New-Object System.IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
            if ($text.Contains('WindowsUtils:')) { return $null }
        } catch [System.IO.IOException] { }
        Start-Sleep -Milliseconds 500
    }
    return "did not place its window within $readyWaitSec s"
}

# The window spans from the left monitor to the right edge of the last one; every monitor must exist.
$screens = @()
foreach ($x in $screenX) {
    $s = Resolve-Screen $x
    if (-not $s) {
        $list = ([Cavern.Monitors]::All() | ForEach-Object { "$($_.Name) X=$($_.X) Y=$($_.Y) $($_.W)x$($_.H)" }) -join '; '
        throw "no screen found at desktop X=$x. Screens: $list"
    }
    $screens += $s
}
$winW = $screenX[-1] + $screens[-1].w - $screenX[0]
$winH = $screens[0].h

$logFile = Join-Path $logDir "$instanceName.log"
Remove-Item -LiteralPath $logFile -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $logFile) { throw "$logFile is in use; close the running instance first (kill.ps1)." }
$argList = @(
    'overrideMachineName', $instanceName,
    'serverAddress',       $serverAddress,
    'serverPort',          $serverPort,
    '-logFile',            $logFile,
    '-force-device-index', $gpu,
    '-monitor',            $screens[0].monitor,
    '-popupwindow',
    '-screen-fullscreen',  0,   # start windowed: the Player Settings default is Exclusive Fullscreen
    '-screen-width',       $winW,
    '-screen-height',      $winH
)
$argList += $extra

$p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru
Write-Host "[CAVERN] $instanceName pid=$($p.Id) gpu=$gpu monitor=$($screens[0].monitor) window=${winW}x${winH} log=$logFile"

$problem = Wait-InstanceReady $p $logFile
if ($problem) {
    Write-Host "[CAVERN] $instanceName $problem. See $logFile"
} else {
    Write-Host "[CAVERN] $instanceName ready."
}

if ([Cavern.Fg]::FocusProcess($p.Id, 8000)) {
    Write-Host "[CAVERN] window (pid=$($p.Id)) brought to foreground for keyboard input."
} else {
    Write-Host "[CAVERN] could not foreground the window; click a screen once if keyboard nav does not respond."
}
