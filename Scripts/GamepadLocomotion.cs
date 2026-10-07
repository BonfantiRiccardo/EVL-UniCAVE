using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

namespace UniCAVE
{
    /// <summary>
    /// Moves the CAVE root with a standard gamepad read through the Input System, as a backup for the CAVE2
    /// wand: the left stick moves, the right stick turns, the triggers fly up and down, the left shoulder
    /// sprints and the right shoulder resets. Server-only; UCNetwork replicates the root.
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

        [Header("Sprint / Reset (shoulders)")]
        [Tooltip("Speed multiplier for moving and flying while the left shoulder is held.")]
        public float sprintMultiplier = 1.5f;

        // Captured at Start so the reset button has somewhere to return to.
        private Vector3 startPosition;
        private Quaternion startRotation;

        private Gamepad loggedPad; // last device announced in the log

        void Start()
        {
            startPosition = transform.position;
            startRotation = transform.rotation;
        }

        void Update()
        {
            if(!IsServer) return;
            if(directionSource == null) return;

            Gamepad gp = Gamepad.current;
            if(gp == null) return;

            if(gp != loggedPad)
            {
                loggedPad = gp;
                Debug.Log($"[GamepadLocomotion] reading gamepad '{gp.displayName}' (layout {gp.layout})");
            }

            if(gp.rightShoulder.wasPressedThisFrame)
            {
                transform.SetPositionAndRotation(startPosition, startRotation);
                return;
            }

            float dt = Time.deltaTime;
            float speedMul = gp.leftShoulder.isPressed ? sprintMultiplier : 1f;

            // Left stick moves relative to directionSource.
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
                    transform.position += dt * moveSpeed * speedMul * (right * move.x + fwd * move.y);
            }

            // Right stick X turns around the viewer.
            float turn = gp.rightStick.ReadValue().x;
            if(invertTurn) turn = -turn;
            if(Mathf.Abs(turn) > deadZone)
            {
                transform.RotateAround(directionSource.position, Vector3.up, turn * turnSpeed * dt);
            }

            // Right trigger up, left trigger down.
            float vertical = gp.rightTrigger.ReadValue() - gp.leftTrigger.ReadValue();
            if(Mathf.Abs(vertical) > 0.01f)
            {
                transform.position += Vector3.up * (vertical * verticalSpeed * speedMul * dt);
            }
        }
    }
}
