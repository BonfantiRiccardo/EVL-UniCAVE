# MyUniCAVE

Fork of [UniCAVE](https://github.com/livingenvironmentslab/UniCAVE) 2.0.1
(UW Madison Living Environments Lab) ported to Unity 6 with Netcode for
GameObjects and URP. Adds:

* **Row-interleaved passive stereo** for EVL's CAVE2 Planar polarized panels
  (per-display flags; the composite pass needs no renderer setup).
* **Multi-instance window repositioning** that works on cluster nodes
  spawning more than one Unity process at a time.
* **CAVE2 cluster prefab** (`Prefabs/CAVE2.prefab`) wired for a 7-node /
  22-GPU layout, plus pre-baked `MachineName` ScriptableObject assets for
  every virtual instance.
* **ARCADE2 prefab** (`Prefabs/ARCADE2.prefab`) wired for two side-by-side
  3D displays driven by two GPUs in a single PC.
* **Runtime stereo/mono toggle** (F5) broadcast from the head machine, so
  every node switches together.
* **PowerShell launcher templates** (`CAVEScripts/`) copied next to the
  built `.exe` by an editor post-build hook, so a synced build folder on a
  cluster node is fully self-contained.

Upstream stopped active maintenance around July 2023 against Unity
2019.4 / 2021 LTS and the Multiplayer HLAPI; opening it in Unity 6
produces compile errors. This fork is what we use on EVL's CAVE2 and
ARCADE2 rigs.

## Requirements

* **Unity 6.3** (`6000.3.11f1` or newer).
* **Universal Render Pipeline (URP) 17.x** (the Universal 3D project
  template ships this).
* **Netcode for GameObjects 2.9.x**.
* **Input System 1.19.x**.
* **uGUI with TextMeshPro** (`com.unity.ugui`, present in new Unity 6
  projects), used by the calibration info display.

URP is already present in the Universal 3D template. Netcode for
GameObjects and Input System are not; install them before importing the
package, otherwise `MyUniCAVE` scripts will fail to compile and Unity
will mark the imported assets as broken.

## Installation

### Step 1: install the dependencies via Package Manager

Do this **first**, before adding the package itself.

1. Open **Window > Package Manager**.
2. Set the source dropdown (top-left) to **Unity Registry**.
3. Search for and install:
   * **Netcode for GameObjects**
   * **Input System** (Unity will prompt to enable the new input
     backend and restart the editor; accept it).

### Step 2: add the MyUniCAVE package

Pick one of the two methods.

#### Option A: clone the repository into your project's `Assets/` folder

Source-editable; package updates with `git pull`. Recommended for users
who plan to extend the fork.

```powershell
cd <YourProject>\Assets
git clone https://github.com/BonfantiRiccardo/MyUniCAVE.git
```

#### Option B: import the prebuilt `.unitypackage`

Pre-versioned snapshot, read-only inside the consumer project.

1. Download `Package~/MyUniCAVE.unitypackage` from this repository.
2. In the consumer Unity Editor: **Assets > Import Package > Custom
   Package...**, pick the file, leave every entry checked, click
   **Import**. Files land at `Assets/MyUniCAVE/` with paths and GUIDs
   preserved.

## Shipped rigs

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

Sections below are split into setup common to any rig, then the two
rig-specific paths, then what you need to know to describe a rig of your
own.

## Quick start, part 1: setup common to every rig

1. **Create a new Unity 6 project** from the **Universal 3D** template.
   This sets up URP with a default Renderer asset.

2. **Install dependencies and the package** following the two
   installation steps above (Netcode and Input System first, then the
   package). After this, `Assets/MyUniCAVE/` is present and compiles
   cleanly.

3. **Open or create the scene** that will host the CAVE.

4. **Add a Netcode Network Manager to the scene.** No prefab in this
   package includes one; following the upstream UniCAVE convention, the
   Network Manager lives as a separate scene GameObject so it can be
   shared or swapped between scenes.
   * Create an empty GameObject named `Network Manager`.
   * Add Component > **Netcode > Network Manager**.
   * Add Component > **Netcode > Transports > Unity Transport**. The
     Network Manager's `NetworkTransport` field auto-links to it.
   * On the `NetworkConfig` foldout, leave `PlayerPrefab` empty (the
     CAVE setup does not spawn per-client players) and set
     `EnableSceneManagement = true`.
   * Leave Unity Transport's connection data fields blank;
     `NetworkInitialization` overwrites them at runtime from
     `serverAddress`/`serverPort` (or the matching command-line
     arguments).

   The package ships a `Prefabs/Default Network Manager.prefab` from
   upstream, but it predates the Netcode port and is not currently
   usable as-is. Either build the Network Manager from scratch as
   above, or fix that prefab and overwrite it.

   Without this step nothing in `UCNetwork.Update` runs, which means no
   navigation, no pose broadcast, and no input on any node.

5. **Drag the rig prefab into the scene root.** Do not parent it under
   anything else: navigation moves the prefab root (`CAVENavigator` on
   ARCADE2, `WandLocomotion` and `GamepadLocomotion` on CAVE2), and
   a non-identity parent transform corrupts the physical-to-world
   mapping the asymmetric projection assumes.

6. **Wire the Network Manager into the prefab.** Select the prefab
   instance in the Hierarchy. On its `NetworkInitialization` component,
   drag the scene's `Network Manager` GameObject into the
   `Network Manager` field. This is a scene-level reference and is never
   baked into the prefab itself, so it is the one step you must repeat
   in every new scene.

7. **Confirm `NetworkInitialization` settings** on the prefab root:
   * `headMachineAsset` references the machine name asset for the
     machine that acts as server. When the running machine name matches
     this asset the instance starts as Netcode server; otherwise it
     starts as client. Shipped defaults: `CAVE2_MASTER` for CAVE2,
     `Arcade` (GALAGA1) for ARCADE2.
   * `serverAddress` and `serverPort` point at the server's reachable
     address. Both can be overridden at runtime via `serverAddress`/
     `serverPort` command-line args, which the bundled launchers always
     supply.
   * `targetFrameRate` should match the panel refresh rate. The shipped
     prefabs use 120 (CAVE2) and 240 (ARCADE2). This applies even on a
     head machine that renders nothing, where VSync has no effect.

8. **Machine names decide what activates.** Every `PhysicalDisplay`
   compares its own `MachineName` asset against the running machine and
   deactivates itself when they differ, so one build runs everywhere and
   each process lights up exactly one screen. The running name comes
   from `System.Environment.MachineName` unless the
   `overrideMachineName <name>` argument is passed, which is how a
   single PC hosts several instances with different identities.

## Quick start, part 2a: CAVE2 (row-interleaved, 7 nodes)

Continue from part 1 with `Prefabs/CAVE2.prefab`.

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

4. **Set the tracking server.** The prefab's VRPN addresses use the
   placeholder host `TRACKER_HOST`: `Head_Tracker@TRACKER_HOST` and
   `Wand_Batman@TRACKER_HOST` for the head and wand poses (Vicon), and
   `Navigation@TRACKER_HOST:3894` for the wand's buttons and stick
   (Omicron). At runtime `TRACKER_HOST` is replaced by the
   `trackerHost <address>` argument, which `launch.ps1` passes from the
   `TrackerHost` field of `nodes.json`. The shipped `nodes.json` leaves it
   empty, so this repository does not contain the server's address.
   **To track in the EVL CAVE2 you need the private CAVE2 `nodes.json`:**
   ask the CAVE2 maintainers for it, put it in a `CAVEScripts` folder at
   your project root (see Cluster deployment), and keep it out of public
   repositories. Without it, a device whose address still holds
   `TRACKER_HOST` is disabled with a warning and the rest runs normally.
   On another rig you can also type the full address in place of the
   placeholder.

   The wand pose address names one Vicon rigid body, and the primary
   CAVE2 wand changes over time (`Wand_Batman` or `Wand_Robin`). The
   shipped prefab moves along the facing of the `Head`, so nothing reads
   the wand pose, and the stick and buttons work with either wand because
   `Navigation` reports all of them on the same channels. If you point
   `WandLocomotion.wand` at the `Wand` object to move where you point,
   first check in Vicon Tracker which wand is the primary one and set the
   `Wand` object's `VRPNTrack` address to match.

5. **Add scene content** as siblings of `Head` and the displays or at
   hierarchy root, inside the CAVE2 prefab. Anything inside the prefab
   moves with the root, which `WandLocomotion`, `GamepadLocomotion` and
   the keyboard navigation in `UCNetwork` move at runtime; anything
   outside the prefab root stays in fixed world space.

6. **Build for Windows Standalone**, then deploy per the cluster
   deployment section below.

## Quick start, part 2b: ARCADE2 (dual-pipe side-by-side, one PC)

Continue from part 1 with `Prefabs/ARCADE2.prefab`.

ARCADE2 is two 3D displays driven by two GPUs inside a single PC
(GALAGA1). Each display gets its own Unity process, and the two talk
over loopback rather than over the network. The prefab ships with two
`PhysicalDisplay` children:

| Display | Screen | `windowBounds.x` | MachineName asset | Machine name | Role |
|---|---|---|---|---|---|
| `DisplayA` | right (`\\.\DISPLAY5`) | 6720 | `MachineNames/Arcade.asset` | `GALAGA1` | server (head) |
| `DisplayB` | left (`\\.\DISPLAY1`, primary) | 0 | `MachineNames/Arcade2.asset` | `GALAGA1-B` | client |

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
   foreground so keyboard input reaches it.

## Runtime controls

Input is read on the server only and broadcast to every client, so a
key pressed on the head machine takes effect on all screens at once.
Pressing it on a client does nothing.

| Key | Effect |
|---|---|
| `WASD`, `Q`/`E`, arrow keys | Move, raise/lower, and rotate the CAVE root (keyboard fallback when tracking is unavailable; disable with `enableManualNavigation`) |
| `F5` | Toggle stereo/mono. In mono the right eye renders from the left eye's position, so both halves of the signal match |
| `Esc` | Quit every instance |

The CAVE2 wand is read by `WandLocomotion` and a gamepad by
`GamepadLocomotion`. The CAVE2 prefab enables both: the wand is the
main input, and the gamepad is its backup for when the wand does not
work. Like the keyboard, the gamepad must be connected
to the head machine (CAVE2MASTER on CAVE2), and it keeps working when
the head window loses focus only with Background Behavior = Ignore
Focus (see ARCADE2 step 6). Both components map the same actions, and
both move along the facing of the `Head`:

| Action | Wand | Gamepad |
|---|---|---|
| Move | stick | left stick |
| Turn in place | arrow left/right | right stick X |
| Fly up/down | arrow up/down | right/left trigger |
| Sprint (hold) | L2 | left shoulder |
| Reset to the start pose | O | right shoulder |

The gamepad's face buttons and D-pad are left free for the application.
An earlier build showed inverted wand movement while both components
were on, and the cause was never found. If it returns, disable
`GamepadLocomotion` and look for the `[GamepadLocomotion] reading
gamepad` line in the head's player log, which names the device it read.

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

## Cluster deployment

The package ships with launcher templates under `CAVEScripts/`:

| File | Purpose |
|---|---|
| `launch.ps1` | Spawns instances. On the master: one host instance (headless if `Master.Headless` is true). On a display node: one instance per GPU listed in `nodes.json`, each pinned to its target column or row via `-force-device-index` and `-monitor`. |
| `launch_arcade.ps1` | Single-PC variant for ARCADE2: starts the head and the client on loopback, one per GPU, and foregrounds the head window for keyboard input. Instance list is inline at the top of the script. |
| `kill.ps1` | Terminates every running player on the host: every process started from an exe in the script's folder or below it, or every process named by `-ExeName`. |
| `nodes.json` | Hostname-to-virtual-name mapping, master address, an optional exe name, and the tracking server (`TrackerHost`, empty in the package). Used by `launch.ps1`. Edit before building to match your cluster. |

Edit `nodes.json` to match your hardware (master hostname/IP, per-node
GPU list), then build. Leave `ExeName` empty to use the only exe in the
build folder. The post-build hook copies every file in `CAVEScripts/`
next to the exe, so each node is self-contained.

To change these files for one project without editing the package, put
your own copies in a `CAVEScripts` folder at the project root, beside
`Assets`. The hook copies them after the package's files, so they
replace them in the build. This is where the private CAVE2 `nodes.json`
with `TrackerHost` goes.

Both launchers pass extra player arguments to every instance through
`-ExtraArgs`, as a single string:

```powershell
.\launch.ps1 -ExtraArgs "-myFlag -myValue 3"
```

For the full CAVE2 cluster setup and deployment, ask the CAVE2
maintainers at EVL.

## License

MIT, inherited from upstream UniCAVE.
