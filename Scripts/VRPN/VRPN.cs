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
using System.Collections.Generic;
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

        private const string HostToken = "TRACKER_HOST";   // replaced by the "trackerHost <host>" launch argument
        private static string _trackerHost;
        private static bool _trackerHostRead;
        private static readonly Dictionary<string, string> _resolved = new Dictionary<string, string>();

        /// <summary>
        /// The address with TRACKER_HOST replaced by the trackerHost launch argument, so a prefab can ship
        /// without the tracking server's address. Null when the argument is missing.
        /// </summary>
        public static string ResolveAddress(string address)
        {
            if(address == null || address.IndexOf(HostToken, System.StringComparison.Ordinal) < 0) return address;
            if(_resolved.TryGetValue(address, out string resolved)) return resolved;

            if(!_trackerHostRead)
            {
                _trackerHost = Util.GetArg("trackerHost");
                _trackerHostRead = true;
            }
            resolved = string.IsNullOrEmpty(_trackerHost) ? null : address.Replace(HostToken, _trackerHost);
            if(resolved == null)
                Debug.LogWarning("VRPN: '" + address + "' needs the trackerHost launch argument (TrackerHost in nodes.json); this device is disabled.");
            _resolved[address] = resolved;
            return resolved;
        }

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
            address = ResolveAddress(address);
            if(address == null) return 0.0;
            try { return vrpnAnalogExtern(address, channel, Time.frameCount); }
            catch(System.DllNotFoundException e) { LogDllWarning(e); return 0.0; }
            catch(System.EntryPointNotFoundException e) { LogDllWarning(e); return 0.0; }
        }

        public static bool vrpnButton(string address, int channel)
        {
            if(!_dllAvailable) return false;
            address = ResolveAddress(address);
            if(address == null) return false;
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
            address = ResolveAddress(address);
            if(address == null) return false;
            try { return vrpnButtonExtern(address, channel, frameCount); }
            catch(System.DllNotFoundException e) { LogDllWarning(e); return false; }
            catch(System.EntryPointNotFoundException e) { LogDllWarning(e); return false; }
        }

        //todo - need to allow different transforms here...
        //need to adjust two below functions to match up with your own tracking system's transform
        public static Vector3 vrpnTrackerPos(string address, int channel)
        {
            if(!_dllAvailable) return Vector3.zero;
            address = ResolveAddress(address);
            if(address == null) return Vector3.zero;
            try
            {
                // Vicon is right-handed and Unity left-handed, both Y-up: flip Z.
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
            address = ResolveAddress(address);
            if(address == null) return Quaternion.identity;
            try
            {
                // Same Z flip for the rotation: (qx, qy, qz, qw) -> (-qx, -qy, qz, qw). A rigid body
                // whose axes differ from the device's is aligned with VRPNTrack.trackerRotationOffset.
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