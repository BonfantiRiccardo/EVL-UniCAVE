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
   on `serverPort + 1`. While the barrier is in sync, a client waits for
   the next pose and shows every pose once and in order, so the n-th flip
   after the join shows the same pose on every GPU. Otherwise it waits up
   to `frameLockWaitMs` (default 8 ms) for a new pose and shows the newest
   one, or reuses the previous pose. With `swapBarrier 1` the head also
   chooses the frame at which all processes join the barrier, and sets
   the frame pairing with a one-time hold (see below). Apart from that
   hold, the head never waits for the clients.

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

`.\launch_arcade.ps1 -Sync` passes `frameLock 1 swapBarrier 1` to both
instances, which start at the same time. Right-click > Run with
PowerShell cannot pass parameters, so `launch_arcade_sync.ps1` next to
it does it for you. `launch_arcade.ps1` on its own stays unsynced, which
is what timing runs need. Other options go through `-ExtraArgs`, for
example `.\launch_arcade.ps1 -Sync -ExtraArgs "frameLockAutoAlign 0"`:

| Argument | Effect |
|---|---|
| `frameLock 1` | Pose lockstep between the head and the clients |
| `swapBarrier 1` | Join the NVIDIA present barrier |
| `frameLockPort N` | Port of the frame-lock channel (default `serverPort + 1`) |
| `frameLockWaitMs N` | How long a client waits for a new pose while the barrier is not in sync (default 8) |
| `frameLockAutoAlign 0` | Turn off the hold and the automatic pairing correction; the keys still work |
| `frameLockTargetMs N` | Skip the hold and keep a fixed pairing target instead: how many ms after the head's GPU a client's GPU should show each pose, negative for before |

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
* On the head, once, `FrameLock: held the head for N ms before state K`,
  then `... keeps the pairing from the hold; it shows each pose X ms
  before the head`.
* On the head, every few seconds, `first client shows each pose X ms
  before the head (kept from the hold: X ms before, on target for N% of
  M poses)`, with N close to 100%.

On the screens, move the rig sideways at speed: when the screens are in
sync, the image stays continuous across the bezel.

## Frame pairing

The barrier makes both GPUs flip together, but which pose each GPU shows
at a given flip depends on how many frames each process still had queued
when they joined, so a launch can start one frame or more apart. A
one-frame offset shows as a break at the seam during motion.

The head therefore holds once. When every instance has been in sync for
10 frames, the head stops before it sends the next pose, waits until
every client has applied the previous pose, and then waits 6 more frames,
so that every present queue is empty. The head and the clients then
render the next pose together, and the barrier flips it on all screens
at the same refresh, whatever the pairing was before. The hold freezes
the image for about 0.1 s, once, about a second after both screens are
up (`FrameLock: held the head for N ms ...` in the head's log).

After the hold the head keeps that pairing. The plugin reads the DXGI
frame statistics of each swap chain, which give the vsync at which every
frame reached the screen. Each client reports to the head which pose it
showed at which vsync, and the head computes, for every pose, the
client's display time minus its own. The median of the first samples
after the hold becomes the reference. Later, the head rounds the distance
between each difference and the reference to whole refresh periods, and
when that count is stable and not zero for half a second it corrects the
pairing (`FrameLock: pairing correction ...` in the head's log).

The difference itself does not say whether the pairing is right: on
ARCADE2 the DXGI vsync times of the two GPUs differ by an amount that
changes between launches (from 0.2 to 9 ms so far). The hold sets the
pairing without relying on these times. `frameLockTargetMs N` skips the
hold and keeps the difference within half a refresh period of N instead,
for setups where the hold is not wanted.

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
| `]` | The head shows its image one frame later and moves the reference (or the target) with it |
| `[` | The head removes one frame of image delay, or the clients show a pose twice, and moves the reference (or the target) with it |

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
