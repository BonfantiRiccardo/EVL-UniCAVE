# Launching

## Launch scripts

The package ships with launcher templates under `CAVEScripts/`:

| File | Purpose |
|---|---|
| `launch.ps1` | Spawns instances. On the master: one host instance (headless if `Master.Headless` is true). On a display node: one instance per GPU listed in `nodes.json`, each pinned to its target column or row via `-force-device-index` and `-monitor`. On CAVE2, CAVE2 Control runs it (see [CAVE2](#cave2)). |
| `launch_arcade.ps1` | Single-PC variant for ARCADE2: starts the head and the client on loopback, one per GPU, and foregrounds the head window for keyboard input. Instance list is inline at the top of the script. `-Sync` locks both screens to the same frame (see [Display sync](DisplaySync.md)). |
| `launch_arcade_sync.ps1` | Runs `launch_arcade.ps1 -Sync`; usable with right-click > Run with PowerShell. |
| `launch_cavern.ps1` | Single-PC launcher for CAVERN: starts one instance that renders all three screens in one window over the three monitors, and brings that window to the front for keyboard input. Usable with right-click > Run with PowerShell. |
| `kill.ps1` | Terminates every running player on the host: every process started from an exe in the script's folder or below it, or every process named by `-ExeName`. |
| `nodes.json` | Hostname-to-virtual-name mapping, master address, an optional exe name, and the tracking server (`TrackerHost`, empty in the package). Used by `launch.ps1`. Edit before building to match your cluster. |

Set Windows display scaling to 100% on every PC that runs the
launchers. `launch.ps1` and `launch_arcade.ps1` find the monitors through
Windows' scaled desktop coordinates, so other scaling values break them.
`launch_cavern.ps1` reads the real pixel positions and copes with any
scaling, but its window spans three monitors, so 100% is still the safe
choice.

Edit `nodes.json` to match your hardware (master hostname/IP, per-node
GPU list), then build. Leave `ExeName` empty to use the only exe in the
build folder. A post-build hook copies every file in `CAVEScripts/`
next to the exe, so each node is self-contained.

To change these files for one project without editing the package, put
your own copies in a `CAVEScripts` folder at the project root, beside
`Assets`. The hook copies them after the package's files, so they
replace them in the build. On a rig of your own, this is where a
`nodes.json` with the address of your tracking server goes.

Both launchers pass extra player arguments to every instance through
`-ExtraArgs`, as a single string. UniCAVE arguments take no dash; Unity's
own arguments do:

```powershell
.\launch.ps1 -ExtraArgs "-myFlag -myValue 3"
```

## CAVE2

On CAVE2, builds are installed and run with **CAVE2 Control**, a program
on CAVE2MASTER, the head machine. It is not part of this package: open
it from the desktop or the Start menu of CAVE2MASTER, and ask the CAVE2
maintainers at EVL for access. It starts `launch.ps1` on every PC for
you, so on CAVE2 you never run the launch scripts by hand.

**Prepare the build.** Build a Windows player and pack its folder into a
`.zip`, `.7z` or `.rar` archive with the folder itself at the top, for
example `MyApp\MyApp.exe` inside `MyApp.zip`. The folder name becomes
the name of the build on CAVE2. Download the archive on CAVE2MASTER, so
that it lands in the Downloads folder.

**Install.** The **Install** tab lists the archives in Downloads and
selects the newest one. Check the build folder name that CAVE2 Control
proposes: the name of the folder that holds the player exe, or the name
of the archive. A build with the same name replaces the installed one,
and result files in the old folder are kept aside. Press **Install
build**. CAVE2 Control extracts the build, writes the address of the
tracking server into its `nodes.json` (see
[Tracking server](#tracking-server)), allows the player through Windows
Firewall, and, with **Sync the builds to the nodes afterwards** ticked,
copies the builds to every node.

**Run.** Select the build at the top of the window and press **Start
CAVE** in the **Run** tab. It stops any running player, starts the head
on CAVE2MASTER, and then starts one instance per GPU on every node.
Extra player arguments typed in the Run tab reach every instance through
`launch.ps1 -ExtraArgs`. Keys work in the head's window on CAVE2MASTER
(see [Runtime controls](#runtime-controls)). `Esc` there quits every
instance; **Stop CAVE** ends them by force if that fails.

**Check.** **Verify deploy** compares the player on every node with the
copy on CAVE2MASTER. The **Diagnostics** tab tests the connection to
every node, prints the screen layout of every PC, shows the end of the
log of one instance, and starts a single instance on one GPU of one node.
After each command, the node labels at the bottom of the window turn
green when the command worked on that node and red when it failed.

**Collect results first.** A sync makes every node an exact copy of the
builds on CAVE2MASTER, so it deletes the files that the instances wrote
on the nodes, except their logs. The **Results** tab copies benchmark
logs and captures from the nodes to CAVE2MASTER; use it before the next
sync.

## Tracking server

The prefab's VRPN addresses use the placeholder host `TRACKER_HOST`:
`Head_Tracker@TRACKER_HOST` and `Wand_Batman@TRACKER_HOST` for the head
and wand poses (Vicon), and `Navigation@TRACKER_HOST:3894` for the
wand's buttons and stick (Omicron). At runtime `TRACKER_HOST` is
replaced by the `trackerHost <address>` argument, which `launch.ps1`
passes from the `TrackerHost` field of `nodes.json`. The shipped
`nodes.json` leaves it empty, so this repository does not contain the
server's address. **On CAVE2 you do not have to set it:** when CAVE2
Control installs a build whose `nodes.json` leaves `TrackerHost` empty,
it writes the address of the CAVE2 tracking server into it. On another
rig, put the address in the `TrackerHost` field of a `nodes.json` in a
`CAVEScripts` folder at your project root (see above), or type the full
address in place of the placeholder. Without an address, a device whose
address still holds `TRACKER_HOST` is disabled with a warning and the
rest runs normally.

Only the head connects to the tracking server. A VRPN server answers by
opening a TCP connection back to the client, so the firewall of the head
PC must accept inbound connections for the player exe. On CAVE2, CAVE2
Control allows every installed player through the firewall.

The wand pose address names one Vicon rigid body, and the primary
CAVE2 wand changes over time (`Wand_Batman` or `Wand_Robin`). The
shipped prefab moves along the facing of the `Head`, so nothing reads
the wand pose, and the stick and buttons work with either wand because
`Navigation` reports all of them on the same channels. If you point
`WandLocomotion.wand` at the `Wand` object to move where you point,
first check in Vicon Tracker which wand is the primary one and set the
`Wand` object's `VRPNTrack` address to match.

## Runtime controls

Input is read on the server only and broadcast to every client, so a
key pressed on the head machine takes effect on all screens at once.
Pressing it on a client does nothing.

| Key | Effect |
|---|---|
| `WASD`, `Q`/`E`, arrow keys | Move, raise/lower, and rotate the CAVE root (keyboard fallback when tracking is unavailable; disable with `enableManualNavigation`) |
| `F5` | Toggle stereo/mono. In mono the right eye renders from the left eye's position, so both halves of the signal match |
| `Esc` | Quit every instance |
| `]` and `[` | With `frameLock 1`: shift the frame pairing of synced screens (see [Display sync](DisplaySync.md#keys)) |

The CAVE2 wand is read by `WandLocomotion` and a gamepad by
`GamepadLocomotion`. The CAVE2 prefab enables both: the wand is the
main input, and the gamepad is its backup for when the wand does not
work. Like the keyboard, the gamepad must be connected to the head
machine (CAVE2MASTER on CAVE2), and it keeps working when the head
window loses focus only with Background Behavior = Ignore Focus (see
[Rigs](Rigs.md#arcade2), step 6). Both components map the same actions,
and both move along the facing of the `Head`:

| Action | Wand | Gamepad |
|---|---|---|
| Move | stick | left stick |
| Turn in place | arrow left/right | right stick X |
| Fly up/down | arrow up/down | right/left trigger |
| Sprint (hold) | L2 | left shoulder |
| Reset to the start pose | O | right shoulder |

The gamepad's face buttons and D-pad are left free for the application.
If the wand moves the rig in the wrong direction while a gamepad is
connected, disable `GamepadLocomotion` and look for the
`[GamepadLocomotion] reading gamepad` line in the head's player log,
which names the device it read.

## Troubleshooting

| Symptom | What to do |
|---|---|
| PowerShell says running scripts is disabled on this system | Run `Get-ExecutionPolicy -List`. A `MachinePolicy` entry overrides every other setting, including `-ExecutionPolicy Bypass`. An administrator has to remove it (registry key `HKLM\SOFTWARE\Policies\Microsoft\Windows\PowerShell`); open a new PowerShell window afterwards, because a running one keeps the old value. Builds copied from a download or a zip may also need `Get-ChildItem -Recurse \| Unblock-File`. |
| A launcher reports that no screen was found at a desktop X | The Windows display arrangement does not match the `windowBounds` baked into the prefab. Fix the arrangement in Settings > System > Display, or the prefab values. |
| CAVE2: one column stays black after **Start CAVE** | An instance did not start or did not connect. Quit with `Esc` on the head window and press **Start CAVE** again; if it repeats, read that instance's log in **Diagnostics > Player log**. |
| CAVE2: **Verify deploy** reports `STALE` or `MISSING` on a node | That node did not receive the last sync. Press **Sync builds to nodes**, then verify again. |
| CAVE2: a column shows the scene in the wrong place or size | A panel is off or asleep, so the desktop layout of that PC changed. Compare **Diagnostics > Monitor layout** with the other PCs and check the panel's power. |
| Several instances share one GPU and the ones without keyboard focus run much slower | Windows gives the focused window priority on a busy GPU. Render the screens on that GPU from one process with a `PhysicalDisplayManager` (see [Rigs](Rigs.md#how-many-instances)). |
