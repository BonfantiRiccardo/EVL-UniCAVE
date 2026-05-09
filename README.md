# MyUniCAVE

Fork of [UniCAVE](https://github.com/livingenvironmentslab/UniCAVE) 2.0.1
(UW Madison Living Environments Lab) ported to Unity 6 with Netcode for
GameObjects and URP. Adds:

* **Row-interleaved passive stereo** for EVL's CAVE2 Planar polarized panels
  (URP renderer feature plus per-display flags).
* **Multi-instance window repositioning** that works on cluster nodes
  spawning more than one Unity process at a time.
* **CAVE2 cluster prefab** (`Prefabs/UW/CAVE2.prefab`) wired for a 7-node /
  22-GPU layout, plus pre-baked `MachineName` ScriptableObject assets for
  every virtual instance.
* **PowerShell launcher templates** (`CAVEScripts/`) copied next to the
  built `.exe` by an editor post-build hook, so a synced build folder on a
  cluster node is fully self-contained.

Upstream stopped active maintenance around April 2021 against Unity
2019.4 / 2021 LTS and the Multiplayer HLAPI; opening it in Unity 6
produces compile errors. This fork is what we use on EVL's
CAVE2 cluster.

## Requirements

* **Unity 6.3** (`6000.3.11f1` or newer).
* **Universal Render Pipeline (URP) 17.x** (the Universal 3D project
  template ships this).
* **Netcode for GameObjects 2.9.x**.
* **Input System 1.19.x**.

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

1. Download `Package~/MyUniCAVE-X.Y.Z.unitypackage` from this repository.
2. In the consumer Unity Editor: **Assets > Import Package > Custom
   Package...**, pick the file, leave every entry checked, click
   **Import**. Files land at `Assets/MyUniCAVE/` with paths and GUIDs
   preserved.

## Quick start: setting up CAVE2 in a fresh Universal 3D project

1. **Create a new Unity 6 project** from the **Universal 3D** template.
   This sets up URP with a default Renderer asset.

2. **Install dependencies and the package** following the two
   installation steps above (Netcode and Input System first, then the
   package). After this, `Assets/MyUniCAVE/` is present and compiles
   cleanly.

3. **Open or create the scene** that will host the CAVE
   (e.g. `Assets/Scenes/CAVE-demo.unity`).

4. **Add a Netcode Network Manager to the scene.** The CAVE2 prefab
   does not include one; following the upstream UniCAVE convention, the
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

5. **Drag `Assets/MyUniCAVE/Prefabs/UW/CAVE2.prefab` into the scene
   root.** Do not parent it under anything else: `CAVENavigator` moves
   the prefab root to navigate, and a non-identity parent transform
   corrupts the physical-to-world mapping the asymmetric projection
   assumes.

6. **Wire the Network Manager into the prefab.** Select the CAVE2
   prefab instance in the Hierarchy. On its `NetworkInitialization`
   component, drag the scene's `Network Manager` GameObject into the
   `Network Manager` field. This is a scene-level reference and is
   never baked into the prefab itself.

7. **Confirm `NetworkInitialization` settings** on the prefab root:
   * `headMachineAsset` references the machine name asset for the
     master/head machine (default: `CAVE2_MASTER` under
     `Prefabs/MachineNames/CAVE2/`). Cluster start logic: when the
     running machine name matches this asset the instance starts as
     Netcode server; otherwise it starts as client.
   * `serverAddress` and `serverPort` to the master's reachable IP and
     port. Both can be overridden at runtime via `serverAddress`/
     `serverPort` command-line args, which the bundled `launch.ps1`
     always supplies.
   * `targetFrameRate` to the panel refresh rate (60 for CAVE2). Higher
     values only make sense in the editor.

8. **No `PhysicalDisplayManager` is used in this prefab.** Each
   `PhysicalDisplay` checks its own `MachineName` against the running
   machine. At runtime only the display whose name matches the
   `overrideMachineName` argument activates; the other 21 sit dormant.

9. **Verify per-display values are baked correctly.** For each child
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
   * `useRenderTextures = true`, required because row interleave
     compositing reads from per-eye RenderTextures.

10. **Add the `RowInterleaveStereoFeature` to the URP Renderer asset**
    used by this scene. **Project Settings > Graphics > URP Renderer >
    Renderer Features > Add Renderer Feature > Row Interleave Stereo
    Feature**. This is what owns the composite shader; without it,
    builds will ship without the row interleave pass.

11. **Verify the head tracking source** in `VRPNTrack` (a child of the
    `Head` node): `trackerAddress = <Vicon-body-name>@<tracking-server-ip>`
    (e.g. `Head_Tracker@10.0.0.207`).

12. **Add scene content** as siblings of `Head` and the displays or at hierarchy root, inside
    the CAVE2 prefab. Anything inside the prefab moves with
    `CAVENavigator` at runtime; anything outside the prefab root stays
    in fixed world space.

13. **Build for Windows Standalone**. The post-build hook
    (`CopyLaunchScripts.cs`) copies the contents of
    `Assets/MyUniCAVE/CAVEScripts/` next to the produced exe, so each
    cluster node has everything it needs to launch.

14. **Test in the editor first.** Hit Play. The instance reports
    its detected machine name as your dev machine, which won't match
    any `PhysicalDisplay`, so nothing renders to a CAVE panel; but you
    can confirm the prefab loads, all 22 displays exist in the scene
    hierarchy, and there are no missing asset references in the
    console. To test rendering against one display, temporarily point
    one `PhysicalDisplay`'s `MachineName` at your dev machine.

## Cluster deployment

The package ships with launcher templates under `CAVEScripts/`:

| File | Purpose |
|---|---|
| `launch.ps1` | Spawns instances. On the master: one headless host. On a display node: one instance per GPU listed in `nodes.json`, each pinned to its target column or row via `-force-device-index` and `-monitor`. |
| `kill.ps1` | Terminates every running `UniCAVE-GS.exe` on the host. |
| `nodes.json` | Hostname-to-virtual-name mapping, master address, and exe name. Edit before building to match your cluster. |

Edit `nodes.json` to match your hardware (master hostname/IP, per-node
GPU list, exe name), then build. The post-build hook copies the three
files next to the exe so every node is self-contained.

For the full CAVE2 cluster setup (network/SMB share, Mechdyne Cluster
Tools Service buttons, screen layout, deployment runbook), see the
documentation in the consuming project's repository.

## License

MIT, inherited from upstream UniCAVE.
