<#
.SYNOPSIS
    Launches the two ARCADE Unity instances on ONE PC (2 GPUs driving 2
    side-by-side 3D displays) over loopback.

    One instance is the head (Netcode server): it renders its screen AND reads
    the keyboard/gamepad, moves the shared UniCAVE root, and broadcasts the
    resulting pose. The other instance is a client: it connects to 127.0.0.1,
    receives the pose, and renders its screen. Only the head reads input; the
    client mirrors it. No VRPN, no per-instance input.

    Script auto-detects its build folder from $PSScriptRoot, so it finds the exe
    next to itself regardless of deploy path (bundled by CopyLaunchScripts.cs).

    UniCAVE custom args: no dash prefix (Util.GetArg parses raw tokens).
    Unity built-in args:  dash prefix.
#>
[CmdletBinding()]
param(
    # Session D only. Turns on replay mode and per-frame temporal capture. Leave it off for
    # sessions A, B2 and E: replay makes the path advance per rendered frame instead of by the
    # clock, so any frame time measured with it on is meaningless.
    [switch]$Temporal
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
#               GALAGA1   -> 6720  (right screen, \\.\DISPLAY5)
#               GALAGA1-B -> 0     (left  screen, \\.\DISPLAY1, primary)
#             The -monitor index and screen size are auto-resolved from targetX.
# ---------------------------------------------------------------------------
$serverAddress = '127.0.0.1'
$serverPort    = 6543
$exeName       = 'UniCAVE-GS.exe'
$staggerMs     = 2000

$instances = @(
    @{ name = 'GALAGA1';   gpu = 1; targetX = 6720 },   # head / server / right screen
    @{ name = 'GALAGA1-B'; gpu = 0; targetX = 0    }    # client        / left  screen
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
$exe    = Join-Path $scriptDir $exeName
$logDir = Join-Path $scriptDir 'Logs'

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
        '-gs-capture-nodes',   'all',
        '-gs-viewdata',
        '-monitor',            $screen.monitor,
        '-popupwindow',
        '-screen-width',       $screen.w,
        '-screen-height',      $screen.h
    )
    if ($Temporal) { $argList += @('-gs-temporal', '-gs-replay') }

    $p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru
    Write-Host "[ARCADE] $($inst.name) pid=$($p.Id) gpu=$($inst.gpu) monitor=$($screen.monitor) size=$($screen.w)x$($screen.h) log=$logFile"
    return $p
}

$procs = @()
foreach ($inst in $instances) {
    $procs += (Start-ArcadeInstance $inst)
    Start-Sleep -Milliseconds $staggerMs   # stagger to avoid a D3D device-creation race
}

# Bring the head (first instance) to the foreground so keyboard nav reaches it.
$headPid = $procs[0].Id
if ([Arcade.Fg]::FocusProcess($headPid, 8000)) {
    Write-Host "[ARCADE] head window (pid=$headPid) brought to foreground for keyboard input."
} else {
    Write-Host "[ARCADE] could not foreground the head window; click the head screen once if keyboard nav does not respond."
}
