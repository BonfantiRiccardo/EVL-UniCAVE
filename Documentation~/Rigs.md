# Rigs

Each prefab is a complete CAVE: one root with the networking components,
one `Head`, and one `PhysicalDisplay` child per physical screen.

| Prefab | Rig | Screens | Stereo mode | Unity instances |
|---|---|---|---|---|
| `Prefabs/CAVE2.prefab` | EVL CAVE2 | 22 (columns A to R, plus 4 back-wall segments) | Row-interleaved passive | 22, spread over 7 nodes |
| `Prefabs/ARCADE2.prefab` | EVL ARCADE2 | 2 side-by-side 3D displays | Dual-pipe side-by-side | 2, both on one PC (2 GPUs) |
| `Prefabs/ARCADE.prefab` | ARCADE, single screen | 1 side-by-side 3D display | Dual-pipe side-by-side, exclusive fullscreen | 1 |
| `Prefabs/PROJECTOR.prefab` | Single 3D projector | 1 screen, 2.2 x 1.24 m | Dual-pipe side-by-side, exclusive fullscreen | 1 |

`Scenes/` has a ready example for three of them: `CAVE2.unity`,
`ARCADE.unity` (ARCADE2) and `Projector.unity`, each with its Network
Manager already wired.

## Prefab root

* **Keep the prefab at the scene root.** Navigation moves the prefab
  root (`CAVENavigator` on ARCADE2, `WandLocomotion` and
  `GamepadLocomotion` on CAVE2), and a non-identity parent transform
  corrupts the physical-to-world mapping the asymmetric projection
  assumes.
* **`NetworkInitialization` settings:**
  * `headMachineAsset` references the machine name asset for the
    machine that acts as server. When the running machine name matches
    this asset the instance starts as Netcode server; otherwise it
    starts as client. Shipped defaults: `CAVE2_MASTER` for CAVE2,
    `Arcade` (GALAGA1) for ARCADE2.
  * `serverAddress` and `serverPort` point at the server's reachable
    address. Both can be overridden at runtime via `serverAddress`/
    `serverPort` command-line arguments, which the bundled launchers
    always supply.
  * `targetFrameRate` should match the panel refresh rate. The shipped
    prefabs use 120 (CAVE2) and 240 (ARCADE2). This applies even on a
    head machine that renders nothing, where VSync has no effect.
* **Machine names decide what activates.** Every `PhysicalDisplay`
  compares its own `MachineName` asset against the running machine and
  deactivates itself when they differ, so one build runs everywhere and
  each process lights up exactly one screen. The running name comes
  from `System.Environment.MachineName` unless the
  `overrideMachineName <name>` argument is passed, which is how a
  single PC hosts several instances with different identities.

## CAVE2

`Prefabs/CAVE2.prefab` uses row-interleaved stereo on 7 nodes.

1. **No `PhysicalDisplayManager` is used in this prefab.** At runtime
   only the display whose name matches the `overrideMachineName`
   argument activates; the other 21 sit dormant.

2. **Verify per-display values are baked correctly.** For each child
   `PhysicalDisplay`:
   * `MachineName` references the right asset under
     `Prefabs/MachineNames/CAVE2/` (e.g. `ORION-03-GPU1.asset`).
   * `windowBounds` is the absolute Windows desktop rect that GPU's
     column or row should occupy at runtime. The
     `WindowsUtils.SetMyWindowInfo` call repositions the OS window to
     this rect after Unity finishes startup.
   * `width` and `height` are the panel's physical dimensions in
     **metres**, used by `Util.getAsymProjMatrix` for off-axis
     projection. Bake the per-instance column or row dimensions, since
     one Unity instance owns one column.
   * `head` references the prefab's `Head` child (one shared head, used
     by all displays).
   * `rowInterleaved = true`, `rowInterleaveSwapEyes` set per panel
     polarizer orientation (start with `false`; flip if a display shows
     mixed eyes).
   * `useRenderTextures = false`. The row-interleave path allocates its
     own per-eye RenderTextures sized to `windowBounds`; enabling
     `useRenderTextures` overwrites them with `renderTextureSize` ones
     and breaks the composite.

3. **No renderer setup is needed.** `PhysicalDisplay` adds a
   `RowInterleaveStereo` component to a compositing camera, and that
   component injects its own composite pass into URP. The shader sits
   in `Shaders/Resources/`, so player builds always include it.

4. **Set the tracking server** as described in
   [Launching](Launching.md#tracking-server).

5. **Add scene content** as siblings of `Head` and the displays or at
   hierarchy root, inside the CAVE2 prefab. Anything inside the prefab
   moves with the root, which `WandLocomotion`, `GamepadLocomotion` and
   the keyboard navigation in `UCNetwork` move at runtime; anything
   outside the prefab root stays in fixed world space.

6. **Build for Windows Standalone**, then deploy as described in
   [Launching](Launching.md).

## ARCADE2

`Prefabs/ARCADE2.prefab` is two 3D displays driven by two GPUs inside a
single PC (GALAGA1). Each display gets its own Unity process, and the two
talk over loopback rather than over the network. The prefab ships with
two `PhysicalDisplay` children:

| Display | Screen | `windowBounds.x` | MachineName asset | Machine name | Role |
|---|---|---|---|---|---|
| `DisplayA` | left (`\\.\DISPLAY1`, primary) | 0 | `MachineNames/Arcade.asset` | `GALAGA1` | server (head) |
| `DisplayB` | right (`\\.\DISPLAY5`) | 6720 | `MachineNames/Arcade2.asset` | `GALAGA1-B` | client |

The asset names are easy to misread: `Arcade.asset` is the first
instance and `Arcade2.asset` is the second one. The `2` counts
instances, it does not refer to the ARCADE2 room. Only `GALAGA1` is a
real hostname; `GALAGA1-B` is virtual and exists only because
`launch_arcade.ps1` passes `overrideMachineName GALAGA1-B` to the second
process.

1. **Confirm the networking fields** on the prefab root:
   `headMachineAsset = Arcade` (GALAGA1), `serverAddress = 127.0.0.1`,
   `serverPort = 6543`. Loopback is correct here because both processes
   are on the same PC.

2. **Confirm the stereo flags** on both `PhysicalDisplay` children:
   `is3D = true`, `dualPipe = true`, `dualInstance = false`,
   `rowInterleaved = false`, `useRenderTextures = false`.
   `dualInstance` stays false because each process drives a whole
   screen, both eyes included; it is only for setups that run one
   process per eye.

3. **Check the viewport split.** `windowBounds` is the full signal the
   display expects (6720x1890), and `leftViewport`/`rightViewport` cut
   it into two 3360x1890 halves that the panel sends to one eye each.
   The cameras render straight to the back buffer through
   `Camera.rect`, so no RenderTextures are involved.

4. **Fix eye order per panel if needed.** Which half feeds which eye
   depends on the individual display, so a panel can come up with the
   eyes swapped (depth looks inverted, or the image is uncomfortable to
   fuse). To correct it, swap the `x` values of `leftViewport` and
   `rightViewport` on that display alone. The shipped prefab already
   differs between the two: `DisplayB` uses left `x = 0` / right
   `x = 3360`, while `DisplayA` uses left `x = 3360` / right `x = 0`.

5. **Check the physical geometry.** Both displays are 4.2672 m by
   2.37744 m, placed at `x = +/-2.1336`, `z = -3`, rotated 180 degrees
   about Y so they face the viewer. These metre values feed the
   asymmetric projection, so they must describe the real screens or the
   perspective will be wrong regardless of the stereo settings.

6. **Set Input System background behavior.** Project Settings > Input
   System Package > **Background Behavior = Ignore Focus**. Windows
   routes the keyboard only to the foreground window, so without this
   the gamepad stops working whenever the head window loses focus.

7. **Launch with `CAVEScripts/launch_arcade.ps1`** rather than starting
   the exe by hand. Edit the `$instances` block if your hardware differs:
   `gpu` is the Unity `-force-device-index` and must be the GPU that
   physically drives the screen at `targetX`, and `targetX` must match
   that display's baked `windowBounds.x`. If a window comes up blurry or
   slow it is presenting cross-adapter, so swap the two `gpu` values.
   The script starts the head first, staggers the second launch to avoid
   a D3D device-creation race, and forces the head window to the
   foreground so keyboard input reaches it. To show the same frame on
   both screens, use `launch_arcade_sync.ps1` (see
   [Display sync](DisplaySync.md)).

## Building a rig of your own

Copy the closest prefab and edit it, or start from an empty GameObject
with `NetworkObject`, `UCNetwork`, `NetworkInitialization`, and
`CAVENavigator`, one child with `HeadConfiguration`, and one child per
screen with `PhysicalDisplay`.

### Choosing the stereo mode

`PhysicalDisplay` selects a rendering path from a few flags. Pick the
row matching your hardware and leave the other flags false.

| Hardware | Flags | Extra setup |
|---|---|---|
| 2D screen or projector | `is3D = false` | One centre camera, nothing else |
| Micropolarized passive panel | `is3D`, `rowInterleaved` | Keep `useRenderTextures` false; no renderer setup |
| Side-by-side 3D display | `is3D`, `dualPipe` | Set `windowBounds` to the full signal and split it with `leftViewport`/`rightViewport` |
| One process per eye | `is3D`, `dualPipe`, `dualInstance` | Pass `eye left` or `eye right` on the command line; each process sizes its window from its own viewport |
| Quad-buffered stereo | `is3D` only | Requires XR Plugin Management with a quad-buffer loader |

### Per-display fields that matter

* `machineNameAsset`: which machine or virtual instance owns this
  screen. Create new assets under `Prefabs/MachineNames/` via
  **Create > UniCAVE > Machine Name** and set `_name` to the hostname,
  or to the value you will pass as `overrideMachineName`.
* `width` and `height`: the screen's real size in metres. These drive
  the off-axis projection and are unrelated to resolution.
* `head`: the shared `HeadConfiguration` child. All displays in a rig
  point at the same one, so the eye positions match across nodes.
* `windowBounds`: absolute Windows desktop rect for this process's
  window. On a multi-screen desktop it places the window on the right
  panel.
* The display's transform position and rotation must describe where the
  screen physically sits relative to the viewer, in metres, with the
  screen facing inward.

### How many instances

One Unity process per screen. Give each screen a distinct machine name,
then launch each process with `overrideMachineName <that name>` plus
`-force-device-index` for the GPU driving it. Exactly one machine name
must match `headMachineAsset`, and that process becomes the server.
Model your launcher on `launch.ps1` (multiple nodes) or
`launch_arcade.ps1` (multiple instances on one PC).
