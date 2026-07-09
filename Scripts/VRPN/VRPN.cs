//MIT License
//Copyright 2016-Present 
//Ross Tredinnick
//Brady Boettcher
//Living Environments Laboratory
//Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), 
//to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, 
//sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
//The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, 
//INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
//IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
//TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using UnityEngine;
using System.Runtime.InteropServices;

namespace UniCAVE
{
    public static class VRPN
    {
        [DllImport("unityVrpn")]
        private static extern double vrpnAnalogExtern(string address, int channel, int frameCount);

        [DllImport("unityVrpn")]
        private static extern bool vrpnButtonExtern(string address, int channel, int frameCount);

        [DllImport("unityVrpn")]
        private static extern double vrpnTrackerExtern(string address, int channel, int component, int frameCount);

        private static bool _dllAvailable = true;
        private static bool _dllWarningLogged = false;

        private static void LogDllWarning(System.Exception e)
        {
            if(!_dllWarningLogged)
            {
                Debug.LogWarning("VRPN: Native plugin 'unityVrpn' not available on this platform. VRPN tracking/input disabled. (" + e.GetType().Name + ")");
                _dllWarningLogged = true;
            }
            _dllAvailable = false;
        }

        /// <summary>
        /// Returns true if the native VRPN plugin is loaded and available.
        /// </summary>
        public static bool IsAvailable => _dllAvailable;

        public static double vrpnAnalog(string address, int channel)
        {
            if(!_dllAvailable) return 0.0;
            try { return vrpnAnalogExtern(address, channel, Time.frameCount); }
            catch(System.DllNotFoundException e) { LogDllWarning(e); return 0.0; }
            catch(System.EntryPointNotFoundException e) { LogDllWarning(e); return 0.0; }
        }

        public static bool vrpnButton(string address, int channel)
        {
            if(!_dllAvailable) return false;
            try { return vrpnButtonExtern(address, channel, Time.frameCount); }
            catch(System.DllNotFoundException e) { LogDllWarning(e); return false; }
            catch(System.EntryPointNotFoundException e) { LogDllWarning(e); return false; }
        }

        /// <summary>
        /// Get if a tracker button is pressed. Needed for editor mode since framecount is not available.
        /// </summary>
        /// <param name="address">The address of the tracker</param>
        /// <param name="channel">The button number of the button (0 based).</param>
        /// <param name="frameCount">The count of the frame.</param>
        /// <returns></returns>
        public static bool vrpnButton(string address, int channel, int frameCount)
        {
            if(!_dllAvailable) return false;
            try { return vrpnButtonExtern(address, channel, frameCount); }
            catch(System.DllNotFoundException e) { LogDllWarning(e); return false; }
            catch(System.EntryPointNotFoundException e) { LogDllWarning(e); return false; }
        }

        //todo - need to allow different transforms here...
        //need to adjust two below functions to match up with your own tracking system's transform
        public static Vector3 vrpnTrackerPos(string address, int channel)
        {
            if(!_dllAvailable) return Vector3.zero;
            try
            {
                // Vicon (Y-up, meters, right-handed) -> Unity (Y-up, left-handed):
                // flip Z only. x = vicon.x, y = vicon.y, z = -vicon.z. Confirmed with a
                // CAVE2 cube parallax test on 2026-07-08.
                return new Vector3(
                    (float)vrpnTrackerExtern(address, channel, 0, Time.frameCount),
                    (float)vrpnTrackerExtern(address, channel, 1, Time.frameCount),
                    -(float)vrpnTrackerExtern(address, channel, 2, Time.frameCount));
            }
            catch(System.DllNotFoundException e) { LogDllWarning(e); return Vector3.zero; }
            catch(System.EntryPointNotFoundException e) { LogDllWarning(e); return Vector3.zero; }
        }

        public static Quaternion vrpnTrackerQuat(string address, int channel)
        {
            if(!_dllAvailable) return Quaternion.identity;
            try
            {
                // Match the position mapping (flip Z only, right-handed -> left-handed):
                // (qx,qy,qz,qw) -> (-qx,-qy,qz,qw). The Wand_Batman rigid-body local
                // axes may still be offset from the wand's physical "forward" - align
                // that with VRPNTrack.trackerRotationOffset. If rotation comes out
                // MIRRORED, try (qx, qy, -qz, -qw) instead.
                return new Quaternion(
                    -(float)vrpnTrackerExtern(address, channel, 3, Time.frameCount),
                    -(float)vrpnTrackerExtern(address, channel, 4, Time.frameCount),
                    (float)vrpnTrackerExtern(address, channel, 5, Time.frameCount),
                    (float)vrpnTrackerExtern(address, channel, 6, Time.frameCount));
            }
            catch(System.DllNotFoundException e) { LogDllWarning(e); return Quaternion.identity; }
            catch(System.EntryPointNotFoundException e) { LogDllWarning(e); return Quaternion.identity; }
        }
    }
}