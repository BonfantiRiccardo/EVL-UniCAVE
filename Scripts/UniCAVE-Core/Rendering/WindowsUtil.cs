using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using UnityEngine;

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
        private static extern bool SetWindowText(System.IntPtr hwnd, System.String lpString);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private static int _setWidth = -1, _setHeight = -1;
        private static int _desiredWidth = -1, _desiredHeight = -1;
        private static int _desiredX = -1, _desiredY = -1;
        private static string _desiredName;
        private static bool _pending = false;
        private static int _retryCount = 0;
        private const int MAX_RETRIES = 120; // ~2 seconds at 60fps

        /// <summary>
        /// Set the unity window's position, title, and size.
        /// First sets a temporary unique resolution to identify the window handle,
        /// then repositions/resizes via Win32 calls.
        /// </summary>
        public static void SetMyWindowInfo(string text, int posX, int posY, int width, int height, int setWidth, int setHeight)
        {
            _setWidth = setWidth;
            _setHeight = setHeight;
            _desiredWidth = width;
            _desiredHeight = height;
            _desiredName = text;
            _desiredX = posX;
            _desiredY = posY;
            _pending = true;
            _retryCount = 0;
            _resolutions = loadAllWindowSizes();
            Screen.SetResolution(setWidth, setHeight, false);
        }

        private static Dictionary<long, Vector2Int> _resolutions = null;

        private static Dictionary<long, Vector2Int> loadAllWindowSizes()
        {
            Dictionary<long, Vector2Int> res = new Dictionary<long, Vector2Int>();

            EnumWindows(delegate (IntPtr wnd, IntPtr param)
            {
                RECT r = new RECT();
                GetWindowRect(wnd, out r);

                int wndWidth = r.Right - r.Left;
                int wndHeight = r.Bottom - r.Top;

                if(!res.ContainsKey(wnd.ToInt64()))
                    res.Add(wnd.ToInt64(), new Vector2Int(wndWidth, wndHeight));

                return true;
            }, IntPtr.Zero);

            return res;
        }

        /// <summary>
        /// Returns true if there are no pending operations, or all operations have completed.
        /// Call this each frame from Update() — it will keep retrying until the window is found and resized.
        /// </summary>
        public static bool CompletedOperation()
        {
            if(!_pending) return true;

            _retryCount++;

            // Try to find the window by the magic resolution we set
            if(_resolutions != null)
            {
                Dictionary<long, Vector2Int> newResolutions = loadAllWindowSizes();
                foreach(var kvp in newResolutions)
                {
                    if(kvp.Value.x == _setWidth && kvp.Value.y == _setHeight)
                    {
                        Debug.Log("WindowsUtils: Found window handle, repositioning to " + _desiredX + "," + _desiredY + " size " + _desiredWidth + "x" + _desiredHeight);
                        SetWindowText(new IntPtr(kvp.Key), _desiredName);
                        SetWindowPos(new IntPtr(kvp.Key), 0, _desiredX, _desiredY, _desiredWidth, _desiredHeight, _desiredWidth * _desiredHeight == 0 ? 1 : 0);
                        Screen.SetResolution(_desiredWidth, _desiredHeight, false);
                        _resolutions = null;
                        _pending = false;
                        return true;
                    }
                }
            }

            // Fallback: if we can't find the window by magic resolution after timeout,
            // try using GetActiveWindow and force the resolution directly
            if(_retryCount > MAX_RETRIES)
            {
                Debug.LogWarning("WindowsUtils: Could not find window by magic resolution after " + MAX_RETRIES + " frames. Using GetActiveWindow fallback.");
                IntPtr hwnd = GetActiveWindow();
                if(hwnd != IntPtr.Zero)
                {
                    SetWindowText(hwnd, _desiredName);
                    SetWindowPos(hwnd, 0, _desiredX, _desiredY, _desiredWidth, _desiredHeight, 0);
                }
                Screen.SetResolution(_desiredWidth, _desiredHeight, false);
                _resolutions = null;
                _pending = false;
                return true;
            }

            return false;
        }

#endif
    }
}