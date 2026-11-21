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
        internal DockSourceKind Source;
        internal Rectangle ReferenceRect;
        internal Rectangle RowRect;
        internal Rectangle ComposerRect;
        internal Rectangle WindowRect;
        internal int LeftBarrierX;
        internal string[] HeaderTexts = new string[0];
        internal string[] BodyTexts = new string[0];
        internal string Detail;

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
                return ComposerRect.Bottom - 22;
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
            internal int Index;
            internal Rectangle Rect;
            internal string ControlType;
            internal string Name;
            internal string ClassName;
        }

        private readonly Func<IntPtr> _windowProvider;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly AutoResetEvent _wakeSignal = new AutoResetEvent(false);
        private Thread _thread;
        private volatile ComposerDockSnapshot _latest = ComposerDockSnapshot.Empty;
        private int _forceFullWalkFlag;
        private int _forceFingerprintFlag;

        internal event EventHandler SnapshotUpdated;

        internal ComposerDockTracker(Func<IntPtr> windowProvider)
        {
            _windowProvider = windowProvider;
        }

        internal ComposerDockSnapshot Latest { get { return _latest; } }

        internal void WakeNow(bool forceFullWalk, bool forceFingerprint)
        {
            if (forceFullWalk) Interlocked.Exchange(ref _forceFullWalkFlag, 1);
            if (forceFingerprint) Interlocked.Exchange(ref _forceFingerprintFlag, 1);
            try { _wakeSignal.Set(); } catch { }
        }

        internal void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "CodexInputUsage.ComposerDock"
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _wakeSignal.Set(); } catch { }
            try { _cts.Dispose(); } catch { }
            try { _wakeSignal.Dispose(); } catch { }
        }

        internal static ComposerDockSnapshot MeasureOnce(IntPtr hwnd)
        {
            AutomationElement composer = null;
            AutomationElement contextEl = null;
            AutomationElement modelEl = null;
            return Discover(hwnd, ref composer, ref contextEl, ref modelEl);
        }

        private void RunLoop()
        {
            AutomationElement composer = null;
            AutomationElement contextEl = null;
            AutomationElement modelEl = null;
            IntPtr trackedHwnd = IntPtr.Zero;
            Rectangle lastRefRect = Rectangle.Empty;
            DateTime lastFullWalkUtc = DateTime.MinValue;
            DateTime lastFingerprintUtc = DateTime.MinValue;
            CancellationToken token = _cts.Token;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    IntPtr hwnd = _windowProvider();
                    if (hwnd == IntPtr.Zero)
                    {
                        // Keep cached _latest so Alt-Tab back to the same Codex window renders on frame 1!
                        _wakeSignal.WaitOne(220);
                        continue;
                    }

                    bool requestedFullWalk = Interlocked.Exchange(ref _forceFullWalkFlag, 0) != 0;
                    bool requestedFp = Interlocked.Exchange(ref _forceFingerprintFlag, 0) != 0;

                    bool needFullWalk = requestedFullWalk
                        || hwnd != trackedHwnd
                        || composer == null
                        || (contextEl == null && modelEl == null)
                        || (DateTime.UtcNow - lastFullWalkUtc).TotalSeconds >= 12.0;

                    if (!needFullWalk)
                    {
                        // Fast path: 1 cross-process UIA bounding rect read!
                        AutomationElement fastEl = contextEl ?? modelEl;
                        Rectangle curRect;
                        if (TryReadRect(fastEl, out curRect))
                        {
                            if (curRect != lastRefRect)
                            {
                                needFullWalk = true;
                            }
                            else
                            {
                                var curSnap = _latest;
                                if (curSnap != null && curSnap.WindowHandle == hwnd)
                                {
                                    Rectangle winRect = NativeWin32.GetWindowRectangle(hwnd);
                                    if (winRect != curSnap.WindowRect)
                                    {
                                        needFullWalk = true;
                                    }
                                    else if (requestedFp || (DateTime.UtcNow - lastFingerprintUtc).TotalMilliseconds >= 300.0)
                                    {
                                        string[] hdrs, bodies;
                                        CollectChatFingerprints(hwnd, winRect, curSnap.ComposerRect, out hdrs, out bodies);
                                        lastFingerprintUtc = DateTime.UtcNow;
                                        _latest = new ComposerDockSnapshot
                                        {
                                            WindowHandle = curSnap.WindowHandle,
                                            Source = curSnap.Source,
                                            ReferenceRect = curSnap.ReferenceRect,
                                            RowRect = curSnap.RowRect,
                                            ComposerRect = curSnap.ComposerRect,
                                            WindowRect = curSnap.WindowRect,
                                            LeftBarrierX = curSnap.LeftBarrierX,
                                            HeaderTexts = hdrs,
                                            BodyTexts = bodies,
                                            Detail = curSnap.Detail
                                        };
                                        if (SnapshotUpdated != null) SnapshotUpdated(this, EventArgs.Empty);
                                    }
                                }
                            }
                        }
                        else
                        {
                            needFullWalk = true;
                        }
                    }

                    if (needFullWalk)
                    {
                        trackedHwnd = hwnd;
                        var prevSnap = _latest;
                        var snap = Discover(hwnd, ref composer, ref contextEl, ref modelEl);

                        // Guard against single-frame React relayout glitches (e.g. children=0 or brief unmount)
                        // when the window handle & geometry are unchanged: hold the previous valid anchor briefly.
                        bool degraded = (snap.Source == DockSourceKind.None || snap.Source == DockSourceKind.UiaComposer)
                            && prevSnap != null
                            && prevSnap.WindowHandle == hwnd
                            && (prevSnap.Source == DockSourceKind.UiaContext || prevSnap.Source == DockSourceKind.UiaModel)
                            && prevSnap.WindowRect == snap.WindowRect
                            && (DateTime.UtcNow - lastFullWalkUtc).TotalMilliseconds < 1200.0;

                        if (!degraded)
                        {
                            if (!snap.ComposerRect.IsEmpty && !snap.WindowRect.IsEmpty)
                            {
                                string[] hdrs, bodies;
                                CollectChatFingerprints(hwnd, snap.WindowRect, snap.ComposerRect, out hdrs, out bodies);
                                snap.HeaderTexts = hdrs;
                                snap.BodyTexts = bodies;
                                lastFingerprintUtc = DateTime.UtcNow;
                            }
                            _latest = snap;
                            lastRefRect = snap.ReferenceRect;
                            if (snap.Source == DockSourceKind.UiaContext || snap.Source == DockSourceKind.UiaModel)
                                lastFullWalkUtc = DateTime.UtcNow;
                            if (SnapshotUpdated != null) SnapshotUpdated(this, EventArgs.Empty);
                        }
                    }
                }
                catch
                {
                    composer = null;
                    contextEl = null;
                    modelEl = null;
                }

                try { _wakeSignal.WaitOne(140); }
                catch { break; }
            }
        }

        private static ComposerDockSnapshot Discover(
            IntPtr hwnd,
            ref AutomationElement composer,
            ref AutomationElement contextEl,
            ref AutomationElement modelEl)
        {
            Rectangle winRect = NativeWin32.GetWindowRectangle(hwnd);
            if (winRect.IsEmpty) return ComposerDockSnapshot.Empty;

            AutomationElement root = null;
            try { root = AutomationElement.FromHandle(hwnd); } catch { }
            if (root == null) return ComposerDockSnapshot.Empty;

            TreeWalker walker = TreeWalker.ControlViewWalker;
            Rectangle dummy;
            if (composer != null && !TryReadRect(composer, out dummy))
                composer = null;

            if (composer == null)
                composer = FindComposer(root, walker);

            Rectangle composerRect;
            if (composer == null || !TryReadRect(composer, out composerRect))
            {
                composer = null;
                contextEl = null;
                modelEl = null;
                return new ComposerDockSnapshot
                {
                    WindowHandle = hwnd,
                    Source = DockSourceKind.None,
                    WindowRect = winRect,
                    Detail = "no-prosemirror-composer"
                };
            }

            var children = new List<ChildInfo>(16);
            AutomationElement ch = TryFirstChild(walker, composer);
            int idx = 0;
            while (ch != null && idx < 64)
            {
                Rectangle r;
                string ct, name, cls;
                if (TryReadRect(ch, out r) && TryReadProps(ch, out ct, out name, out cls))
                {
                    children.Add(new ChildInfo
                    {
                        Element = ch,
                        Index = idx,
                        Rect = r,
                        ControlType = ct,
                        Name = name,
                        ClassName = cls
                    });
                }
                ch = TryNextSibling(walker, ch);
                idx++;
            }

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
                if (!TryExpandableButton(children[i].Element, walker, out btn, out btnRect)) continue;
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
            int rowTop = !rowRect.IsEmpty ? rowRect.Top : (composerRect.Bottom - 36);
            int rowBottom = !rowRect.IsEmpty ? rowRect.Bottom : composerRect.Bottom;
            if (!contextRect.IsEmpty && !modelRect.IsEmpty)
            {
                rowTop = Math.Min(contextRect.Top, modelRect.Top);
                rowBottom = Math.Max(contextRect.Bottom, modelRect.Bottom);
            }

            int rightAnchorLeft = !contextRect.IsEmpty
                ? contextRect.Left
                : (!modelRect.IsEmpty ? modelRect.Left : (composerRect.Right - 220));

            int leftBarrier = composerRect.Left + 12;
            for (int i = 0; i < children.Count; i++)
            {
                var c = children[i];
                if (contextIndex >= 0 && c.Index >= contextIndex) continue;
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
            out string[] headerTexts,
            out string[] bodyTexts)
        {
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
                int headerMinX = mainPaneLeft > winRect.Left + 140
                    ? Math.Max(winRect.Left + 180, mainPaneLeft - 24)
                    : (winRect.Left + 60);
                int headerMaxY = winRect.Top + 88;
                int bodyMinX = composerRect.Left - 80;
                int bodyMaxX = composerRect.Right + 80;
                int bodyMinY = winRect.Top + 70;
                int bodyMaxY = composerRect.Top - 6;

                var headers = new List<Tuple<int, string>>();
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
                                    headers.Add(Tuple.Create(left, trimmed));
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

                headers.Sort(delegate(Tuple<int, string> a, Tuple<int, string> b) { return a.Item1.CompareTo(b.Item1); });
                var uniqHeaders = new List<string>();
                var seenH = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headers.Count && uniqHeaders.Count < 16; i++)
                {
                    if (seenH.Add(headers[i].Item2)) uniqHeaders.Add(headers[i].Item2);
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
                var editCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
                var edits = root.FindAll(TreeScope.Descendants, editCond);
                AutomationElement bestEdit = null;
                int bestBottom = int.MinValue;
                for (int i = 0; i < edits.Count; i++)
                {
                    Rectangle r;
                    if (TryReadRect(edits[i], out r) && r.Width >= 240 && r.Bottom > bestBottom)
                    {
                        bestBottom = r.Bottom;
                        bestEdit = edits[i];
                    }
                }
                if (bestEdit != null) return TryParent(walker, bestEdit);
            }
            catch { }

            return null;
        }

        private static bool TryExpandableButton(AutomationElement el, TreeWalker walker, out AutomationElement btn, out Rectangle rect)
        {
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
                controlType = cur.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty);
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
                var b = e.Current.BoundingRectangle;
                if (b.IsEmpty || double.IsNaN(b.X) || double.IsInfinity(b.X) || b.Width <= 0 || b.Height <= 0)
                    return false;
                if (b.X < -10000 || b.Y < -10000) return false;
                rect = new Rectangle(
                    (int)Math.Round(b.X),
                    (int)Math.Round(b.Y),
                    (int)Math.Round(b.Width),
                    (int)Math.Round(b.Height));
                return !rect.IsEmpty;
            }
            catch { return false; }
        }
    }
}
