# Launching

## Launch scripts

The package ships with launcher templates under `CAVEScripts/`:

| File | Purpose |
|---|---|
| `launch.ps1` | Spawns instances. On the master: one host instance (headless if `Master.Headless` is true). On a display node: one instance per GPU listed in `nodes.json`, each pinned to its target column or row via `-force-device-index` and `-monitor`. |
| `launch_arcade.ps1` | Single-PC variant for ARCADE2: starts the head and the client on loopback, one per GPU, and foregrounds the head window for keyboard input. Instance list is inline at the top of the script. `-Sync` locks both screens to the same frame (see [Display sync](DisplaySync.md)). |
| `launch_arcade_sync.ps1` | Runs `launch_arcade.ps1 -Sync`; usable with right-click > Run with PowerShell. |
| `kill.ps1` | Terminates every running player on the host: every process started from an exe in the script's folder or below it, or every process named by `-ExeName`. |
| `nodes.json` | Hostname-to-virtual-name mapping, master address, an optional exe name, and the tracking server (`TrackerHost`, empty in the package). Used by `launch.ps1`. Edit before building to match your cluster. |

Edit `nodes.json` to match your hardware (master hostname/IP, per-node
GPU list), then build. Leave `ExeName` empty to use the only exe in the
build folder. A post-build hook copies every file in `CAVEScripts/`
next to the exe, so each node is self-contained.

To change these files for one project without editing the package, put
your own copies in a `CAVEScripts` folder at the project root, beside
`Assets`. The hook copies them after the package's files, so they
replace them in the build. This is where a private `nodes.json` with
`TrackerHost` goes.

Both launchers pass extra player arguments to every instance through
`-ExtraArgs`, as a single string. UniCAVE arguments take no dash; Unity's
own arguments do:

```powershell
.\launch.ps1 -ExtraArgs "-myFlag -myValue 3"
```

For the full CAVE2 cluster setup and deployment, ask the CAVE2
maintainers at EVL.

## Tracking server

The prefab's VRPN addresses use the placeholder host `TRACKER_HOST`:
`Head_Tracker@TRACKER_HOST` and `Wand_Batman@TRACKER_HOST` for the head
and wand poses (Vicon), and `Navigation@TRACKER_HOST:3894` for the
wand's buttons and stick (Omicron). At runtime `TRACKER_HOST` is
replaced by the `trackerHost <address>` argument, which `launch.ps1`
passes from the `TrackerHost` field of `nodes.json`. The shipped
`nodes.json` leaves it empty, so this repository does not contain the
server's address. **To track in the EVL CAVE2 you need the private
CAVE2 `nodes.json`:** ask the CAVE2 maintainers for it, put it in a
`CAVEScripts` folder at your project root (see above), and keep it out
of public repositories. Without it, a device whose address still holds
`TRACKER_HOST` is disabled with a warning and the rest runs normally.
On another rig you can also type the full address in place of the
placeholder.

Only the head connects to the tracking server. A VRPN server answers by
opening a TCP connection back to the client, so the firewall of the head
PC must accept inbound connections for the player exe.

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
