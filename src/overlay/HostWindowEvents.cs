using System;
using System.Runtime.InteropServices;

namespace CodexInputUsage
{
    /// <summary>
    /// Out-of-context Win32 WinEvent hooks on the UI thread for zero-lag foreground transitions
    /// and 60fps window-drag tracking without polling delay or DLL injection.
    /// </summary>
    internal sealed class HostWindowEvents : IDisposable
    {
        private const uint EventSystemForeground = 0x0003;
        private const uint EventSystemMoveSizeStart = 0x000A;
        private const uint EventSystemMoveSizeEnd = 0x000B;
        private const uint EventSystemMinimizeStart = 0x0016;
        private const uint EventSystemMinimizeEnd = 0x0017;
        private const uint EventObjectLocationChange = 0x800B;
        private const uint WinEventOutOfContext = 0x0000;
        private const uint WinEventSkipOwnProcess = 0x0002;
        private const int ObjIdWindow = 0;
        private const int ChildIdSelf = 0;

        private delegate void WinEventProc(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventProc lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private readonly WinEventProc _callback;
        private IntPtr _globalHook = IntPtr.Zero;
        private IntPtr _locationHook = IntPtr.Zero;
        private uint _trackedPid;
        private IntPtr _trackedHwnd = IntPtr.Zero;
        private bool _disposed;

        internal event EventHandler ForegroundChanged;
        internal event EventHandler TrackedWindowMoved;
        internal event EventHandler TrackedWindowMoveSizeEnded;
        internal event EventHandler TrackedWindowMinimized;

        internal HostWindowEvents()
        {
            // Keep delegate rooted for the lifetime of the hooks.
            _callback = OnWinEvent;
            _globalHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemMinimizeEnd,
                IntPtr.Zero,
                _callback,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
        }

        internal void SetTrackedWindow(IntPtr hwnd)
        {
            if (_disposed) return;
            if (hwnd == _trackedHwnd) return;

            _trackedHwnd = hwnd;
            uint nextPid = 0;
            if (hwnd != IntPtr.Zero)
                NativeWin32.GetWindowThreadProcessId(hwnd, out nextPid);

            if (nextPid != _trackedPid)
            {
                UnhookLocation();
                _trackedPid = nextPid;
                if (_trackedPid != 0)
                {
                    // Scope LOCATIONCHANGE strictly to the Codex process so other apps incur zero callback cost.
                    _locationHook = SetWinEventHook(
                        EventObjectLocationChange,
                        EventObjectLocationChange,
                        IntPtr.Zero,
                        _callback,
                        _trackedPid,
                        0,
                        WinEventOutOfContext | WinEventSkipOwnProcess);
                }
            }
        }

        private void OnWinEvent(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime)
        {
            if (_disposed) return;

            if (eventType == EventSystemForeground)
            {
                if (ForegroundChanged != null) ForegroundChanged(this, EventArgs.Empty);
                return;
            }

            if (_trackedHwnd == IntPtr.Zero || hwnd != _trackedHwnd) return;

            if (eventType == EventObjectLocationChange)
            {
                if (idObject == ObjIdWindow && idChild == ChildIdSelf)
                {
                    if (TrackedWindowMoved != null) TrackedWindowMoved(this, EventArgs.Empty);
                }
                return;
            }

            if (eventType == EventSystemMoveSizeEnd || eventType == EventSystemMinimizeEnd)
            {
                if (TrackedWindowMoveSizeEnded != null) TrackedWindowMoveSizeEnded(this, EventArgs.Empty);
                return;
            }

            if (eventType == EventSystemMinimizeStart)
            {
                if (TrackedWindowMinimized != null) TrackedWindowMinimized(this, EventArgs.Empty);
                return;
            }

            if (eventType == EventSystemMoveSizeStart)
            {
                if (TrackedWindowMoved != null) TrackedWindowMoved(this, EventArgs.Empty);
            }
        }

        private void UnhookLocation()
        {
            if (_locationHook != IntPtr.Zero)
            {
                try { UnhookWinEvent(_locationHook); } catch { }
                _locationHook = IntPtr.Zero;
            }
            _trackedPid = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnhookLocation();
            if (_globalHook != IntPtr.Zero)
            {
                try { UnhookWinEvent(_globalHook); } catch { }
                _globalHook = IntPtr.Zero;
            }
        }
    }
}
