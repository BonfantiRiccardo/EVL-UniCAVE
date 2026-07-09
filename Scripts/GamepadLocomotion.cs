using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

namespace UniCAVE
{
    /// <summary>
    /// Gamepad-driven locomotion for the UniCAVE root, using a standard gamepad
    /// (e.g. a wireless Xbox controller paired to the master) read through Unity's
    /// Input System. No VRPN / Omicron needed.
    ///
    ///   Left stick   : move relative to directionSource's forward/right (ground-projected).
    ///   Right stick X: turn (yaw the whole CAVE around the viewer).
    ///   Triggers     : right trigger = up, left trigger = down.
    ///
    /// Set directionSource to the Head (glasses) for "move where I look", or to the
    /// tracked Wand for "move where I point".
    ///
    /// Attach to the UniCAVE root (same GameObject as UCNetwork). Server-only; the
    /// resulting root transform is replicated to all display nodes by UCNetwork.
    /// Move, turn and vertical are handled independently so any one works on its own.
    /// </summary>
    public class GamepadLocomotion : NetworkBehaviour
    {
        [Header("Direction basis")]
        [Tooltip("Whose forward/right defines movement. Head = move where you look; " +
                 "Wand = move where you point. Its horizontal facing is used, and turns " +
                 "pivot around its position.")]
        public Transform directionSource;

        [Header("Move (left stick)")]
        [Tooltip("Translation speed in meters per second.")]
        public float moveSpeed = 1f;

        [Tooltip("Extra dead zone on top of the Input System's own stick processing.")]
        public float deadZone = 0.1f;

        [Tooltip("Keep movement on the ground plane (facing up/down won't rise/sink).")]
        public bool horizontalOnly = true;

        public bool invertStrafe  = false;
        [Tooltip("Enable if pushing the stick up moves you backward.")]
        public bool invertForward = false;

        [Header("Turn (right stick X)")]
        [Tooltip("Yaw speed in degrees/second. Rotates the world around the viewer " +
                 "(directionSource's position), so you turn in place instead of orbiting.")]
        public float turnSpeed = 10f;

        [Tooltip("Flip if pushing the right stick right turns you left.")]
        public bool invertTurn = false;

        [Header("Up / Down (triggers)")]
        [Tooltip("Vertical speed in meters/second. Right trigger raises, left trigger lowers.")]
        public float verticalSpeed = 1f;

        void Update()
        {
            if(!IsServer) return;
            if(directionSource == null) return;

            Gamepad gp = Gamepad.current;
            if(gp == null) return;

            float dt = Time.deltaTime;

            // --- Move: left stick, relative to directionSource ---
            Vector2 stick = gp.leftStick.ReadValue();
            float lx = invertStrafe  ? -stick.x : stick.x;
            float ly = invertForward ? -stick.y : stick.y;
            Vector2 move = new Vector2(lx, ly);
            if(move.magnitude > deadZone)
            {
                move = Vector2.ClampMagnitude(move, 1f);

                Vector3 fwd   = directionSource.forward;
                Vector3 right = directionSource.right;
                bool validBasis = true;

                if(horizontalOnly)
                {
                    fwd.y = 0f;
                    right.y = 0f;

                    if(fwd.sqrMagnitude < 1e-6f)
                    {
                        // Facing straight up/down: try to rebuild forward from right.
                        if(right.sqrMagnitude < 1e-6f) validBasis = false;
                        else { right.Normalize(); fwd = Vector3.Cross(right, Vector3.up); }
                    }
                    else if(right.sqrMagnitude < 1e-6f)
                    {
                        fwd.Normalize();
                        right = Vector3.Cross(Vector3.up, fwd);
                    }
                    else
                    {
                        fwd.Normalize();
                        right.Normalize();
                    }
                }

                if(validBasis)
                    transform.position += dt * moveSpeed * (right * move.x + fwd * move.y);
            }

            // --- Turn: right stick X, yaw around the viewer (no orbiting) ---
            float turn = gp.rightStick.ReadValue().x;
            if(invertTurn) turn = -turn;
            if(Mathf.Abs(turn) > deadZone)
            {
                transform.RotateAround(directionSource.position, Vector3.up, turn * turnSpeed * dt);
            }

            // --- Up / down: triggers (right = up, left = down), world vertical ---
            float vertical = gp.rightTrigger.ReadValue() - gp.leftTrigger.ReadValue();
            if(Mathf.Abs(vertical) > 0.01f)
            {
                transform.position += Vector3.up * (vertical * verticalSpeed * dt);
            }
        }
    }
}
