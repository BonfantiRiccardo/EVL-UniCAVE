# Wand button actions

This page explains how wand button presses reach the application and how
to attach your own actions to them.

## Where the button presses come from

The tracking system streams its devices over VRPN, and only the head
connects to it. A device address has the form `Device@host:port`, where
the port defaults to 3883. The prefabs write `TRACKER_HOST` in place of
the host; at launch it is replaced by `TrackerHost` from `nodes.json`
(see [Launching](Launching.md#tracking-server)). On CAVE2 there are
three devices:

| Address | Server | Content |
|---|---|---|
| `Head_Tracker@TRACKER_HOST` | Vicon Tracker | head pose |
| `Wand_Batman@TRACKER_HOST` | Vicon Tracker | wand pose |
| `Navigation@TRACKER_HOST:3894` | Omicron input server | wand buttons and stick |

A button press must therefore reach the head as a VRPN button on a
device that the head reads; on CAVE2 this device is `Navigation`. A VRPN
server answers by opening a TCP connection back to the head, so the
head PC's firewall must accept inbound connections for the player exe.

Buttons are numbered from 0. On the CAVE2 wand:

| Button | 0 | 1 | 2 | 3 | 4 | 5 | 6 |
|---|---|---|---|---|---|---|---|
| Wand | X | O | L2 | up | down | left | right |
| Used by `WandLocomotion` for | free | reset | sprint | fly up | fly down | turn left | turn right |

## What to modify

| To change | Edit |
|---|---|
| Tracking server | `TrackerHost` in `nodes.json`, in a `CAVEScripts` folder at your project root |
| Head and wand pose devices | `trackerAddress` of the `VRPNTrack` components on the prefab's `Head` and `Wand` objects |
| Buttons used for navigation | `WandLocomotion` on the prefab root: `buttonDevice`, `resetButton`, `sprintButton` and the arrow buttons; -1 frees a button |
| A new action | A new script in your own project, added to the prefab root in your scene (below) |

## Adding a new action

The head reads the button and sends the action to every process. The
prefab root carries the `NetworkObject`, so a `NetworkBehaviour` added
to the root can broadcast the action with a ClientRpc:

```csharp
using Unity.Netcode;
using UnityEngine;
using UniCAVE;

public class WandButtonAction : NetworkBehaviour
{
    public string buttonDevice = "Navigation@TRACKER_HOST:3894";
    public int button = 0;
    public float holdTime = 0.15f;   // CAVE2 sends every wand on the same channels

    float lastPressedTime = float.NegativeInfinity;
    bool wasPressed;

    void Update()
    {
        if(!IsServer || !IsSpawned) return;   // only the head reads VRPN

        if(VRPN.vrpnButton(buttonDevice, button)) lastPressedTime = Time.unscaledTime;
        bool pressed = Time.unscaledTime - lastPressedTime < holdTime;
        if(pressed && !wasPressed)
        {
            DoAction();
            DoActionClientRpc();
        }
        wasPressed = pressed;
    }

    [ClientRpc]
    void DoActionClientRpc() => DoAction();

    void DoAction()
    {
        // Your action, for example toggling an object.
    }
}
```

The head runs as a Netcode server, not as a host, so it does not
receive its own ClientRpc and calls `DoAction()` directly. This matters
on ARCADE2, where the head also renders a screen. The hold time handles
a CAVE2 detail: Omicron sends all wands on the same channels, so an idle
wand reports the button as released right after each press, and without
the hold one press would fire several times.

## Finding other buttons

To find the number of another button, turn on `debugLogChannels` on
`WandLocomotion` in a test build: the head's log then lists the pressed
buttons twice a second. Turn it off afterwards, because its extra reads
disturb the wand input on CAVE2.
