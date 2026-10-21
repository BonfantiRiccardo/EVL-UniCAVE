# EVL-UniCAVE

EVL-UniCAVE runs one Unity application on an immersive display made of
several screens, such as a CAVE or a pair of 3D monitors. Each screen
has its own Unity process, on one PC or on many, and screens that share
a GPU can share one process. One process, the head, reads input and
tracking and sends the rig pose to the others over the network. Each
process then renders its screens with an off-axis projection for the
tracked viewer, so all screens show one consistent scene.

EVL-UniCAVE is a fork of
[UniCAVE](https://github.com/livingenvironmentslab/UniCAVE) 2.0.1 by the
Living Environments Laboratory at UW Madison. Upstream targets Unity
2019.4 and 2021 LTS with the Multiplayer HLAPI and does not compile in
Unity 6. We use this fork on EVL's CAVE2 and ARCADE2, and on the CAVERN
rig.

This fork adds:

* A port to Unity 6, Netcode for GameObjects, URP and the Input System.
* Row-interleaved passive stereo for micropolarized panels, such as the
  CAVE2 panels. Each eye is rendered only at the rows it can see, which
  halves the rendering work.
* Ready prefabs for EVL's CAVE2 (22 GPUs on 7 PCs) and ARCADE2 (two 3D
  displays on two GPUs of one PC), for CAVERN (three tilted 3D monitors
  on one GPU), plus a single 3D display and a 3D projector.
* One process for several screens on the same GPU, so the screens do not
  compete for the GPU and always show the same frame.
* Window placement that works when one PC runs several Unity processes.
* Wand and gamepad navigation. The tracking server's address is set at
  launch, so the project never stores it.
* A stereo/mono toggle (F5) that switches every screen at once.
* Display sync for screens driven by different GPUs of one PC: an
  NVIDIA present barrier and a frame lockstep make both GPUs show the
  same frame.
* PowerShell launch scripts, copied next to every build.

## Installation

Requirements: Unity 6.3 (`6000.3.11f1` or newer), URP 17, Netcode for
GameObjects 2.9, Input System 1.19, and uGUI with TextMeshPro. A project
created from the **Universal 3D** template already has URP and uGUI.

1. **Install the dependencies first**, otherwise the package scripts do
   not compile. In **Window > Package Manager**, select **Unity
   Registry** and install **Netcode for GameObjects** and **Input
   System**. Accept the prompt to enable the new input backend and
   restart the Editor.
2. **Add the package** with one of these methods:
   * Clone the repository into your project's `Assets` folder. You can
     then edit the code and update it with `git pull`:
     ```powershell
     cd <YourProject>\Assets
     git clone https://github.com/BonfantiRiccardo/EVL-UniCAVE.git MyUniCAVE
     ```
   * Import the `.unitypackage`. Download `EVL-UniCAVE.unitypackage`
     from the `Package~` folder of this repository or from the
     [Releases](https://github.com/BonfantiRiccardo/EVL-UniCAVE/releases)
     page, then choose **Assets > Import Package > Custom Package...**
     and import every entry. The files land in `Assets/MyUniCAVE/`.

## Quick setup of a scene

1. **Add a Network Manager.** Create an empty GameObject named
   `Network Manager` and add **Netcode > Network Manager** and
   **Netcode > Transports > Unity Transport**. Leave `PlayerPrefab`
   empty, turn on `EnableSceneManagement`, and leave the transport's
   address fields blank, because they are set at runtime. Without a
   Network Manager, no input, navigation or pose broadcast runs. Do not
   use `Prefabs/Default Network Manager.prefab`: it comes from upstream
   and does not work with Netcode.
2. **Add the rig prefab** at the scene root, not under another object,
   because navigation moves the prefab root:

   | Prefab | Display | Unity processes |
   |---|---|---|
   | `Prefabs/CAVE2.prefab` | EVL CAVE2: 18 columns and 4 back-wall segments, passive stereo | 22, on 7 PCs |
   | `Prefabs/ARCADE2.prefab` | Two side-by-side 3D displays on two GPUs | 2, on one PC |
   | `Prefabs/CAVERN.prefab` | Three tilted 3D monitors on one GPU, passive stereo | 1, for all three screens |
   | `Prefabs/ARCADE.prefab` | One side-by-side 3D display | 1 |
   | `Prefabs/PROJECTOR.prefab` | One 3D projector | 1 |

   `Scenes/` contains a ready example for CAVE2, ARCADE2 (`ARCADE.unity`)
   and the projector.
3. **Connect the two.** On the prefab's `NetworkInitialization`
   component, drag the scene's `Network Manager` into the
   `Network Manager` field. The reference belongs to the scene, so
   repeat this step in every new scene. The other fields name the head
   machine (`headMachineAsset`) and its address (`serverAddress`,
   `serverPort`); the shipped values fit EVL's rigs.
4. **Add your content.** Objects inside the prefab root move with the
   navigation; objects outside it stay fixed in the world.
5. **Build a Windows player and launch it** with the scripts that the
   build copies next to the exe:
   * CAVE2: run `launch.ps1` on every PC; it reads `nodes.json`. For
     head and wand tracking, the head also needs the tracking server's
     address (see
     [Launching](Documentation~/Launching.md#tracking-server)).
   * ARCADE2: right-click `launch_arcade_sync.ps1` > Run with PowerShell
     for screens that show the same frame, or `launch_arcade.ps1` for
     free-running screens, which timing measurements need.
   * CAVERN: right-click `launch_cavern.ps1` > Run with PowerShell.

   If you navigate with a gamepad, set **Project Settings > Input System
   Package > Background Behavior** to **Ignore Focus**, so the gamepad
   keeps working when the head window is not in the foreground.

One build serves every screen. Each `PhysicalDisplay` is active only in
the process whose machine name matches its `MachineName` asset, or the
machine name of its `PhysicalDisplayManager` when it has one. The launch
scripts give each process its name with `overrideMachineName`.

Further documentation:

* [Rigs](Documentation~/Rigs.md): prefab settings, CAVE2, ARCADE2 and
  CAVERN details, stereo modes, and how to build a rig of your own.
* [Launching](Documentation~/Launching.md): launch scripts, `nodes.json`,
  the tracking server, and runtime keys.
* [Wand button actions](Documentation~/WandButtons.md): how wand button
  presses reach the head over VRPN, and how to attach your own actions
  to them.
* [Display sync](Documentation~/DisplaySync.md): how the two ARCADE2
  screens show the same frame, and how to check it.

## License

MIT, inherited from upstream UniCAVE. `Plugins/x86_64/UniCAVESwapSync.dll`
links NVIDIA's NVAPI library, which is also MIT-licensed (Copyright (c)
2024 NVIDIA CORPORATION & AFFILIATES).
