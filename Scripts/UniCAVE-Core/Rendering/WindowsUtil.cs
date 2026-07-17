using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UniCAVE
{
    /// <summary>
    /// Used to resize, rename, and relocate the unity window
    /// </summary>
    public class WindowsUtils
    {
#if UNITY_STANDALONE_WIN

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, int hWndInsertAfter, int x, int y, int cx, int cy, int wFlags);

        [DllImport("user32.dll")]
        private static extern bool SetWindowText(IntPtr hwnd, string lpString);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private static int _desiredWidth = -1, _desiredHeight = -1;
        private static int _desiredX = -1, _desiredY = -1;
        private static string _desiredName;
        private static bool _pending = false; // only reposition once SetMyWindowInfo supplies real bounds
        private static int _delayFrames = 0;
        private const int DELAY = 2;       // min frames to wait before first attempt
        private const int MAX_RETRIES = 60; // give up after ~1 second at 60 fps

        // Find the top-level Unity window belonging to the current process.
        // GetActiveWindow() only works for the foreground instance, which breaks
        // multi-instance launches where only one process can be foreground at a time.
        private static IntPtr FindMyMainWindow()
        {
            int myPid = Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;
            var classBuf = new System.Text.StringBuilder(256);

            EnumWindows((hwnd, lParam) =>
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if(pid != (uint)myPid) return true;
                if(!IsWindowVisible(hwnd)) return true;

                GetClassName(hwnd, classBuf, classBuf.Capacity);
                if(classBuf.ToString().StartsWith("UnityWndClass"))
                {
                    found = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }

        /// <summary>
        /// Set the unity window's position, title, and size.
        /// </summary>
        public static void SetMyWindowInfo(string text, int posX, int posY, int width, int height, int setWidth, int setHeight)
        {
            _desiredWidth = width;
            _desiredHeight = height;
            _desiredName = text;
            _desiredX = posX;
            _desiredY = posY;
            _pending = true;
            _delayFrames = 0;
        }

        /// <summary>
        /// Returns true if there are no pending operations, or all operations have completed.
        /// Call this each frame from Update().
        /// </summary>
        public static bool CompletedOperation()
        {
            if(!_pending) return true;

            // Never reposition with unset/invalid dimensions. This guards managed
            // (CAVE2) and exclusive-fullscreen displays, which never call
            // SetMyWindowInfo and would otherwise get SetWindowPos(-1,-1,-1,-1)
            // plus Screen.SetResolution(-1,-1), collapsing the window/backbuffer.
            if(_desiredWidth <= 0 || _desiredHeight <= 0)
            {
                _pending = false;
                return true;
            }

            _delayFrames++;
            if(_delayFrames < DELAY) return false;

            IntPtr hwnd = FindMyMainWindow();
            if(hwnd == IntPtr.Zero)
            {
                if(_delayFrames < MAX_RETRIES) return false;
                Debug.LogWarning("WindowsUtils: could not find own Unity window after " + MAX_RETRIES + " frames, giving up.");
                _pending = false;
                return true;
            }

            Debug.Log("WindowsUtils: Repositioning window to " + _desiredX + "," + _desiredY + " size " + _desiredWidth + "x" + _desiredHeight);
            SetWindowText(hwnd, _desiredName);
            SetWindowPos(hwnd, 0, _desiredX, _desiredY, _desiredWidth, _desiredHeight, 0);
            Screen.SetResolution(_desiredWidth, _desiredHeight, false);
            _pending = false;
            return true;
        }

#endif
    }
}
