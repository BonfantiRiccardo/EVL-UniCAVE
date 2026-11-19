# Display sync (ARCADE2)

Each ARCADE2 screen is driven by its own GPU and its own Unity process.
By default each process presents whenever its frame is ready and renders
the newest pose it has received, so during motion the two screens can
show different moments, which appears as a break at the bezel. Three
mechanisms remove it:

1. **Framelock** aligns the refresh of both GPUs. It is configured once
   in the NVIDIA Control Panel (Workstation > Synchronize displays) with
   an NVIDIA Sync board; no application code is involved.
2. **Swap barrier** (`SwapBarrier`, argument `swapBarrier 1`) joins each
   process's swap chain to the NVIDIA present barrier, so both GPUs flip
   on the same refresh.
3. **Frame lock** (`FrameLock`, argument `frameLock 1`) keeps the
   processes in lockstep over a TCP channel on `serverPort + 1`. Every
   frame the head sends the rig pose and renders only once every client
   has applied it, so all GPUs render the same pose at the same time. A
   client waits for every pose and shows each one once, in order. With
   `swapBarrier 1` the head also chooses the frame at which all processes
   join the barrier, and checks that every screen shows each pose at the
   same refresh (see below).

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
a Sync board with framelock enabled, a Windows player on Direct3D 12,
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
| `frameLockAutoAlign 0` | Measure and report the frame pairing, but do not correct it |

## Checking that it works

Each player log reports, in this order:

* `SwapBarrier: adapter check OK`. A `CROSS-ADAPTER` error means the
  process renders on one GPU and presents on another; correct the `gpu`
  values in `launch_arcade.ps1`.
* On the head, `FrameLock: ... in lockstep from state N`, then
  `FrameLock: all 2 instances join the present barrier at state N`.
* Every few seconds, `SwapBarrier: joined, sync mode SYNC_CLUSTER`, with
  `in sync` growing as fast as `presents`. `SYNC_CLIENT` means that
  Windows composites the window instead of flipping it, or that the other
  process has not joined. With one GPU per screen, `SYNC_SYSTEM` means
  that the Sync board does not lock the two refreshes.
* On the head, once: `FrameLock: ... shows every pose at the same
  refresh as the head`. One or two `pairing step` lines may come first.
* On the head, every few seconds: `FrameLock head: ... acknowledgement
  wait mean X ms ..., barrier head SYNC_CLUSTER, client SYNC_CLUSTER, ...,
  first client shows each pose 0.00 ms after the head, at the same
  refresh for 100% of N poses`. The wait should stay well below a
  millisecond. When frames miss refreshes, the line also counts some
  `vsync time(s) off the refresh grid ignored`, which is harmless.

On the screens, move the rig sideways at speed: when the screens are in
sync, the image stays continuous across the bezel.

## Frame pairing

The barrier makes both GPUs flip together, but which pose each GPU shows
at a given flip depends on how many frames each process had queued when
the barrier took hold. A process with one more frame in its queue shows
every pose one refresh later, which appears as a break at the seam during
motion. The barrier keeps that pairing until one process presents a frame
more or less than the other.

The lockstep keeps the queues equal, because neither process can run
ahead of the other. The head also measures the pairing. The plugin reads
the DXGI frame statistics of each swap chain, which give the vsync at
which every frame reached the screen. Each client reports which pose it
showed at which vsync, and for every pose the head takes the client's
vsync time minus its own; both processes run on one PC and share the QPC
clock. When the Sync board locks the refreshes, this difference is a
whole number of refreshes, and it must be zero. When frames miss
refreshes, the DXGI times can lie a few milliseconds off the refresh
grid although both screens flip together. The head therefore uses only
differences within 1 ms of a whole number of refreshes, and its report
counts the others as ignored.

The head decides on windows of 30 poses, once every barrier has been in
sync for half a second. When a client shows the poses a whole number of
refreshes late, the head renders one frame without sending a pose: the
client's extra queued frame flips with it, and the client comes one
refresh closer. When a client shows the poses early, it shows its current
pose once more. The head takes one step at a time and measures again
after each one (`FrameLock: pairing step N ...` in the head's log). After
6 steps within a minute it stops and asks for the logs.

The barrier reports whether the refreshes are locked. When any process
reports `SYNC_SYSTEM` instead of `SYNC_CLUSTER`, the barrier still pairs
the presents, but the two screens refresh at different moments, and no
frame pairing can make them show a pose together. The head then warns
every 30 seconds (`... the refreshes of the GPUs are not locked ...`)
and does not correct. Check NVIDIA Control Panel > Workstation >
Synchronize displays: one display is the timing server, and the other
one is locked to it.

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
