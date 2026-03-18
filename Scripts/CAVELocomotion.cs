using UnityEngine;
using Unity.Netcode;

namespace UniCAVE
{
    public class VRPNLocomotion : NetworkBehaviour
    {
        public string analogDevice  = "Controller0@localhost";
        public int    forwardAxis   = 1; // left stick Y
        public int    turnAxis      = 0; // left stick X
        public float  moveSpeed     = 3f;
        public float  turnSpeed     = 60f;

        void Update()
        {
            if (!IsServer) return;

            float forward = (float)VRPN.vrpnAnalog(analogDevice, forwardAxis);
            float turn    = (float)VRPN.vrpnAnalog(analogDevice, turnAxis);

            transform.Translate(0, 0, forward * moveSpeed * Time.deltaTime, Space.Self);
            transform.Rotate(0, turn * turnSpeed * Time.deltaTime, 0, Space.World);
        }
    }
}