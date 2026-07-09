using UnityEngine;
using Unity.Netcode;

namespace UniCAVE
{
    /// <summary>
    /// VRPN-based locomotion for the UniCAVE root.
    /// Reads a VRPN gamepad's analog sticks/triggers (and a sprint button) and
    /// drives the CAVE root. Server-only; UCNetwork replicates the root transform
    /// to every display node.
    ///
    /// IMPORTANT: analogDevice/buttonDevice must point at a VRPN device that
    /// actually carries button/analog channels (a gamepad). Vicon Tracker's VRPN
    /// output is POSE-ONLY, so Head_Tracker / Wand_Batman will NOT work here.
    /// Source the stick from Omicron's VRPNDevice (its VRPN gamepad output) or a
    /// standalone gamepad vrpn_server. Until that source exists these reads return
    /// 0 and the CAVE simply won't move (pose tracking is unaffected).
    ///
    /// Attach to the UniCAVE root (same GameObject as UCNetwork).
    /// </summary>
    public class CAVELocomotion : NetworkBehaviour
    {
        [Header("VRPN gamepad device (NOT a Vicon pose device)")]
        public string analogDevice = "Controller0@TRACKER_HOST";
        public string buttonDevice = "Controller0@TRACKER_HOST";

        [Header("Analog Channels")]
        [Tooltip("Left stick X (strafe)")]
        public int strafeAxis   = 0;
        [Tooltip("Left stick Y (forward / back)")]
        public int forwardAxis  = 1;
        [Tooltip("Right stick X (yaw)")]
        public int yawAxis      = 2;
        [Tooltip("Right stick Y (pitch)")]
        public int pitchAxis    = 3;
        [Tooltip("Left trigger (move down)")]
        public int leftTriggerAxis  = 5;
        [Tooltip("Right trigger (move up)")]
        public int rightTriggerAxis = 4;

        [Header("Button")]
        [Tooltip("Sprint (speed multiplier while held)")]
        public int sprintButton = 4;

        [Header("Speeds")]
        public float moveSpeed     = 3f;
        public float turnSpeed     = 20f;
        public float verticalSpeed = 1.5f;
        public float sprintMultiplier = 3f;

        [Header("Options")]
        [Tooltip("Dead zone for analog sticks")]
        public float deadZone = 0.15f;
        public bool invertMoveX  = true;
        [Tooltip("Enable if pushing the stick up returns a negative value")]
        public bool invertMoveY  = true;
        public bool invertPitchY = false;

        void Update()
        {
            if(!IsServer) return;

            float dt = Time.deltaTime;

            // Read analog channels from the VRPN gamepad
            float lx = (float)VRPN.vrpnAnalog(analogDevice, strafeAxis);
            float ly = (float)VRPN.vrpnAnalog(analogDevice, forwardAxis);
            float rx = (float)VRPN.vrpnAnalog(analogDevice, yawAxis);
            float ry = (float)VRPN.vrpnAnalog(analogDevice, pitchAxis);
            float lt = (float)VRPN.vrpnAnalog(analogDevice, leftTriggerAxis);
            float rt = (float)VRPN.vrpnAnalog(analogDevice, rightTriggerAxis);

            if(invertMoveX)  lx = -lx;
            if(invertMoveY)  ly = -ly;
            if(invertPitchY) ry = -ry;

            bool sprint = VRPN.vrpnButton(buttonDevice, sprintButton);
            float speedMul = sprint ? sprintMultiplier : 1f;

            // --- Movement (left stick) ---
            Vector2 move = new Vector2(lx, ly);
            if(move.magnitude > deadZone)
            {
                move = Vector2.ClampMagnitude(move, 1f);
                transform.Translate(move.x * moveSpeed * speedMul * dt, 0f, move.y * moveSpeed * speedMul * dt, Space.Self);
            }

            // --- Rotation (right stick) ---
            Vector2 look = new Vector2(rx, ry);
            if(look.magnitude > deadZone)
            {
                look = Vector2.ClampMagnitude(look, 1f);
                transform.Rotate(0f, look.x * turnSpeed * dt, 0f, Space.World);   // yaw around world up
                transform.Rotate(look.y * turnSpeed * dt, 0f, 0f, Space.Self);    // pitch around local right
            }

            // --- Vertical (triggers) ---
            float vertical = Mathf.Clamp01(rt) - Mathf.Clamp01(lt);
            if(Mathf.Abs(vertical) > 0.01f)
            {
                transform.Translate(0f, vertical * verticalSpeed * speedMul * dt, 0f, Space.World);
            }
        }
    }
}
