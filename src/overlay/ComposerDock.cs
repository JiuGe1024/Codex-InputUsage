using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace CodexInputUsage
{
    internal enum DockSourceKind
    {
        None = 0,
        UiaContext = 1,
        UiaModel = 2,
        UiaComposer = 3
    }

    internal sealed class ComposerDockSnapshot
    {
        internal IntPtr WindowHandle;
        internal float DpiScale = 1f;
        internal DockSourceKind Source;
        internal Rectangle ReferenceRect;
        internal Rectangle RowRect;
        internal Rectangle ComposerRect;
        internal Rectangle WindowRect;
        internal int LeftBarrierX;
        internal string[] HeaderTexts = new string[0];
        internal string[] BodyTexts = new string[0];
        internal string Detail;
        internal long GeometryMs;
        internal long FingerprintMs;

        internal ComposerDockSnapshot WithFingerprints(string[] headers, string[] bodies, long elapsedMs)
        {
            var snapshot = (ComposerDockSnapshot)MemberwiseClone();
            snapshot.HeaderTexts = headers;
            snapshot.BodyTexts = bodies;
            snapshot.FingerprintMs = elapsedMs;
            return snapshot;
        }

        internal ComposerDockSnapshot KeepFingerprints(ComposerDockSnapshot previous)
        {
            return WindowHandle == previous.WindowHandle
                ? WithFingerprints(previous.HeaderTexts, previous.BodyTexts, previous.FingerprintMs)
                : this;
        }

        internal ComposerDockSnapshot ForLiveWindow(Rectangle live)
        {
            if (live.IsEmpty || live.Size != WindowRect.Size) return null;
            if (live == WindowRect) return this;

            int dx = live.X - WindowRect.X;
            int dy = live.Y - WindowRect.Y;
            var snapshot = (ComposerDockSnapshot)MemberwiseClone();
            snapshot.ReferenceRect = Offset(ReferenceRect, dx, dy);
            snapshot.RowRect = Offset(RowRect, dx, dy);
            snapshot.ComposerRect = Offset(ComposerRect, dx, dy);
            snapshot.LeftBarrierX += dx;
            snapshot.WindowRect = live;
            return snapshot;
        }

        private static Rectangle Offset(Rectangle rect, int dx, int dy)
        {
            if (!rect.IsEmpty) rect.Offset(dx, dy);
            return rect;
        }

        internal static readonly ComposerDockSnapshot Empty = new ComposerDockSnapshot
        {
            Source = DockSourceKind.None,
            HeaderTexts = new string[0],
            BodyTexts = new string[0],
            Detail = "empty"
        };

        internal int RowCenterY
        {
            get
            {
                if (!RowRect.IsEmpty) return RowRect.Top + (RowRect.Height / 2);
                if (!ReferenceRect.IsEmpty) return ReferenceRect.Top + (ReferenceRect.Height / 2);
                return ComposerRect.Bottom - OverlayDpi.Pixels(22, DpiScale);
            }
        }
    }

    internal static class NativeWin32
    {
        internal const int GWL_EXSTYLE = -20;
        internal const int DWMWA_CLOAKED = 14;
        internal const uint GA_ROOT = 2;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOREDRAW = 0x0008;
        internal const byte AC_SRC_OVER = 0x00;
        internal const byte AC_SRC_ALPHA = 0x01;
        internal const uint ULW_ALPHA = 0x00000002;
        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect { internal int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint { internal int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeSize { internal int CX, CY; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct BlendFunction
        {
            internal byte BlendOp;
            internal byte BlendFlags;
            internal byte SourceConstantAlpha;
            internal byte AlphaFormat;
        }

        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

        internal static float GetWindowDpiScale(IntPtr hwnd)
        {
            uint dpi = 0;
            if (hwnd != IntPtr.Zero)
            {
                try { dpi = GetDpiForWindow(hwnd); } catch (EntryPointNotFoundException) { }
            }
            if (dpi == 0)
            {
                try { dpi = GetDpiForSystem(); } catch (EntryPointNotFoundException) { }
            }
            return dpi == 0 ? 1f : Math.Max(1f, dpi / 96f);
        }
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(NativePoint point);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)]
        internal static extern bool GetUserObjectInformation(IntPtr obj, int index, StringBuilder info, uint length, out uint needed);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst, ref NativeSize psize,
            IntPtr hdcSrc, ref NativePoint pptSrc, uint crKey, ref BlendFunction pblend, uint dwFlags);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ErrorInsufficientBuffer = 122;
        private const int AppModelErrorNoApplication = 15703;

        internal static bool IsOfficialCodexProcess(Process process)
        {
            if (process == null) return false;
            try
            {
                if (!string.Equals(process.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch { return false; }

            IntPtr hProc = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
            if (hProc == IntPtr.Zero) return false;
            try
            {
                var pathBuf = new StringBuilder(1024);
                uint pathLen = (uint)pathBuf.Capacity;
                if (!QueryFullProcessImageName(hProc, 0, pathBuf, ref pathLen))
                    return false;

                string path = pathBuf.ToString();
                string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + "\\";
                if (!path.StartsWith(windowsApps + "OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (path.IndexOf("__2p2nqsd0c76g0\\app\\ChatGPT.exe", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

                // When launched via Microsoft Store AppModel, verify exact AUMID;
                // when launched directly via shortcut/launcher (APPMODEL_ERROR_NO_APPLICATION = 15703),
                // the TrustedInstaller-protected WindowsApps\OpenAI.Codex_*__2p2nqsd0c76g0\app\ChatGPT.exe path already verifies authenticity.
                uint aumidLen = 0;
                int rc = GetApplicationUserModelId(hProc, ref aumidLen, null);
                if (rc == AppModelErrorNoApplication)
                    return true;
                if (rc != ErrorInsufficientBuffer || aumidLen == 0 || aumidLen > 512)
                    return false;
                var id = new StringBuilder((int)aumidLen);
                return GetApplicationUserModelId(hProc, ref aumidLen, id) == 0
                    && id.ToString().StartsWith("OpenAI.Codex_2p2nqsd0c76g0!", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
            finally
            {
                CloseHandle(hProc);
            }
        }

        internal static IntPtr GetForegroundCodexWindow(out string nonMatchDesc)
        {
            nonMatchDesc = null;
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || IsIconic(fg))
            {
                nonMatchDesc = "fg-empty-or-iconic";
                return IntPtr.Zero;
            }
            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            if (pid == 0)
            {
                nonMatchDesc = "fg-pid-zero";
                return IntPtr.Zero;
            }
            try
            {
                using (var proc = Process.GetProcessById((int)pid))
                {
                    if (IsOfficialCodexProcess(proc)) return fg;
                    nonMatchDesc = string.Format("not-codex proc={0} pid={1} hwnd=0x{2:X}", proc.ProcessName, pid, fg.ToInt64());
                    return IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                nonMatchDesc = "fg-error=" + ex.GetType().Name;
                return IntPtr.Zero;
            }
        }

        internal static IntPtr FindAnyCodexWindow()
        {
            foreach (var proc in Process.GetProcessesByName("ChatGPT"))
            {
                using (proc)
                {
                    try
                    {
                        IntPtr hwnd = proc.MainWindowHandle;
                        if (hwnd != IntPtr.Zero && !IsIconic(hwnd) && IsOfficialCodexProcess(proc))
                            return hwnd;
                    }
                    catch { }
                }
            }
            return IntPtr.Zero;
        }

        internal static bool HasRunningCodexProcess()
        {
            foreach (var proc in Process.GetProcessesByName("ChatGPT"))
            {
                using (proc)
                {
                    try
                    {
                        if (IsOfficialCodexProcess(proc)) return true;
                    }
                    catch { }
                }
            }
            return false;
        }

        internal static Rectangle GetWindowRectangle(IntPtr hwnd)
        {
            NativeRect r;
            if (!GetWindowRect(hwnd, out r)) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        internal static string GetDesktopName()
        {
            try
            {
                IntPtr desk = GetThreadDesktop(GetCurrentThreadId());
                var sb = new StringBuilder(256);
                uint needed;
                if (GetUserObjectInformation(desk, 2, sb, (uint)(sb.Capacity * 2), out needed))
                    return sb.ToString();
            }
            catch { }
            return "unknown";
        }
    }

    internal sealed class ComposerDockTracker : IDisposable
    {
        private const string ComposerEditorClass = "ProseMirror";
        private const string ContextClassHint = "codex-description";
        private static readonly string[] ContextNameHints = new string[]
        {
            "上下文用量", "Context usage", "Context window", "context window"
        };

        private sealed class ChildInfo
        {
            internal AutomationElement Element;
            internal Rectangle Rect;
            internal string ControlType;
            internal string Name;
            internal string ClassName;
            internal bool HasCache;
        }

        private readonly Func<IntPtr> _windowProvider;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly AutoResetEvent _wakeSignal = new AutoResetEvent(false);
        private readonly AutoResetEvent _fingerprintSignal = new AutoResetEvent(false);
        private readonly object _snapshotGate = new object();
        private Thread _thread;
        private Thread _fingerprintThread;
        private volatile ComposerDockSnapshot _latest = ComposerDockSnapshot.Empty;
        private volatile bool _sessionBound;
        private bool _disposed;
        private int _forceFullWalkFlag;
        private int _fingerprintVersion;

        internal event EventHandler SnapshotUpdated;

        internal ComposerDockTracker(Func<IntPtr> windowProvider)
        {
            _windowProvider = windowProvider;
        }

        internal ComposerDockSnapshot Latest { get { return _latest; } }

        internal void SetSessionBound(bool bound)
        {
            if (_sessionBound == bound) return;
            _sessionBound = bound;
            if (!bound) WakeNow(false, true);
        }

        internal void WakeNow(bool forceFullWalk, bool forceFingerprint)
        {
            lock (_snapshotGate)
            {
                if (_disposed) return;
                if (forceFullWalk) Interlocked.Exchange(ref _forceFullWalkFlag, 1);
                if (forceFingerprint)
                {
                    _fingerprintVersion++;
                    _fingerprintSignal.Set();
                }
                _wakeSignal.Set();
            }
        }

        internal void Start()
        {
            if (_thread != null) return;
            _thread = StartWorker(RunLoop, "ComposerDock");
            _fingerprintThread = StartWorker(RunFingerprints, "ChatFingerprints");
        }

        private static Thread StartWorker(ThreadStart run, string name)
        {
            var thread = new Thread(run) { IsBackground = true, Name = "CodexInputUsage." + name };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            return thread;
        }

        public void Dispose()
        {
            lock (_snapshotGate)
            {
                if (_disposed) return;
                _disposed = true;
                _cts.Cancel();
                _wakeSignal.Set();
                _fingerprintSignal.Set();
            }
            // A UIA provider call may already be in flight. Keep both signals alive until the workers exit.
            if (_thread != null) _thread.Join();
            if (_fingerprintThread != null) _fingerprintThread.Join();
            _wakeSignal.Dispose();
            _fingerprintSignal.Dispose();
            _cts.Dispose();
        }

        internal static ComposerDockSnapshot MeasureOnce(IntPtr hwnd)
        {
            AutomationElement composer = null;
            AutomationElement contextEl = null;
            AutomationElement modelEl = null;
            return Discover(hwnd, ref composer, ref contextEl, ref modelEl);
        }

        internal static bool HoldPreviousAnchor(ComposerDockSnapshot previous, ComposerDockSnapshot next,
            long nowMs, ref long? degradedSinceMs)
        {
            bool degraded = (next.Source == DockSourceKind.None || next.Source == DockSourceKind.UiaComposer)
                && (previous.Source == DockSourceKind.UiaContext || previous.Source == DockSourceKind.UiaModel)
                && next.WindowHandle == previous.WindowHandle
                && next.WindowRect == previous.WindowRect
                && next.DpiScale == previous.DpiScale;
            if (!degraded)
            {
                degradedSinceMs = null;
                return false;
            }

            if (!degradedSinceMs.HasValue) degradedSinceMs = nowMs;
            return nowMs - degradedSinceMs.Value < 350;
        }

        private void PublishGeometry(ComposerDockSnapshot snapshot)
        {
            lock (_snapshotGate)
            {
                if (_disposed || snapshot.WindowHandle != _windowProvider()) return;
                if (snapshot.WindowHandle != _latest.WindowHandle)
                {
                    _sessionBound = false;
                    _fingerprintVersion++;
                }
                bool firstUsableGeometry = snapshot.Source != DockSourceKind.None
                    && (_latest.Source == DockSourceKind.None || snapshot.WindowHandle != _latest.WindowHandle);
                _latest = snapshot.KeepFingerprints(_latest);
                if (firstUsableGeometry) _fingerprintSignal.Set();
            }
            NotifySnapshotUpdated();
        }

        private void NotifySnapshotUpdated()
        {
            var handler = SnapshotUpdated;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RunLoop()
        {
            AutomationElement composer = null;
            AutomationElement contextEl = null;
            AutomationElement modelEl = null;
            IntPtr trackedHwnd = IntPtr.Zero;
            var clock = Stopwatch.StartNew();
            long lastDiscoveryMs = -12000;
            long? degradedSinceMs = null;
            string lastProviderError = null;
            var token = _cts.Token;

            while (!token.IsCancellationRequested)
            {
                IntPtr hwnd = _windowProvider();
                if (hwnd == IntPtr.Zero)
                {
                    _wakeSignal.WaitOne(220);
                    continue;
                }

                if (trackedHwnd != hwnd || _latest.WindowHandle != hwnd)
                {
                    trackedHwnd = hwnd;
                    composer = contextEl = modelEl = null;
                    degradedSinceMs = null;
                    PublishGeometry(new ComposerDockSnapshot { WindowHandle = hwnd, Detail = "new-window" });
                }

                var previous = _latest;
                Rectangle window = NativeWin32.GetWindowRectangle(hwnd);
                if (window.IsEmpty)
                {
                    _wakeSignal.WaitOne(50);
                    continue;
                }
                float scale = NativeWin32.GetWindowDpiScale(hwnd);
                bool needDiscovery = Interlocked.Exchange(ref _forceFullWalkFlag, 0) != 0
                    || composer == null || (contextEl == null && modelEl == null)
                    || scale != previous.DpiScale || clock.ElapsedMilliseconds - lastDiscoveryMs >= 12000;
                var elapsed = Stopwatch.StartNew();

                if (!needDiscovery)
                {
                    Rectangle reference;
                    Rectangle composerRect;
                    Rectangle row;
                    AutomationElement anchor = contextEl ?? modelEl;
                    if (!TryReadRect(anchor, out reference))
                    {
                        needDiscovery = true;
                    }
                    else if (reference != previous.ReferenceRect || window != previous.WindowRect)
                    {
                        row = reference;
                        if (!TryReadRect(composer, out composerRect)
                            || (contextEl != null && modelEl != null && !TryReadRect(modelEl, out row)))
                        {
                            needDiscovery = true;
                        }
                        else
                        {
                            var next = new ComposerDockSnapshot
                            {
                                WindowHandle = hwnd,
                                WindowRect = window,
                                DpiScale = scale,
                                ComposerRect = composerRect,
                                ReferenceRect = reference,
                                RowRect = row,
                                Source = previous.Source,
                                LeftBarrierX = previous.LeftBarrierX + composerRect.Left - previous.ComposerRect.Left,
                                GeometryMs = elapsed.ElapsedMilliseconds,
                                Detail = "fast-rect"
                            };
                            PublishGeometry(next);
                            needDiscovery = Math.Abs(composerRect.Width - previous.ComposerRect.Width) >= OverlayDpi.Pixels(12, scale)
                                || window.Size != previous.WindowRect.Size;
                        }
                    }
                }

                if (needDiscovery)
                {
                    elapsed.Restart();
                    ComposerDockSnapshot next;
                    try
                    {
                        next = Discover(hwnd, ref composer, ref contextEl, ref modelEl);
                        lastProviderError = null;
                    }
                    catch (Exception ex)
                    {
                        if (!IsRecoverableUiaError(ex)) throw;
                        composer = contextEl = modelEl = null;
                        string error = ex.GetType().Name;
                        if (lastProviderError != error)
                            Program.LogDiag(string.Format("geometry-uia-retry hwnd=0x{0:X} error={1}", hwnd.ToInt64(), ex.GetType().Name));
                        lastProviderError = error;
                        _wakeSignal.WaitOne(220);
                        continue;
                    }

                    next.GeometryMs = elapsed.ElapsedMilliseconds;
                    lastDiscoveryMs = clock.ElapsedMilliseconds;
                    if (HoldPreviousAnchor(_latest, next, lastDiscoveryMs, ref degradedSinceMs))
                    {
                        _wakeSignal.WaitOne(30);
                        continue;
                    }

                    degradedSinceMs = null;
                    PublishGeometry(next);
                }

                _wakeSignal.WaitOne(50);
            }
        }

        private void RunFingerprints()
        {
            var token = _cts.Token;
            var clock = Stopwatch.StartNew();
            long nextScanMs = 0;
            int scannedVersion = -1;
            var titleElements = new List<Tuple<AutomationElement, string>>();
            while (!token.IsCancellationRequested)
            {
                ComposerDockSnapshot geometry;
                int version;
                lock (_snapshotGate)
                {
                    geometry = _latest;
                    version = _fingerprintVersion;
                }

                if (geometry.WindowHandle == _windowProvider() && geometry.Source != DockSourceKind.None)
                {
                    if (version != scannedVersion || clock.ElapsedMilliseconds >= nextScanMs || TitleChanged(titleElements))
                    {
                        var elapsed = Stopwatch.StartNew();
                        string[] headers, bodies;
                        CollectChatFingerprints(geometry.WindowHandle, geometry.WindowRect, geometry.ComposerRect,
                            geometry.DpiScale, titleElements, out headers, out bodies);
                        nextScanMs = clock.ElapsedMilliseconds + (_sessionBound ? 800 : 300);

                        lock (_snapshotGate)
                        {
                            if (_disposed || version != _fingerprintVersion || geometry.WindowHandle != _windowProvider()
                                || _latest.Source == DockSourceKind.None || _latest.WindowHandle != geometry.WindowHandle
                                || _latest.WindowRect.Size != geometry.WindowRect.Size || _latest.DpiScale != geometry.DpiScale)
                            {
                                scannedVersion = -1;
                                continue;
                            }
                            scannedVersion = version;
                            _latest = _latest.WithFingerprints(headers, bodies, elapsed.ElapsedMilliseconds);
                        }
                        NotifySnapshotUpdated();
                    }
                    long remainingMs = nextScanMs - clock.ElapsedMilliseconds;
                    _fingerprintSignal.WaitOne((int)Math.Max(0, Math.Min(300, remainingMs)));
                }
                else
                {
                    _fingerprintSignal.WaitOne(220);
                }
            }
        }

        private static bool TitleChanged(List<Tuple<AutomationElement, string>> titles)
        {
            foreach (var title in titles)
            {
                try
                {
                    if (!string.Equals(title.Item1.Current.Name, title.Item2, StringComparison.Ordinal))
                        return true;
                }
                catch (Exception ex)
                {
                    if (!IsRecoverableUiaError(ex)) throw;
                    return true;
                }
            }
            return false;
        }

        private static bool IsRecoverableUiaError(Exception ex)
        {
            // A disappearing Electron accessibility node can invalidate a live or cached read.
            return ex is ElementNotAvailableException || ex is COMException
                || ex is InvalidOperationException || ex is TimeoutException;
        }

        private static ComposerDockSnapshot Discover(
            IntPtr hwnd,
            ref AutomationElement composer,
            ref AutomationElement contextEl,
            ref AutomationElement modelEl)
        {
            Rectangle winRect = NativeWin32.GetWindowRectangle(hwnd);
            float scale = NativeWin32.GetWindowDpiScale(hwnd);
            var unavailable = new ComposerDockSnapshot
            {
                WindowHandle = hwnd, WindowRect = winRect, DpiScale = scale, Detail = "uia-unavailable"
            };
            if (winRect.IsEmpty) return unavailable;

            TreeWalker walker = TreeWalker.ControlViewWalker;
            Rectangle composerRect = Rectangle.Empty;
            if (composer == null || !TryReadRect(composer, out composerRect))
            {
                AutomationElement root = null;
                try { root = AutomationElement.FromHandle(hwnd); }
                catch (ElementNotAvailableException) { }
                catch (COMException) { }
                catch (ArgumentException) { }
                if (root == null)
                {
                    composer = contextEl = modelEl = null;
                    unavailable.Detail = "uia-root-unavailable";
                    return unavailable;
                }
                composer = FindComposer(root, walker);
                if (composer != null) TryReadRect(composer, out composerRect);
            }

            if (composerRect.IsEmpty)
            {
                string composerDetail = composer == null ? "uia-editor-not-found" : "uia-composer-no-bounds";
                composer = null;
                contextEl = null;
                modelEl = null;
                return new ComposerDockSnapshot
                {
                    WindowHandle = hwnd,
                    DpiScale = scale,
                    Source = DockSourceKind.None,
                    WindowRect = winRect,
                    Detail = composerDetail
                };
            }

            var children = ReadComposerChildren(composer, walker);

            // 1. Find Context usage indicator (Image child of composer)
            int contextIndex = -1;
            contextEl = null;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i].ControlType != "Image") continue;
                if (contextIndex < 0)
                {
                    contextIndex = i;
                    contextEl = children[i].Element;
                }
                if (LooksLikeContext(children[i].Name, children[i].ClassName))
                {
                    contextIndex = i;
                    contextEl = children[i].Element;
                    break;
                }
            }

            Rectangle contextRect = contextIndex >= 0 ? children[contextIndex].Rect : Rectangle.Empty;

            // 2. Find Model selector (expandable Button right of Context icon)
            modelEl = null;
            Rectangle modelRect = Rectangle.Empty;
            for (int i = 0; i < children.Count; i++)
            {
                AutomationElement btn;
                Rectangle btnRect;
                if (!TryExpandableButton(children[i], walker, out btn, out btnRect)) continue;
                if (!contextRect.IsEmpty && btnRect.Left < contextRect.Left) continue;
                // When context indicator wasn't found, model selector sits in the right half of the composer
                if (contextRect.IsEmpty && btnRect.Left < composerRect.Left + (composerRect.Width / 3)) continue;
                if (modelRect.IsEmpty || btnRect.Left < modelRect.Left)
                {
                    modelEl = btn;
                    modelRect = btnRect;
                }
            }

            // 3. Determine toolbar row vertical band and left-cluster barrier
            Rectangle rowRect = !modelRect.IsEmpty ? modelRect : contextRect;
            int rowTop = !rowRect.IsEmpty ? rowRect.Top : (composerRect.Bottom - OverlayDpi.Pixels(36, scale));
            int rowBottom = !rowRect.IsEmpty ? rowRect.Bottom : composerRect.Bottom;
            if (!contextRect.IsEmpty && !modelRect.IsEmpty)
            {
                rowTop = Math.Min(contextRect.Top, modelRect.Top);
                rowBottom = Math.Max(contextRect.Bottom, modelRect.Bottom);
            }

            int rightAnchorLeft = !contextRect.IsEmpty
                ? contextRect.Left
                : (!modelRect.IsEmpty ? modelRect.Left : (composerRect.Right - OverlayDpi.Pixels(220, scale)));

            int leftBarrier = composerRect.Left + OverlayDpi.Pixels(12, scale);
            for (int i = 0; i < children.Count; i++)
            {
                var c = children[i];
                if (contextIndex >= 0 && i >= contextIndex) continue;
                if (c.Rect.IsEmpty || c.Rect.Height <= 1) continue;
                if (c.ControlType != "Button" && c.ControlType != "Image") continue;
                bool overlapsRow = c.Rect.Bottom > rowTop && c.Rect.Top < rowBottom;
                if (!overlapsRow) continue;
                if (c.Rect.Right < rightAnchorLeft)
                    leftBarrier = Math.Max(leftBarrier, c.Rect.Right);
            }

            DockSourceKind source;
            Rectangle refRect;
            if (!contextRect.IsEmpty)
            {
                source = DockSourceKind.UiaContext;
                refRect = contextRect;
            }
            else if (!modelRect.IsEmpty)
            {
                source = DockSourceKind.UiaModel;
                refRect = modelRect;
            }
            else
            {
                source = DockSourceKind.UiaComposer;
                refRect = composerRect;
            }

            return new ComposerDockSnapshot
            {
                WindowHandle = hwnd,
                DpiScale = scale,
                Source = source,
                ReferenceRect = refRect,
                RowRect = !modelRect.IsEmpty ? modelRect : contextRect,
                ComposerRect = composerRect,
                WindowRect = winRect,
                LeftBarrierX = leftBarrier,
                Detail = string.Format("src={0} ref={1} row={2} comp={3} leftBarrier={4} children={5}",
                    source, refRect, rowRect, composerRect, leftBarrier, children.Count)
            };
        }

        private static void CollectChatFingerprints(
            IntPtr hwnd,
            Rectangle winRect,
            Rectangle composerRect,
            float scale,
            List<Tuple<AutomationElement, string>> titleElements,
            out string[] headerTexts,
            out string[] bodyTexts)
        {
            titleElements.Clear();
            headerTexts = new string[0];
            bodyTexts = new string[0];
            if (hwnd == IntPtr.Zero || winRect.IsEmpty || composerRect.IsEmpty) return;

            try
            {
                AutomationElement root = AutomationElement.FromHandle(hwnd);
                if (root == null) return;

                var cache = new CacheRequest();
                cache.Add(AutomationElement.NameProperty);
                cache.Add(AutomationElement.ControlTypeProperty);
                cache.Add(AutomationElement.BoundingRectangleProperty);
                cache.Add(AutomationElement.IsOffscreenProperty);
                cache.TreeScope = TreeScope.Element;

                var cond = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Header),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

                // Chat header sits in the top title bar (y <= winTop + 88), strictly above the sidebar thread list.
                // Estimate main pane left edge from composer symmetry so both sidebar-open and collapsed layouts work.
                int mainPaneLeft = Math.Max(winRect.Left, (composerRect.Left + composerRect.Right) - winRect.Right);
                int headerMinX = mainPaneLeft > winRect.Left + OverlayDpi.Pixels(140, scale)
                    ? Math.Max(winRect.Left + OverlayDpi.Pixels(180, scale), mainPaneLeft - OverlayDpi.Pixels(24, scale))
                    : winRect.Left + OverlayDpi.Pixels(60, scale);
                int headerMaxY = winRect.Top + OverlayDpi.Pixels(88, scale);
                int bodyMinX = composerRect.Left - OverlayDpi.Pixels(80, scale);
                int bodyMaxX = composerRect.Right + OverlayDpi.Pixels(80, scale);
                int bodyMinY = winRect.Top + OverlayDpi.Pixels(70, scale);
                int bodyMaxY = composerRect.Top - OverlayDpi.Pixels(6, scale);

                var headers = new List<Tuple<int, string, AutomationElement, string>>();
                var bodies = new List<Tuple<int, string>>();

                using (cache.Activate())
                {
                    var found = root.FindAll(TreeScope.Descendants, cond);
                    for (int i = 0; i < found.Count; i++)
                    {
                        try
                        {
                            var info = found[i].Cached;
                            if (info.IsOffscreen) continue;
                            string name = info.Name;
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            var b = info.BoundingRectangle;
                            if (b.IsEmpty || double.IsNaN(b.X) || double.IsInfinity(b.X) || b.Width <= 0 || b.Height <= 0)
                                continue;

                            int left = (int)Math.Round(b.X);
                            int top = (int)Math.Round(b.Y);
                            int right = left + (int)Math.Round(b.Width);
                            int bottom = top + (int)Math.Round(b.Height);
                            var ct = info.ControlType;

                            if (top >= winRect.Top && bottom <= headerMaxY && left >= headerMinX)
                            {
                                string trimmed = name.Trim();
                                if (trimmed.Length >= 3)
                                    headers.Add(Tuple.Create(left, trimmed, found[i], name));
                            }
                            else if ((ct == ControlType.Text || ct == ControlType.Header)
                                && top >= bodyMinY && bottom <= bodyMaxY && left >= bodyMinX && right <= bodyMaxX)
                            {
                                string trimmed = name.Trim();
                                if (trimmed.Length >= 12)
                                    bodies.Add(Tuple.Create(bottom, trimmed));
                            }
                        }
                        catch { }
                    }
                }

                headers.Sort((a, b) => a.Item1.CompareTo(b.Item1));
                var uniqHeaders = new List<string>();
                var seenH = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headers.Count && uniqHeaders.Count < 16; i++)
                {
                    if (seenH.Add(headers[i].Item2))
                    {
                        uniqHeaders.Add(headers[i].Item2);
                        // The first few header candidates suffice for inexpensive change detection.
                        if (titleElements.Count < 3)
                            titleElements.Add(Tuple.Create(headers[i].Item3, headers[i].Item4));
                    }
                }

                // Bottom-most visible messages right above the composer come first
                bodies.Sort(delegate(Tuple<int, string> a, Tuple<int, string> b) { return b.Item1.CompareTo(a.Item1); });
                var uniqBodies = new List<string>();
                var seenB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < bodies.Count && uniqBodies.Count < 24; i++)
                {
                    if (seenB.Add(bodies[i].Item2)) uniqBodies.Add(bodies[i].Item2);
                }

                headerTexts = uniqHeaders.ToArray();
                bodyTexts = uniqBodies.ToArray();
            }
            catch { }
        }

        private static bool LooksLikeContext(string name, string cls)
        {
            for (int i = 0; i < ContextNameHints.Length; i++)
            {
                if (name.IndexOf(ContextNameHints[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return cls.IndexOf(ContextClassHint, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static AutomationElement FindComposer(AutomationElement root, TreeWalker walker)
        {
            // Fast native UIA query: finds ProseMirror directly without walking thousands of chat nodes
            try
            {
                var cond = new PropertyCondition(AutomationElement.ClassNameProperty, ComposerEditorClass);
                AutomationElement editor = root.FindFirst(TreeScope.Descendants, cond);
                if (editor != null)
                {
                    AutomationElement parent = TryParent(walker, editor);
                    if (parent != null) return parent;
                }
            }
            catch { }

            // Fallback: locate the bottom-most Edit control inside the window
            try
            {
                var editCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                var edits = root.FindAll(TreeScope.Descendants, editCondition);
                AutomationElement bestEdit = null;
                int bestBottom = int.MinValue;
                for (int i = 0; i < edits.Count; i++)
                {
                    Rectangle rect;
                    if (TryReadRect(edits[i], out rect) && rect.Width >= 240 && rect.Bottom > bestBottom)
                    {
                        bestEdit = edits[i];
                        bestBottom = rect.Bottom;
                    }
                }
                if (bestEdit != null) return TryParent(walker, bestEdit);
            }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }
            catch (InvalidOperationException) { }

            return null;
        }

        private static List<ChildInfo> ReadComposerChildren(AutomationElement composer, TreeWalker walker)
        {
            var children = new List<ChildInfo>(16);
            var cache = new CacheRequest { TreeScope = TreeScope.Element | TreeScope.Children };
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.ClassNameProperty);
            cache.Add(AutomationElement.IsExpandCollapsePatternAvailableProperty);

            try
            {
                using (cache.Activate())
                {
                    var found = composer.FindAll(TreeScope.Children, Automation.ControlViewCondition);
                    for (int i = 0; i < found.Count && i < 64; i++)
                        AddComposerChild(children, found[i], true);
                }
                return children;
            }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }
            catch (InvalidOperationException) { }

            children.Clear();
            var child = TryFirstChild(walker, composer);
            for (int i = 0; child != null && i < 64; i++)
            {
                AddComposerChild(children, child, false);
                child = TryNextSibling(walker, child);
            }
            return children;
        }

        private static void AddComposerChild(List<ChildInfo> children, AutomationElement element, bool cached)
        {
            if (cached)
            {
                try
                {
                    var info = element.Cached;
                    Rectangle rect;
                    if (ConvertRect(info.BoundingRectangle, out rect))
                    {
                        children.Add(new ChildInfo
                        {
                            Element = element,
                            Rect = rect,
                            ControlType = ControlTypeName(info.ControlType),
                            Name = info.Name ?? string.Empty,
                            ClassName = info.ClassName ?? string.Empty,
                            HasCache = true
                        });
                    }
                    return;
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                catch (COMException) { }
            }
            Rectangle liveRect;
            string type, name, cls;
            if (TryReadRect(element, out liveRect) && TryReadProps(element, out type, out name, out cls))
                children.Add(new ChildInfo { Element = element, Rect = liveRect, ControlType = type, Name = name, ClassName = cls });
        }

        private static bool TryExpandableButton(ChildInfo info, TreeWalker walker, out AutomationElement btn, out Rectangle rect)
        {
            AutomationElement el = info.Element;
            if (info.HasCache)
            {
                try
                {
                    if (TryCachedExpandableButton(el, out rect))
                    {
                        btn = el;
                        return true;
                    }
                    var children = el.CachedChildren;
                    for (int i = 0; i < children.Count && i < 8; i++)
                    {
                        if (!TryCachedExpandableButton(children[i], out rect)) continue;
                        btn = children[i];
                        return true;
                    }
                    btn = null;
                    rect = Rectangle.Empty;
                    return false;
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                catch (COMException) { }
            }

            btn = el;
            rect = Rectangle.Empty;
            string ct, name, cls;
            if (TryReadProps(el, out ct, out name, out cls) && ct == "Button" && IsExpandable(el) && TryReadRect(el, out rect))
                return true;

            AutomationElement child = TryFirstChild(walker, el);
            int guard = 0;
            while (child != null && guard < 8)
            {
                if (TryReadProps(child, out ct, out name, out cls) && ct == "Button" && IsExpandable(child) && TryReadRect(child, out rect))
                {
                    btn = child;
                    return true;
                }
                child = TryNextSibling(walker, child);
                guard++;
            }
            return false;
        }

        private static bool TryCachedExpandableButton(AutomationElement el, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            return el.Cached.ControlType == ControlType.Button
                && object.Equals(el.GetCachedPropertyValue(AutomationElement.IsExpandCollapsePatternAvailableProperty), true)
                && ConvertRect(el.Cached.BoundingRectangle, out rect);
        }

        private static bool IsExpandable(AutomationElement el)
        {
            try
            {
                object pat;
                return el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pat);
            }
            catch { return false; }
        }

        private static AutomationElement TryFirstChild(TreeWalker w, AutomationElement e)
        {
            try { return w.GetFirstChild(e); } catch { return null; }
        }

        private static AutomationElement TryNextSibling(TreeWalker w, AutomationElement e)
        {
            try { return w.GetNextSibling(e); } catch { return null; }
        }

        private static AutomationElement TryParent(TreeWalker w, AutomationElement e)
        {
            try { return w.GetParent(e); } catch { return null; }
        }

        private static bool TryReadProps(AutomationElement e, out string controlType, out string name, out string className)
        {
            controlType = string.Empty;
            name = string.Empty;
            className = string.Empty;
            try
            {
                var cur = e.Current;
                controlType = ControlTypeName(cur.ControlType);
                name = cur.Name ?? string.Empty;
                className = cur.ClassName ?? string.Empty;
                return true;
            }
            catch { return false; }
        }

        private static bool TryReadRect(AutomationElement e, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            try
            {
                return ConvertRect(e.Current.BoundingRectangle, out rect);
            }
            catch { return false; }
        }

        private static string ControlTypeName(ControlType type)
        {
            return type == null ? string.Empty : type.ProgrammaticName.Replace("ControlType.", string.Empty);
        }

        private static bool ConvertRect(System.Windows.Rect bounds, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            if (bounds.IsEmpty || double.IsNaN(bounds.X) || double.IsInfinity(bounds.X) || bounds.Width <= 0 || bounds.Height <= 0)
                return false;
            if (bounds.X < -10000 || bounds.Y < -10000) return false;
            rect = new Rectangle(
                (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y),
                (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height));
            return !rect.IsEmpty;
        }
    }
}
