using UnityEngine;
using Unity.Netcode;

namespace UniCAVE
{
    /// <summary>
    /// Wand-driven locomotion for the UniCAVE root.
    /// Attach to the UniCAVE root GameObject (same GameObject as UCNetwork).
    ///
    /// Reads the wand's analog stick via VRPN and translates the CAVE root
    /// along the *wand's* forward/right axes (projected horizontally by default).
    /// Push the stick forward to move whichever direction the wand is pointing,
    /// push left/right to strafe relative to that same pointing direction.
    ///
    /// Pure translation – no rotation is applied. The user changes the "facing"
    /// direction by physically pointing the wand. Head tracking via VRPNTrack
    /// on the Head child still gives the correct off-axis projection for each
    /// display independently of where the CAVE root is or which way the wand
    /// is pointing.
    ///
    /// Server-only; the resulting CAVE-root transform is replicated to clients
    /// by UCNetwork.SetTransformsClientRpc every frame.
    /// </summary>
    public class WandLocomotion : NetworkBehaviour
    {
        [Header("Wand Reference")]
        [Tooltip("The tracked Wand GameObject (a child of this UniCAVE root, with a VRPNTrack component). " +
                 "Its transform.forward / transform.right are used as the movement basis.")]
        public Transform wand;

        [Header("VRPN Device")]
        [Tooltip("VRPN analog device address for the wand's stick (e.g. Controller0@localhost or wandAnalog@192.168.1.10)")]
        public string analogDevice = "Controller0@localhost";

        [Header("Analog Channels")]
        [Tooltip("Left-stick X channel (strafe)")]
        public int strafeAxis  = 0;
        [Tooltip("Left-stick Y channel (forward / back)")]
        public int forwardAxis = 1;

        [Header("Movement")]
        [Tooltip("Translation speed in meters per second")]
        public float moveSpeed = 3f;

        [Tooltip("Dead zone for the left stick (combined magnitude)")]
        public float deadZone = 0.15f;

        [Tooltip("Project the wand's forward/right to the horizontal plane. " +
                 "Recommended: tilting the wand up or down should not make the CAVE rise or sink " +
                 "(avoids motion sickness and keeps navigation predictable).")]
        public bool horizontalOnly = true;

        [Header("Axis Inversion")]
        [Tooltip("Invert left-stick X if strafing goes the wrong way.")]
        public bool invertStrafe  = true;
        [Tooltip("Invert left-stick Y. Most VRPN drivers return negative for stick-up, " +
                 "so this is typically enabled so that pushing up moves forward.")]
        public bool invertForward = true;

        void Update()
        {
            if(!IsServer) return;
            if(wand == null) return;

            // Read stick
            float lx = (float)VRPN.vrpnAnalog(analogDevice, strafeAxis);
            float ly = (float)VRPN.vrpnAnalog(analogDevice, forwardAxis);

            if(invertStrafe)  lx = -lx;
            if(invertForward) ly = -ly;

            Vector2 move = new Vector2(lx, ly);
            if(move.magnitude <= deadZone) return;
            move = Vector2.ClampMagnitude(move, 1f);

            // Direction basis from the wand's world-space axes
            Vector3 wandForward = wand.forward;
            Vector3 wandRight   = wand.right;

            if(horizontalOnly)
            {
                wandForward.y = 0f;
                wandRight.y   = 0f;

                // Guard against degenerate cases (wand pointing straight up or down)
                if(wandForward.sqrMagnitude < 1e-6f)
                {
                    // Fall back: derive right-handed horizontal basis from remaining right vector
                    if(wandRight.sqrMagnitude < 1e-6f) return;
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
            }

            Vector3 delta = (wandRight * move.x + wandForward * move.y) * moveSpeed * Time.deltaTime;

            // World-space translation on the CAVE root. UCNetwork picks up
            // transform.position and replicates to clients next frame.
            transform.position += delta;
        }
    }
}
