# Display sync (ARCADE2)

Each ARCADE2 screen is driven by its own GPU and its own Unity process.
By default each process presents whenever its frame is ready and renders
the newest pose it has received, so during motion the two screens can
show different moments, which appears as a break at the bezel. Three
mechanisms remove it:

1. **Framelock** aligns the refresh of both GPUs. It is configured once
   in the NVIDIA Control Panel (Sync tab) with an NVIDIA Sync board; no
   application code is involved.
2. **Swap barrier** (`SwapBarrier`, argument `swapBarrier 1`) joins each
   process's swap chain to the NVIDIA present barrier, so both GPUs flip
   on the same refresh.
3. **Frame lock** (`FrameLock`, argument `frameLock 1`) sends the rig
   pose from the head to every client once per frame, over a TCP channel
   on `serverPort + 1`. The head never waits for the clients. While the
   barrier is in sync, a client waits for the next pose and shows every
   pose once and in order, so the n-th flip after the join shows the
   same pose on every GPU. Otherwise it waits up to `frameLockWaitMs`
   (default 8 ms) for a new pose and shows the newest one, or reuses the
   previous pose. With `swapBarrier 1` the head also chooses the frame
   at which all processes join the barrier, and corrects the frame
   pairing (see below).

Both components attach themselves to the `UCNetwork` object when their
argument is present, and both turn VSync on, so the frame rate is capped
at the display refresh rate. Do not use them for timing measurements.
Without the arguments nothing changes.

These mechanisms are for screens on different GPUs. When all screens
hang off one GPU, as on CAVERN, render them from one process instead
(see [Rigs](Rigs.md#how-many-instances)): one process shows the same
frame on every screen without any sync.

## Requirements

NVIDIA RTX/Quadro class GPUs (not GeForce) with driver 472.12 or newer,
a Sync board with framelock configured, a Windows player on Direct3D 12,
and each process on the GPU that drives its screen. The window must
cover its screen exactly, without borders or desktop scaling, which the
`-popupwindow` launch provides.

## Launch

`.\launch_arcade.ps1 -Sync` passes `frameLock 1 swapBarrier 1` and the
ARCADE2 pairing target `frameLockTargetMs -8.5` (see Frame pairing) to
both instances. Right-click > Run with PowerShell cannot pass
parameters, so `launch_arcade_sync.ps1` next to it does it for you.
`launch_arcade.ps1` on its own stays unsynced, which is what timing runs
need. Other options go through `-ExtraArgs`, for example
`.\launch_arcade.ps1 -Sync -ExtraArgs "frameLockAutoAlign 0"`:

| Argument | Effect |
|---|---|
| `frameLock 1` | Pose lockstep between the head and the clients |
| `swapBarrier 1` | Join the NVIDIA present barrier |
| `frameLockPort N` | Port of the frame-lock channel (default `serverPort + 1`) |
| `frameLockWaitMs N` | How long a client waits for a new pose while the barrier is not in sync (default 8) |
| `frameLockAutoAlign 0` | Turn off the automatic frame-pairing correction; the keys still work |
| `frameLockTargetMs N` | Pairing target: how many ms after the head's GPU a client's GPU should show each pose, negative for before (default 0) |

## Checking that it works

Each player log reports, in this order:

* `SwapBarrier: adapter check OK`. A `CROSS-ADAPTER` error means the
  process renders on one GPU and presents on another; correct the `gpu`
  values in `launch_arcade.ps1`.
* On the head, `FrameLock: ... in lockstep from state N`, then
  `FrameLock: all 2 instances join the present barrier at state N`.
* Every few seconds, `SwapBarrier: joined, sync mode SYNC_SYSTEM` (or
  `SYNC_CLUSTER`), with `in sync` growing as fast as `presents`.
  `SYNC_CLIENT` means that Windows composites the window instead of
  flipping it, or that the other process has not joined.
* On the head, every few seconds, `first client shows each pose X ms
  before the head (target 8.50 ms before, on target for N% of M poses)`.
  N is close to 100% once the pairing matches the target, and X stays
  within half a refresh period of the target.

On the screens, move the rig sideways at speed: when the screens are in
sync, the image stays continuous across the bezel.

## Frame pairing

The barrier makes both GPUs flip together, but which pose each GPU shows
at a given flip depends on how the two processes lined up when they
joined, so a launch can start one frame or more apart. A one-frame
offset shows as a break at the seam during motion. The plugin reads the
DXGI frame statistics of each swap chain, which give the vsync at which
every frame reached the screen. Each client reports to the head which
pose it showed at which vsync, and the head computes, for every pose,
the client's display time minus its own. It rounds the distance between
that difference and `frameLockTargetMs` to whole refresh periods, and
once that count is stable and not zero for half a second it corrects
the pairing (`FrameLock: pairing correction ...` in the head's log).

The target is a property of the display setup. When both GPUs report
the same vsync times, 0 is right. On ARCADE2 the DXGI vsync times of
the two GPUs differ by 7 to 9 ms (constant during a launch, different
between launches), and the seam is clean when the right GPU shows each
pose 8 to 10 ms before the left one, so `launch_arcade.ps1 -Sync`
passes `frameLockTargetMs -8.5`. To find the value for another setup,
move the rig sideways, press `]` or `[` on the head until the seam stays
continuous during motion, and take the value from the head's log
(`FrameLock: pairing target now X ms`).

A correction only ever delays the side that is ahead: when a client is
behind, the head keeps sending the newest pose but shows its own image
one frame later; when a client is ahead, the head removes that delay, or
the client shows its previous pose once more. Advancing the side that is
behind would leave it waiting a whole frame for a pose that does not
exist yet, which drops both screens below the refresh rate. The
correction repeats only if the pairing drifts again.

## Keys

| Key | Effect |
|---|---|
| `]` | The head shows its image one frame later and moves the pairing target with it |
| `[` | The head removes one frame of image delay, or the clients show a pose twice, and moves the pairing target with it |

If the seam breaks during motion, press `]`; if the break gets worse,
press `[` twice. Like every key, they work on the head only.

## Building the plugin

`Plugins/x86_64/UniCAVESwapSync.dll` is built from `Native~/SwapSync/`
with CMake and Visual Studio 2022. It needs the NVAPI SDK (MIT-licensed
headers and libraries, https://github.com/NVIDIA/nvapi) and the Unity
plugin headers in `<Unity install>/Editor/Data/PluginAPI`:

```powershell
cmake -S Native~/SwapSync -B C:\build\swapsync -A x64 -DNVAPI_DIR=C:/path/to/nvapi
cmake --build C:\build\swapsync --config Release
```

The build copies the DLL into `Plugins/x86_64/`. Use a short build
folder, because MSBuild's file tracker fails on paths longer than 260
characters. The DLL is excluded from the Editor, so the Editor never
locks it, and players load it only when `swapBarrier 1` is passed.
