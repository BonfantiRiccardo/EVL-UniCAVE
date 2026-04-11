using UnityEngine;
using Unity.Netcode;

namespace UniCAVE
{
    /// <summary>
    /// VRPN-based locomotion for the UniCAVE root.
    /// Reads analog channels for sticks and triggers, and a button for sprint.
    ///
    /// Mapping:
    ///   Left stick  -> position (strafe + forward/back)
    ///   Right stick -> rotation (yaw + pitch)
    ///   LT2         -> move down
    ///   RT2         -> move up
    ///   LT1         -> sprint (speed multiplier)
    /// </summary>
    public class VRPNLocomotion : NetworkBehaviour
    {
        [Header("VRPN Device")]
        public string analogDevice = "Controller0@localhost";
        public string buttonDevice = "Controller0@localhost";

        [Header("Analog Channels")]
        [Tooltip("Left stick X (strafe)")]
        public int strafeAxis   = 0;
        [Tooltip("Left stick Y (forward / back)")]
        public int forwardAxis  = 1;
        [Tooltip("Right stick X (yaw)")]
        public int yawAxis      = 2;
        [Tooltip("Right stick Y (pitch)")]
        public int pitchAxis    = 3;
        [Tooltip("Left trigger LT2 (move down)")]
        public int leftTriggerAxis  = 5;
        [Tooltip("Right trigger RT2 (move up)")]
        public int rightTriggerAxis = 4;

        [Header("Button Channels")]
        [Tooltip("Left bumper LT1 (sprint)")]
        public int sprintButton = 4;

        [Header("Speeds")]
        public float moveSpeed     = 3f;
        public float turnSpeed     = 20f;
        public float verticalSpeed = 1.5f;
        public float sprintMultiplier = 3f;

        [Header("Options")]
        [Tooltip("Dead zone for analog sticks")]
        public float deadZone = 0.15f;

        [Tooltip("Invert left stick X")]
        public bool invertMoveX = true;
        [Tooltip("Invert left stick Y (enable if stick-up returns negative)")]
        public bool invertMoveY = true;
        [Tooltip("Invert right stick Y (enable if stick-up returns negative)")]
        public bool invertPitchY = false;

        void Update()
        {
            if(!IsServer) return;

            float dt = Time.deltaTime;

            // Read all analog channels
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
                // Yaw around world Y
                transform.Rotate(0f, look.x * turnSpeed * dt, 0f, Space.World);
                // Pitch around local right axis
                transform.Rotate(look.y * turnSpeed * dt, 0f, 0f, Space.Self);
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
