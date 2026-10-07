using UnityEngine;
using Unity.Netcode;

namespace UniCAVE
{
    /// <summary>
    /// Moves the CAVE root with the CAVE2 wand: the stick moves along the wand's facing, the arrow pad
    /// turns or flies, and two buttons sprint and reset. Stick and buttons are read from the Omicron
    /// VRPN device; the wand pose comes from its VRPNTrack. Server-only; UCNetwork replicates the root.
    /// </summary>
    public class WandLocomotion : NetworkBehaviour
    {
        /// <summary>What an arrow-pad axis drives.</summary>
        public enum ArrowAction { None, Yaw, Vertical, Move }

        [Header("Wand Reference")]
        [Tooltip("Transform whose forward and right define movement: the tracked Wand to move where you " +
                 "point, or the Head to move where you look.")]
        public Transform wand;

        [Tooltip("What yaw pivots around. Use the Head so you turn in place. Falls back to the wand if unset.")]
        public Transform turnPivot;

        [Header("VRPN Device")]
        [Tooltip("VRPN analog device for the wand's stick and arrows. Use the Omicron device: the Vicon " +
                 "pose device has no analog channels.")]
        public string analogDevice = "Controller0@localhost";

        [Tooltip("VRPN button device address. Normally the same as analogDevice.")]
        public string buttonDevice = "Controller0@localhost";

        [Header("Analog Channels (0-based; trackd valNN is channel NN-1)")]
        [Tooltip("Stick X, trackd val01. Left -1 / right +1.")]
        public int strafeAxis  = 0;
        [Tooltip("Stick Y, trackd val02. Back -1 / forward +1.")]
        public int forwardAxis = 1;

        [Tooltip("Arrow pad X as an analog channel, -1 to disable. The CAVE2 wand reports its arrows " +
                 "as buttons, so it uses the button fields below instead.")]
        public int arrowStrafeAxis  = -1;
        [Tooltip("Arrow pad Y as an analog channel, -1 to disable.")]
        public int arrowForwardAxis = -1;

        [Header("Arrow Pad Buttons (CAVE2 wand publishes the D-pad as buttons)")]
        [Tooltip("Button index for arrow up, -1 to disable. Find it by pressing one arrow at a time " +
                 "with debugLogChannels on; on CAVE2 the arrows are among 3, 4, 5 and 6.")]
        public int arrowUpButton    = -1;
        [Tooltip("Button index for arrow down. -1 disables.")]
        public int arrowDownButton  = -1;
        [Tooltip("Button index for arrow left. -1 disables.")]
        public int arrowLeftButton  = -1;
        [Tooltip("Button index for arrow right. -1 disables.")]
        public int arrowRightButton = -1;

        [Header("Arrow Pad Behaviour")]
        [Tooltip("What the arrow X axis does. Yaw = turn in place around turnPivot.")]
        public ArrowAction arrowStrafeAction = ArrowAction.Yaw;
        [Tooltip("What the arrow Y axis does. Vertical = fly up/down; Move = translate like the stick.")]
        public ArrowAction arrowForwardAction = ArrowAction.Vertical;

        [Header("Buttons (0-based; trackd butNN is button NN-1)")]
        [Tooltip("Held for a speed boost. trackd but02 = X button = 1. -1 disables.")]
        public int sprintButton = 1;
        [Tooltip("Pressed to snap the CAVE root back to where it started. trackd but03 = O button = 2. -1 disables.")]
        public int resetButton = 2;

        [Header("Speeds")]
        [Tooltip("Translation speed in meters per second.")]
        public float moveSpeed = 1f;
        [Tooltip("Yaw speed in degrees per second.")]
        public float turnSpeed = 20f;
        [Tooltip("Vertical speed in meters per second.")]
        public float verticalSpeed = 1f;
        [Tooltip("Speed multiplier while the sprint button is held.")]
        public float sprintMultiplier = 3f;

        [Header("Options")]
        [Tooltip("Dead zone for the stick (combined magnitude).")]
        public float deadZone = 0.15f;

        [Tooltip("Keep movement horizontal, so tilting the wand does not make the CAVE rise or sink.")]
        public bool horizontalOnly = true;

        [Header("Axis Inversion")]
        [Tooltip("Invert stick X if strafing goes the wrong way.")]
        public bool invertStrafe  = false;
        [Tooltip("Invert stick Y if pushing the stick up moves you backward. The CAVE2 wand reports +1 " +
                 "for right and for up, so both inverts are off by default.")]
        public bool invertForward = false;
        [Tooltip("Invert the arrow pad X axis.")]
        public bool invertArrowStrafe  = false;
        [Tooltip("Invert the arrow pad Y axis.")]
        public bool invertArrowForward = false;
        [Tooltip("Flip if pushing the arrow right turns you left.")]
        public bool invertTurn = false;

        [Header("Interleaved Source Filter")]
        [Tooltip("Omicron sends every wand on the same VRPN channels, so an idle second wand mixes " +
                 "zero reports into the real ones and movement stutters. Holding the last non-zero value " +
                 "for a moment hides them. The wand being moved wins; two wands used at once stay ambiguous.")]
        public bool holdLastValue = true;

        [Tooltip("Seconds to hold the last non-zero value. Must exceed the gap between real reports " +
                 "(0.05 s per wand); too long and the CAVE coasts after you let go.")]
        public float holdTime = 0.15f;

        [Header("Debug")]
        [Tooltip("Log every analog channel and pressed button twice a second, to check the mapping on the rig.")]
        public bool debugLogChannels = false;
        [Tooltip("How many analog channels to scan when debugLogChannels is on.")]
        public int debugChannelCount = 8;
        [Tooltip("How many buttons to scan when debugLogChannels is on.")]
        public int debugButtonCount = 12;

        // Captured at Start so the reset button has somewhere to return to.
        private Vector3 startPosition;
        private Quaternion startRotation;

        // Edge detection: reset fires once per press, not every frame it is held.
        private bool resetWasPressed;

        // One-shot guard so the missing-wand warning does not spam the log every frame.
        private bool wandNullWarned;

        // Last significant values, used to bridge the interleaved all-zero reports.
        private Vector2 heldStick;
        private float heldStickTime = float.NegativeInfinity;
        private float heldArrowX, heldArrowY;
        private float heldArrowXTime = float.NegativeInfinity;
        private float heldArrowYTime = float.NegativeInfinity;
        private float sprintHeldTime = float.NegativeInfinity;
        private float resetHeldTime  = float.NegativeInfinity;
        private float arrowUpHeldTime    = float.NegativeInfinity;
        private float arrowDownHeldTime  = float.NegativeInfinity;
        private float arrowLeftHeldTime  = float.NegativeInfinity;
        private float arrowRightHeldTime = float.NegativeInfinity;

        private float nextDebugLogTime;

        void Start()
        {
            startPosition = transform.position;
            startRotation = transform.rotation;

            // Always logged: if this line is missing from the player log, the component never ran.
            Debug.Log($"[WandLocomotion] Start on '{Util.GetMachineName()}' IsServer={IsServer} " +
                      $"vrpnPluginAvailable={VRPN.IsAvailable} analog='{analogDevice}' " +
                      $"button='{buttonDevice}' wand={(wand == null ? "NULL" : wand.name)}");
        }

        void Update()
        {
            // Before the early-outs, so the log keeps running when they skip the rest.
            if(debugLogChannels) LogChannels();

            if(!IsServer) return;

            if(wand == null)
            {
                if(!wandNullWarned)
                {
                    Debug.LogWarning("[WandLocomotion] 'wand' is not assigned; locomotion disabled. " +
                                     "Assign the tracked Wand transform on the UniCAVE root.");
                    wandNullWarned = true;
                }
                return;
            }

            float dt = Time.deltaTime;

            // Buttons are filtered too: an idle wand's "released" reports would clear a held button.
            bool sprint = sprintButton >= 0 &&
                          FilterButton(VRPN.vrpnButton(buttonDevice, sprintButton), ref sprintHeldTime);
            float speedMul = sprint ? sprintMultiplier : 1f;

            if(resetButton >= 0)
            {
                bool resetPressed = FilterButton(VRPN.vrpnButton(buttonDevice, resetButton), ref resetHeldTime);
                bool resetEdge = resetPressed && !resetWasPressed;
                resetWasPressed = resetPressed;
                if(resetEdge)
                {
                    transform.SetPositionAndRotation(startPosition, startRotation);
                    return; // Don't fight the snap with this frame's stick input.
                }
            }

            float lx = (float)VRPN.vrpnAnalog(analogDevice, strafeAxis);
            float ly = (float)VRPN.vrpnAnalog(analogDevice, forwardAxis);

            if(invertStrafe)  lx = -lx;
            if(invertForward) ly = -ly;

            Vector2 move = FilterStick(new Vector2(lx, ly));

            // Analog and button arrows are summed so either wiring works, then clamped.
            float ax = ReadArrow(arrowStrafeAxis, ref heldArrowX, ref heldArrowXTime);
            float ay = ReadArrow(arrowForwardAxis, ref heldArrowY, ref heldArrowYTime);

            ax += ReadArrowButtons(arrowRightButton, arrowLeftButton, ref arrowRightHeldTime, ref arrowLeftHeldTime);
            ay += ReadArrowButtons(arrowUpButton, arrowDownButton, ref arrowUpHeldTime, ref arrowDownHeldTime);

            // Applied after summing so the flags mean the same thing on both wirings.
            if(invertArrowStrafe)  ax = -ax;
            if(invertArrowForward) ay = -ay;

            ax = Mathf.Clamp(ax, -1f, 1f);
            ay = Mathf.Clamp(ay, -1f, 1f);

            float turn = 0f;
            float vertical = 0f;

            ApplyArrow(arrowStrafeAction,  ax, isStrafeAxis: true,  ref move, ref turn, ref vertical);
            ApplyArrow(arrowForwardAction, ay, isStrafeAxis: false, ref move, ref turn, ref vertical);

            if(move.sqrMagnitude > 1e-6f)
            {
                move = Vector2.ClampMagnitude(move, 1f);
                if(TryGetBasis(out Vector3 wandForward, out Vector3 wandRight))
                {
                    transform.position += dt * moveSpeed * speedMul * (wandRight * move.x + wandForward * move.y);
                }
            }

            // Yaw around the viewer, so turning does not orbit them.
            if(Mathf.Abs(turn) > 1e-6f)
            {
                if(invertTurn) turn = -turn;
                Vector3 pivot = turnPivot != null ? turnPivot.position : wand.position;
                transform.RotateAround(pivot, Vector3.up, turn * turnSpeed * dt);
            }

            if(Mathf.Abs(vertical) > 1e-6f)
            {
                transform.position += Vector3.up * (vertical * verticalSpeed * speedMul * dt);
            }
        }

        /// <summary>
        /// Read one arrow axis from an analog channel, or 0 if it is disabled or inside the dead
        /// zone. Inversion is applied by the caller, after the analog and button paths are summed.
        /// </summary>
        private float ReadArrow(int channel, ref float held, ref float heldTimestamp)
        {
            if(channel < 0) return 0f;
            float v = (float)VRPN.vrpnAnalog(analogDevice, channel);

            if(Mathf.Abs(v) > deadZone)
            {
                held = Mathf.Clamp(v, -1f, 1f);
                heldTimestamp = Time.unscaledTime;
                return held;
            }

            if(holdLastValue && Time.unscaledTime - heldTimestamp < holdTime) return held;

            held = 0f;
            return 0f;
        }

        /// <summary>
        /// Gate the stick on its combined magnitude, holding the last significant reading long
        /// enough to bridge the all-zero reports interleaved by an idle second wand.
        /// </summary>
        private Vector2 FilterStick(Vector2 raw)
        {
            if(raw.magnitude > deadZone)
            {
                heldStick = Vector2.ClampMagnitude(raw, 1f);
                heldStickTime = Time.unscaledTime;
                return heldStick;
            }

            // Genuinely centred, or the other wand's zero row. Cannot distinguish, so hold briefly.
            if(holdLastValue && Time.unscaledTime - heldStickTime < holdTime) return heldStick;

            heldStick = Vector2.zero;
            return Vector2.zero;
        }

        /// <summary>Turns two opposed arrow buttons into a -1..+1 axis, through the same hold filter as the other buttons.</summary>
        private float ReadArrowButtons(int positive, int negative, ref float posTime, ref float negTime)
        {
            float v = 0f;
            if(positive >= 0 && FilterButton(VRPN.vrpnButton(buttonDevice, positive), ref posTime)) v += 1f;
            if(negative >= 0 && FilterButton(VRPN.vrpnButton(buttonDevice, negative), ref negTime)) v -= 1f;
            return v;
        }

        /// <summary>Hold a button "pressed" briefly, so an idle wand's release report cannot clear it.</summary>
        private bool FilterButton(bool raw, ref float lastPressedTime)
        {
            if(raw)
            {
                lastPressedTime = Time.unscaledTime;
                return true;
            }

            return holdLastValue && Time.unscaledTime - lastPressedTime < holdTime;
        }

        /// <summary>Route one arrow axis into whichever action it is configured to drive.</summary>
        private void ApplyArrow(ArrowAction action, float value, bool isStrafeAxis,
                                ref Vector2 move, ref float turn, ref float vertical)
        {
            if(action == ArrowAction.None || Mathf.Abs(value) < 1e-6f) return;

            switch(action)
            {
                case ArrowAction.Yaw:
                    turn += value;
                    break;
                case ArrowAction.Vertical:
                    vertical += value;
                    break;
                case ArrowAction.Move:
                    // Additive with the stick so either input alone works.
                    if(isStrafeAxis) move.x += value;
                    else             move.y += value;
                    break;
            }
        }

        /// <summary>
        /// Build the movement basis from the wand's world-space axes, ground-projected
        /// when horizontalOnly is set. Returns false if the basis is degenerate.
        /// </summary>
        private bool TryGetBasis(out Vector3 wandForward, out Vector3 wandRight)
        {
            wandForward = wand.forward;
            wandRight   = wand.right;

            if(!horizontalOnly) return true;

            wandForward.y = 0f;
            wandRight.y   = 0f;

            // Guard against degenerate cases (wand pointing straight up or down)
            if(wandForward.sqrMagnitude < 1e-6f)
            {
                // Fall back: derive right-handed horizontal basis from remaining right vector
                if(wandRight.sqrMagnitude < 1e-6f) return false;
                wandRight.Normalize();
                wandForward = Vector3.Cross(wandRight, Vector3.up);
            }
            else if(wandRight.sqrMagnitude < 1e-6f)
            {
                wandForward.Normalize();
                wandRight = Vector3.Cross(Vector3.up, wandForward);
            }
            else
            {
                wandForward.Normalize();
                wandRight.Normalize();
            }

            return true;
        }

        /// <summary>Logs the raw analog and button state, at most twice a second.</summary>
        private void LogChannels()
        {
            if(Time.unscaledTime < nextDebugLogTime) return;
            nextDebugLogTime = Time.unscaledTime + 0.5f;

            var sb = new System.Text.StringBuilder("[WandLocomotion] analog");
            for(int i = 0; i < debugChannelCount; ++i)
                sb.Append($" ch{i}:{VRPN.vrpnAnalog(analogDevice, i):F3}");

            sb.Append("  buttons");
            bool any = false;
            for(int i = 0; i < debugButtonCount; ++i)
            {
                if(VRPN.vrpnButton(buttonDevice, i)) { sb.Append(' ').Append(i); any = true; }
            }
            if(!any) sb.Append(" none");

            Debug.Log(sb.ToString());
        }
    }
}
