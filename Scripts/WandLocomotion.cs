using UnityEngine;
using Unity.Netcode;

namespace UniCAVE
{
    /// <summary>
    /// Wand-driven locomotion for the UniCAVE root: the third navigation option
    /// alongside CAVENavigator (keyboard) and GamepadLocomotion (Unity gamepad).
    /// Attach to the UniCAVE root GameObject (same GameObject as UCNetwork).
    ///
    /// Reads the CAVE2 wand's analog stick, arrow pad and buttons over VRPN and
    /// translates/rotates the CAVE root along the *wand's* forward/right axes
    /// (projected horizontally by default). Push the stick forward to move
    /// whichever direction the wand is pointing, push left/right to strafe.
    /// Yaw pivots around <see cref="turnPivot"/> (set it to the Head) so the user
    /// turns in place rather than orbiting the root origin.
    ///
    /// Head tracking via VRPNTrack on the Head child still gives the correct
    /// off-axis projection for each display independently of where the CAVE root
    /// is or which way the wand is pointing.
    ///
    /// Server-only; the resulting CAVE-root transform is replicated to clients
    /// by UCNetwork.SetTransformsClientRpc every frame.
    ///
    /// IMPORTANT - two different VRPN devices:
    ///   pose  (wand orientation) = Vicon,   e.g. Wand_Batman@TRACKER_HOST
    ///   input (stick / buttons)  = Omicron VRPNDevice, a separate address.
    /// Vicon's VRPN output is pose-only, so pointing analogDevice at Wand_Batman
    /// silently yields zeros. See local/VRPN_Tracking_Input_Setup.md.
    ///
    /// IMPORTANT - channel numbering: trackd prints val01../but01.. (1-based) but
    /// VRPN channels are 0-based, so trackd val01 is channel 0, but02 is button 1.
    /// The defaults below already encode the CAVE2 wand mapping:
    ///   val01 -> ch 0  stick X (left -1 / right +1)
    ///   val02 -> ch 1  stick Y (back -1 / forward +1)
    ///   val05 -> ch 4  arrows X (left -1 / right +1)
    ///   val06 -> ch 5  arrows Y (back -1 / forward +1)
    ///   but02 -> button 1  (X)
    ///   but03 -> button 2  (O)
    /// Enable <see cref="debugLogChannels"/> to confirm this on the live rig
    /// before trusting it.
    /// </summary>
    public class WandLocomotion : NetworkBehaviour
    {
        /// <summary>What an arrow-pad axis drives. Lets the mapping be retuned in the
        /// Inspector instead of requiring a rebuild + redeploy across the cluster.</summary>
        public enum ArrowAction { None, Yaw, Vertical, Move }

        [Header("Wand Reference")]
        [Tooltip("The tracked Wand GameObject (a child of this UniCAVE root, with a VRPNTrack component). " +
                 "Its transform.forward / transform.right are used as the movement basis.")]
        public Transform wand;

        [Tooltip("What yaw pivots around. Set to the Head so turning rotates the world about the " +
                 "viewer instead of orbiting them around the root origin. Falls back to the wand if unset.")]
        public Transform turnPivot;

        [Header("VRPN Device")]
        [Tooltip("VRPN analog device address for the wand's stick/arrows. This is the Omicron VRPNDevice " +
                 "(gamepad) address, NOT the Vicon pose device - Vicon carries no analog channels.")]
        public string analogDevice = "Controller0@TRACKER_HOST";

        [Tooltip("VRPN button device address. Normally the same as analogDevice.")]
        public string buttonDevice = "Controller0@TRACKER_HOST";

        [Header("Analog Channels (0-based; trackd valNN is channel NN-1)")]
        [Tooltip("Stick X, trackd val01. Left -1 / right +1.")]
        public int strafeAxis  = 0;
        [Tooltip("Stick Y, trackd val02. Back -1 / forward +1.")]
        public int forwardAxis = 1;

        [Tooltip("Arrow pad X as an ANALOG channel. On the CAVE2 wand the arrows are buttons, not " +
                 "analog, so leave this at -1 and use the arrow button fields below. Kept for rigs " +
                 "whose VRPN device does expose the D-pad as an axis.")]
        public int arrowStrafeAxis  = -1;
        [Tooltip("Arrow pad Y as an ANALOG channel. See arrowStrafeAxis; -1 disables.")]
        public int arrowForwardAxis = -1;

        [Header("Arrow Pad Buttons (CAVE2 wand publishes the D-pad as buttons)")]
        [Tooltip("Button index for arrow up. -1 disables. Determine by pressing one arrow at a time " +
                 "with debugLogChannels on; on CAVE2 the four arrows are among indices 3,4,5,6.")]
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

        [Tooltip("Project the wand's forward/right to the horizontal plane. " +
                 "Recommended: tilting the wand up or down should not make the CAVE rise or sink " +
                 "(avoids motion sickness and keeps navigation predictable).")]
        public bool horizontalOnly = true;

        [Header("Axis Inversion")]
        [Tooltip("Invert stick X if strafing goes the wrong way.")]
        public bool invertStrafe  = false;
        [Tooltip("Invert stick Y if pushing the stick up moves you backward. The CAVE2 wand is an " +
                 "XInput pad behind Omicron and reports +1 for both right and up, so both inverts " +
                 "are off by default here (unlike most raw VRPN gamepad drivers).")]
        public bool invertForward = false;
        [Tooltip("Invert the arrow pad X axis.")]
        public bool invertArrowStrafe  = false;
        [Tooltip("Invert the arrow pad Y axis.")]
        public bool invertArrowForward = false;
        [Tooltip("Flip if pushing the arrow right turns you left.")]
        public bool invertTurn = false;

        [Header("Interleaved Source Filter")]
        [Tooltip("Omicron publishes every WandService (controllerSourceId 0..3) onto the SAME VRPN " +
                 "analog/button channels, so an idle second wand interleaves all-zero reports with the " +
                 "real ones and movement pulses at ~20Hz. VRPN carries no source id, so we cannot tell " +
                 "them apart; holding the last significant value briefly bridges the zero rows.\n\n" +
                 "This is source-agnostic: whichever wand is actually being moved wins, so Wand_Robin " +
                 "works exactly as well as Wand_Batman. Only simultaneous use of two wands is ambiguous, " +
                 "and that is inherent to Omicron sharing one device, not caused by this filter.")]
        public bool holdLastValue = true;

        [Tooltip("How long to hold the last significant value, in seconds. Must exceed the gap between " +
                 "two real reports (XInputService runs at 0.05s per service). Too high and the CAVE " +
                 "coasts after you let go.")]
        public float holdTime = 0.15f;

        [Header("Debug")]
        [Tooltip("Log every analog channel and pressed button ~2x/second on the server. " +
                 "Use this to confirm the channel mapping on the live rig, then turn it off.")]
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

            // Always logged, not gated on debugLogChannels: if this line is absent from the player
            // log the component never ran at all, which is a different problem from bad channels.
            Debug.Log($"[WandLocomotion] Start on '{Util.GetMachineName()}' IsServer={IsServer} " +
                      $"vrpnPluginAvailable={VRPN.IsAvailable} analog='{analogDevice}' " +
                      $"button='{buttonDevice}' wand={(wand == null ? "NULL" : wand.name)}");
        }

        void Update()
        {
            // Deliberately ahead of the early-outs below, so the log still shows what is happening
            // when the component is inactive rather than going silent and looking like dead input.
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

            // --- Buttons ---
            // Filtered too: an idle wand's "released" reports would otherwise clear a held button.
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

            // --- Read the stick ---
            float lx = (float)VRPN.vrpnAnalog(analogDevice, strafeAxis);
            float ly = (float)VRPN.vrpnAnalog(analogDevice, forwardAxis);

            if(invertStrafe)  lx = -lx;
            if(invertForward) ly = -ly;

            Vector2 move = FilterStick(new Vector2(lx, ly));

            // --- Read the arrow pad and fold it into the actions it drives ---
            // Analog path (most rigs) plus button path (CAVE2, where the D-pad is discrete buttons).
            // Both are summed so either wiring works, then clamped back to a unit axis.
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

            // --- Translate along the wand's horizontal facing ---
            if(move.sqrMagnitude > 1e-6f)
            {
                move = Vector2.ClampMagnitude(move, 1f);
                if(TryGetBasis(out Vector3 wandForward, out Vector3 wandRight))
                {
                    // World-space translation on the CAVE root. UCNetwork picks up
                    // transform.position and replicates to clients next frame.
                    transform.position += dt * moveSpeed * speedMul * (wandRight * move.x + wandForward * move.y);
                }
            }

            // --- Yaw around the viewer, so turning does not orbit them ---
            if(Mathf.Abs(turn) > 1e-6f)
            {
                if(invertTurn) turn = -turn;
                Vector3 pivot = turnPivot != null ? turnPivot.position : wand.position;
                transform.RotateAround(pivot, Vector3.up, turn * turnSpeed * dt);
            }

            // --- Vertical, always world up ---
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

        /// <summary>
        /// Turn an opposed pair of arrow buttons into a -1..+1 axis. Both go through the same
        /// hold filter as every other button, which matters here because the interleaved wand
        /// emits a "released" immediately after each "pressed".
        /// </summary>
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

        /// <summary>
        /// Dump the raw analog/button state so the trackd mapping can be confirmed against
        /// what VRPN actually delivers. Throttled so it does not flood the player log.
        /// </summary>
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
