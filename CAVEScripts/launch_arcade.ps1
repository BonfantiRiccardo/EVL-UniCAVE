<#
.SYNOPSIS
    Launches the two ARCADE Unity instances on ONE PC (2 GPUs driving 2
    side-by-side 3D displays) over loopback.

    One instance is the head (Netcode server): it renders its screen AND reads
    the keyboard/gamepad, moves the shared UniCAVE root, and broadcasts the
    resulting pose. The other instance is a client: it connects to 127.0.0.1,
    receives the pose, and renders its screen. Only the head reads input; the
    client mirrors it. No VRPN, no per-instance input.

    The script finds the exe next to itself, so it works from any deploy path
    (CopyLaunchScripts.cs copies it into every build). If $exeName is empty,
    the only player exe in the folder is used.

    UniCAVE custom args: no dash prefix (Util.GetArg parses raw tokens).
    Unity built-in args:  dash prefix.

.PARAMETER ExtraArgs
    Extra command-line arguments for both instances, as one string,
    for example -ExtraArgs "-logStats -myFlag 3".

.PARAMETER Sync
    Lock both screens to the same frame: passes "frameLock 1 swapBarrier 1"
    (pose lockstep plus the NVIDIA present barrier, VSync on). Needs the
    framelocked Sync board; frame rate is capped at the display refresh, so
    never use it for timing runs. Single flags go through -ExtraArgs.
#>
[CmdletBinding()]
param(
    [string]$ExtraArgs = '',
    [switch]$Sync
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

# ---------------------------------------------------------------------------
# CONFIG
#
# List the HEAD first so its server binds before the client connects.
#
#   name    : overrideMachineName. Must match a PhysicalDisplay MachineName in
#             ARCADE2.prefab. GALAGA1 is the head (matches headMachine);
#             GALAGA1-B becomes the client.
#   gpu     : Unity -force-device-index. This MUST be the GPU that physically
#             drives the screen sitting at 'targetX'. If a window comes up
#             blurry/slow it is presenting cross-adapter: swap gpu 0 and 1.
#             (One-instance visual test: launch a single instance and see which
#             screen lights up crisp.)
#   targetX : the desktop X of that instance's screen. It MUST match the
#             display's baked windowBounds.x in ARCADE2.prefab:
#               GALAGA1   -> 0     (left  screen, \\.\DISPLAY1, primary)
#               GALAGA1-B -> 6720  (right screen, \\.\DISPLAY5)
#             The -monitor index and screen size are auto-resolved from targetX.
# ---------------------------------------------------------------------------
$serverAddress = '127.0.0.1'
$serverPort    = 6543
$exeName       = ''   # empty: the only player exe next to this script
$staggerMs     = 0    # delay after each instance; 0 starts head and client together

$instances = @(
    @{ name = 'GALAGA1';   gpu = 0; targetX = 0    },   # head / server / left  screen
    @{ name = 'GALAGA1-B'; gpu = 1; targetX = 6720 }    # client        / right screen
)

# ---------------------------------------------------------------------------
# Foreground helper: give the head window keyboard focus after launch.
# Windows routes keyboard only to the foreground window; the gamepad works
# regardless of focus once Input System Background Behavior = Ignore Focus is
# set, but keyboard nav needs the head focused. Finds the head window by PID +
# class 'UnityWndClass*' (same filter as WindowsUtil.FindMyMainWindow) and
# forces it foreground (AttachThreadInput works around the foreground lock).
# ---------------------------------------------------------------------------
Add-Type -Namespace Arcade -Name Fg -Language CSharp -MemberDefinition @'
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

# Resolve Unity's -monitor index and the screen size for a desktop X by sorting
# the active screens left to right (Unity typically enumerates in the same
# order). Returns @{ monitor = 1-based index; w; h }.
function Resolve-Screen([int]$targetX) {
    $screens = @([System.Windows.Forms.Screen]::AllScreens | Sort-Object { $_.Bounds.X })
    for ($i = 0; $i -lt $screens.Count; $i++) {
        if ($screens[$i].Bounds.X -eq $targetX) {
            return @{ monitor = $i + 1; w = $screens[$i].Bounds.Width; h = $screens[$i].Bounds.Height }
        }
    }
    return $null
}

function Start-ArcadeInstance($inst) {
    $screen = Resolve-Screen $inst.targetX
    if (-not $screen) { throw "no screen found at desktop X=$($inst.targetX); check AllScreens output." }

    $logFile = Join-Path $logDir "$($inst.name).log"
    $argList = @(
        'overrideMachineName', $inst.name,
        'serverAddress',       $serverAddress,
        'serverPort',          $serverPort,
        '-logFile',            $logFile,
        '-force-device-index', $inst.gpu,
        '-monitor',            $screen.monitor,
        '-popupwindow',
        '-screen-width',       $screen.w,
        '-screen-height',      $screen.h
    )
    if ($Sync) { $argList += @('frameLock', '1', 'swapBarrier', '1') }
    $argList += $extra

    $p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru
    Write-Host "[ARCADE] $($inst.name) pid=$($p.Id) gpu=$($inst.gpu) monitor=$($screen.monitor) size=$($screen.w)x$($screen.h) log=$logFile"
    return $p
}

if ($Sync) { Write-Host "[ARCADE] sync on: frameLock 1 swapBarrier 1 (VSync, capped at the refresh rate)" }

$procs = @()
foreach ($inst in $instances) {
    $procs += (Start-ArcadeInstance $inst)
    if ($staggerMs -gt 0) { Start-Sleep -Milliseconds $staggerMs }
}

# Bring the head (first instance) to the foreground so keyboard nav reaches it.
$headPid = $procs[0].Id
if ([Arcade.Fg]::FocusProcess($headPid, 8000)) {
    Write-Host "[ARCADE] head window (pid=$headPid) brought to foreground for keyboard input."
} else {
    Write-Host "[ARCADE] could not foreground the head window; click the head screen once if keyboard nav does not respond."
}
