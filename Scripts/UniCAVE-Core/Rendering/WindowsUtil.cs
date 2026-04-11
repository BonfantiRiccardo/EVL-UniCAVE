using System;
using System.Runtime.InteropServices;
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
        private static extern bool SetWindowText(IntPtr hwnd, string lpString);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        private static int _desiredWidth = -1, _desiredHeight = -1;
        private static int _desiredX = -1, _desiredY = -1;
        private static string _desiredName;
        private static bool _pending = false;
        private static int _delayFrames = 0;
        private const int DELAY = 2; // wait a couple of frames for the window to be ready

        /// <summary>
        /// Set the unity window's position, title, and size.
        /// Uses GetActiveWindow to obtain the handle directly — no magic resolution trick.
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

            // Wait a couple of frames so the window is fully initialized
            _delayFrames++;
            if(_delayFrames < DELAY) return false;

            IntPtr hwnd = GetActiveWindow();
            if(hwnd != IntPtr.Zero)
            {
                Debug.Log("WindowsUtils: Repositioning window to " + _desiredX + "," + _desiredY + " size " + _desiredWidth + "x" + _desiredHeight);
                SetWindowText(hwnd, _desiredName);
                SetWindowPos(hwnd, 0, _desiredX, _desiredY, _desiredWidth, _desiredHeight, 0);
            }
            else
            {
                Debug.LogWarning("WindowsUtils: GetActiveWindow returned null, cannot reposition window.");
            }

            Screen.SetResolution(_desiredWidth, _desiredHeight, false);
            _pending = false;
            return true;
        }

#endif
    }
}
