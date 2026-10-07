using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

namespace UniCAVE
{
    /// <summary>
    /// Keyboard and gamepad navigation for the UniCAVE root, on the server only.
    /// UCNetwork replicates the root to the clients.
    /// </summary>
    public class CAVENavigator : NetworkBehaviour
    {
        [Header("Movement")]
        [Tooltip("Translation speed in meters per second")]
        public float moveSpeed = 2.0f;

        [Tooltip("Shift key multiplier for faster movement")]
        public float sprintMultiplier = 3.0f;

        [Header("Rotation")]
        [Tooltip("Rotation speed in degrees per second")]
        public float rotateSpeed = 20.0f;

        [Header("Vertical")]
        [Tooltip("Speed for moving up/down (Q/E keys or triggers)")]
        public float verticalSpeed = 1.5f;

        [Header("Gamepad")]
        [Tooltip("Dead zone for gamepad sticks")]
        public float stickDeadZone = 0.15f;

        void Update()
        {
            // The editor runs as the server, so this also works for desk testing.
            if(!IsServer) return;

            float dt = Time.deltaTime;

            Vector2 moveInput = Vector2.zero;
            Vector2 lookInput = Vector2.zero;
            float verticalInput = 0f;
            bool sprint = false;

            // Keyboard
            var kb = Keyboard.current;
            if(kb != null)
            {
                // WASD movement
                if(kb.wKey.isPressed) moveInput.y += 1f;
                if(kb.sKey.isPressed) moveInput.y -= 1f;
                if(kb.aKey.isPressed) moveInput.x -= 1f;
                if(kb.dKey.isPressed) moveInput.x += 1f;

                // Arrow keys for rotation
                if(kb.leftArrowKey.isPressed) lookInput.x -= 1f;
                if(kb.rightArrowKey.isPressed) lookInput.x += 1f;
                if(kb.upArrowKey.isPressed) lookInput.y += 1f;
                if(kb.downArrowKey.isPressed) lookInput.y -= 1f;

                // Q/E for vertical movement
                if(kb.eKey.isPressed) verticalInput += 1f;
                if(kb.qKey.isPressed) verticalInput -= 1f;

                // Shift for sprint
                sprint = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            }

            // Gamepad input adds to the keyboard, so both work at once.
            var gp = Gamepad.current;
            if(gp != null)
            {
                Vector2 leftStick = gp.leftStick.ReadValue();
                Vector2 rightStick = gp.rightStick.ReadValue();

                if(leftStick.magnitude > stickDeadZone)
                {
                    moveInput.x += leftStick.x;
                    moveInput.y += leftStick.y;
                }

                if(rightStick.magnitude > stickDeadZone)
                {
                    lookInput.x += rightStick.x;
                    lookInput.y += rightStick.y;
                }

                // Triggers for vertical
                verticalInput += gp.rightTrigger.ReadValue();
                verticalInput -= gp.leftTrigger.ReadValue();

                // Left bumper for sprint
                if(gp.leftShoulder.isPressed) sprint = true;
            }

            // Clamp
            moveInput = Vector2.ClampMagnitude(moveInput, 1f);
            lookInput = Vector2.ClampMagnitude(lookInput, 1f);
            verticalInput = Mathf.Clamp(verticalInput, -1f, 1f);

            float speedMul = sprint ? sprintMultiplier : 1f;

            // Yaw around world up, pitch around local right.
            if(lookInput.sqrMagnitude > 0.001f)
            {
                transform.Rotate(Vector3.up, lookInput.x * rotateSpeed * dt, Space.World);
                transform.Rotate(Vector3.right, lookInput.y * rotateSpeed * dt, Space.Self);
            }

            Vector3 planarMove = (-transform.forward * moveInput.y) + (-transform.right * moveInput.x);
            Vector3 verticalMove = Vector3.up * (verticalInput * verticalSpeed);

            if(planarMove.sqrMagnitude > 0.001f || Mathf.Abs(verticalInput) > 0.01f)
            {
                transform.position += (planarMove * moveSpeed + verticalMove) * speedMul * dt;
            }
        }
    }
}
