using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexInputUsage
{
    internal static class AutoStartManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "CodexInputUsageOverlay";

        internal static string GetExecutablePath()
        {
            return Process.GetCurrentProcess().MainModule.FileName;
        }

        internal static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    string raw = key.GetValue(ValueName) as string;
                    if (string.IsNullOrWhiteSpace(raw)) return false;
                    string trimmed = raw.Trim().Trim('"');
                    return string.Equals(trimmed, GetExecutablePath(), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        internal static bool SetEnabled(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return false;
                    if (enable)
                    {
                        key.SetValue(ValueName, "\"" + GetExecutablePath() + "\"", RegistryValueKind.String);
                    }
                    else
                    {
                        if (key.GetValue(ValueName) != null)
                            key.DeleteValue(ValueName, false);
                    }
                    return true;
                }
            }
            catch { return false; }
        }
    }

    internal static class Program
    {
        private static readonly object DiagLock = new object();
        private static string _diagLogPath;
        private static string _diagCapturePath;

        [STAThread]
        private static void Main(string[] args)
        {
            NativeWin32.SetProcessDpiAwarenessContext(new IntPtr(-4));
            InitDiagPaths();

            if (args.Length >= 1 && args[0] == "--self-test")
            {
                RunSelfTest(args.Length >= 2 ? args[1] : null);
                return;
            }

            if (args.Length == 2 && args[0] == "--probe")
            {
                IntPtr hwnd = NativeWin32.FindAnyCodexWindow();
                if (hwnd == IntPtr.Zero)
                {
                    File.WriteAllText(args[1], "codex-window-not-found");
                    return;
                }
                var dock = ComposerDockTracker.MeasureOnce(hwnd);
                File.WriteAllText(args[1], dock.Detail ?? dock.Source.ToString());
                return;
            }

            bool owns;
            using (var single = new Mutex(true, @"Local\CodexInputUsage.OverlayPrototype", out owns))
            {
                if (!owns)
                {
                    LogDiag("startup skipped: another instance holds mutex");
                    return;
                }

                ResetDiagFiles();
                LogStartup();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new OverlayController())
                {
                    Application.Run(app);
                }
            }
        }

        private static void RunSelfTest(string explicitRolloutPath)
        {
            var sb = new StringBuilder();
            CodexThemeKind theme = CodexThemeSource.Detect();
            sb.AppendLine("theme=" + theme);

            // 1. Unit-verify strict IPC routing rules (switch, disconnect, multi-window ambiguity)
            using (var mockIpc = new CodexIpcMonitor())
            {
                mockIpc.ApplyFollowingEventForTest("win-1", "local", "thread-aaa", true);
                var s1 = mockIpc.GetStatus();
                bool ok1 = s1.IsConnected && s1.ActiveThreadId == "thread-aaa" && !s1.IsAmbiguousMultiWindow;

                // Switch chat in same window -> immediately moves to thread-bbb
                mockIpc.ApplyFollowingEventForTest("win-1", "local", "thread-bbb", true);
                var s2 = mockIpc.GetStatus();
                bool ok2 = s2.ActiveThreadId == "thread-bbb" && !s2.IsAmbiguousMultiWindow;

                // Second window opens a different chat -> MUST mark ambiguous and clear active thread (never guess)
                mockIpc.ApplyFollowingEventForTest("win-2", "local", "thread-ccc", true);
                var s3 = mockIpc.GetStatus();
                bool ok3 = s3.ActiveThreadId == null && s3.IsAmbiguousMultiWindow && s3.DistinctThreadCount == 2;

                // Second window stops following -> unambiguous again
                mockIpc.ApplyFollowingEventForTest("win-2", "local", "thread-ccc", false);
                var s4 = mockIpc.GetStatus();
                bool ok4 = s4.ActiveThreadId == "thread-bbb" && !s4.IsAmbiguousMultiWindow;

                // IPC disconnect -> MUST clear active thread immediately (no SQLite recency fallback)
                mockIpc.SimulateDisconnectForTest();
                var s5 = mockIpc.GetStatus();
                bool ok5 = !s5.IsConnected && s5.ActiveThreadId == null && !s5.IsAmbiguousMultiWindow;

                // Verify tracker clears stale values when desiredThread becomes null
                var trackerCheck = new RolloutSessionTracker();
                trackerCheck.UpdateFromDisk(null, "IPC 断线", null);
                var clearedSnap = trackerCheck.BuildSnapshot();
                string[] clearedVariants = MetricFormatter.BuildStripVariants(clearedSnap);
                bool ok6 = !clearedSnap.HasUsage && clearedVariants[0].Contains("--");

                sb.AppendLine(string.Format("route_tests single={0} switch={1} multiWindowClear={2} multiWindowRecover={3} disconnectClear={4} trackerClear={5}",
                    ok1, ok2, ok3, ok4, ok5, ok6));
            }

            // 2. Test live Codex IPC pipe (2.2s window)
            string ipcThread = null;
            bool ipcConnected = false;
            bool ipcAmbiguous = false;
            using (var ipc = new CodexIpcMonitor())
            {
                ipc.Start();
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(2200);
                while (DateTime.UtcNow < deadline)
                {
                    var st = ipc.GetStatus();
                    ipcConnected = st.IsConnected;
                    ipcAmbiguous = st.IsAmbiguousMultiWindow;
                    if (!string.IsNullOrWhiteSpace(st.ActiveThreadId) || st.IsAmbiguousMultiWindow)
                    {
                        ipcThread = st.ActiveThreadId;
                        break;
                    }
                    Thread.Sleep(80);
                }
                var finalSt = ipc.GetStatus();
                sb.AppendLine(string.Format("ipc connected={0} activeThread={1} windows={2} distinct={3} ambiguous={4} err={5}",
                    finalSt.IsConnected, finalSt.ActiveThreadId ?? "(none)", finalSt.WindowCount,
                    finalSt.DistinctThreadCount, finalSt.IsAmbiguousMultiWindow, finalSt.LastError ?? ""));
            }

            // 3. Exact ID lookup in state_5.sqlite (strict: only for verified IPC thread)
            StateThreadRecord dbRec = !string.IsNullOrWhiteSpace(ipcThread)
                ? CodexStateDb.TryReadThread(ipcThread)
                : null;
            if (dbRec != null)
            {
                sb.AppendLine(string.Format("sqlite id={0} tokens_used={1} rollout={2}",
                    dbRec.Id, dbRec.TokensUsed, dbRec.RolloutPath ?? "(null)"));
            }
            else
            {
                sb.AppendLine("sqlite=(no-verified-ipc-thread)");
            }

            // 4. Parse rollout JSONL for the verified IPC thread (or explicit path)
            string targetRollout = explicitRolloutPath;
            if (string.IsNullOrWhiteSpace(targetRollout) && !string.IsNullOrWhiteSpace(ipcThread))
                targetRollout = CodexStateDb.FindRolloutFileByThreadId(ipcThread);

            string bindSource = !ipcConnected ? "IPC 断线" : (ipcAmbiguous ? "多窗口歧义" : (!string.IsNullOrWhiteSpace(ipcThread) ? "IPC" : "等待"));
            var tracker = new RolloutSessionTracker();
            tracker.UpdateFromDisk(ipcThread, bindSource, targetRollout);
            var snap = tracker.BuildSnapshot();
            sb.AppendLine(string.Format("rollout path={0}", targetRollout ?? "(none)"));
            sb.AppendLine(string.Format("metrics hasUsage={0} latestTurn={1} total={2} input={3} cached={4} output={5} cachePct={6} tps={7} ttft={8} ctx={9}/{10} turns={11}",
                snap.HasUsage,
                snap.LatestTurn != null ? snap.LatestTurn.TotalTokens : 0,
                snap.TotalTokens, snap.InputTokens, snap.CachedInputTokens, snap.OutputTokens,
                MetricFormatter.FormatCachePrecise(snap.SessionCacheHitPct),
                MetricFormatter.FormatTps(snap.DisplayTps, snap.TpsIsHeld),
                MetricFormatter.FormatSeconds(snap.LatestTtftMs),
                snap.ContextUsedTokens, snap.ContextWindowTokens, snap.Turns.Count));

            string[] variants = MetricFormatter.BuildStripVariants(snap);
            for (int i = 0; i < variants.Length; i++)
                sb.AppendLine("strip" + i + "=" + variants[i]);

            // 5. Render visual preview PNGs into work/ for inspection
            string workDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\work"));
            Directory.CreateDirectory(workDir);
            using (var bmp = StripForm.RenderPreviewBitmap(variants[0], ThemePalette.Light, false, true))
            {
                bmp.Save(Path.Combine(workDir, "preview-strip-light.png"), ImageFormat.Png);
                sb.AppendLine("strip0_px=" + bmp.Width + "x" + bmp.Height);
            }
            using (var bmp = StripForm.RenderPreviewBitmap(variants[1], ThemePalette.Light, false, true))
            {
                bmp.Save(Path.Combine(workDir, "preview-strip-compact-light.png"), ImageFormat.Png);
                sb.AppendLine("strip1_px=" + bmp.Width + "x" + bmp.Height);
            }
            using (var bmp = StripForm.RenderPreviewBitmap(variants[0], ThemePalette.Dark, true, true))
                bmp.Save(Path.Combine(workDir, "preview-strip-dark-hover.png"), ImageFormat.Png);
            using (var bmp = DetailPopoverForm.RenderPreviewBitmap(snap, ThemePalette.Light))
                bmp.Save(Path.Combine(workDir, "preview-detail-light.png"), ImageFormat.Png);
            using (var bmp = DetailPopoverForm.RenderPreviewBitmap(snap, ThemePalette.Dark))
                bmp.Save(Path.Combine(workDir, "preview-detail-dark.png"), ImageFormat.Png);

            sb.AppendLine("autostart_enabled=" + AutoStartManager.IsEnabled());
            File.WriteAllText(Path.Combine(workDir, "selftest-report.txt"), sb.ToString(), Encoding.UTF8);
        }

        internal static void InitDiagPaths()
        {
            try
            {
                string workDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\work"));
                Directory.CreateDirectory(workDir);
                _diagLogPath = Path.Combine(workDir, "overlay-diag.log");
                _diagCapturePath = Path.Combine(workDir, "overlay-diag-capture.png");
            }
            catch { }
        }

        internal static void ResetDiagFiles()
        {
            try
            {
                if (!string.IsNullOrEmpty(_diagLogPath)) File.WriteAllText(_diagLogPath, string.Empty, Encoding.UTF8);
                if (!string.IsNullOrEmpty(_diagCapturePath) && File.Exists(_diagCapturePath)) File.Delete(_diagCapturePath);
            }
            catch { }
        }

        internal static void LogDiag(string msg)
        {
            if (string.IsNullOrEmpty(_diagLogPath)) return;
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine;
            lock (DiagLock)
            {
                try { File.AppendAllText(_diagLogPath, line, Encoding.UTF8); }
                catch { }
            }
        }

        internal static void CaptureRegionOnce(Rectangle area)
        {
            if (string.IsNullOrEmpty(_diagCapturePath)) return;
            try
            {
                var screen = Screen.FromPoint(area.Location).Bounds;
                var crop = Rectangle.Intersect(screen, Rectangle.Inflate(area, 40, 28));
                if (crop.Width <= 0 || crop.Height <= 0) return;
                using (var bmp = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(crop.Location, Point.Empty, crop.Size, CopyPixelOperation.SourceCopy);
                    bmp.Save(_diagCapturePath, ImageFormat.Png);
                }
                LogDiag("captured docked strip region to " + _diagCapturePath + " crop=" + crop);
            }
            catch (Exception ex)
            {
                LogDiag("capture failed: " + ex.Message);
            }
        }

        private static void LogStartup()
        {
            uint dpi = 0;
            try { dpi = NativeWin32.GetDpiForSystem(); } catch { }
            string screens = string.Join("; ", Screen.AllScreens.Select(s =>
                string.Format("{0}(Bounds={1} Work={2})", s.DeviceName, s.Bounds, s.WorkingArea)).ToArray());
            LogDiag(string.Format("startup pid={0} desktop={1} dpi={2} theme={3} autostart={4} screens=[{5}]",
                Process.GetCurrentProcess().Id, NativeWin32.GetDesktopName(), dpi,
                CodexThemeSource.Current, AutoStartManager.IsEnabled(), screens));
        }

        private static Icon CreateTrayIcon()
        {
            using (var bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var bg = new SolidBrush(Color.FromArgb(255, 37, 99, 235)))
                        g.FillEllipse(bg, 1, 1, 14, 14);
                    using (var fg = new SolidBrush(Color.White))
                    {
                        var bolt = new PointF[]
                        {
                            new PointF(9.2f, 2.8f),
                            new PointF(4.8f, 8.6f),
                            new PointF(7.8f, 8.6f),
                            new PointF(6.8f, 13.2f),
                            new PointF(11.2f, 7.4f),
                            new PointF(8.2f, 7.4f)
                        };
                        g.FillPolygon(fg, bolt);
                    }
                }
                IntPtr hIcon = bmp.GetHicon();
                return Icon.FromHandle(hIcon);
            }
        }

        private sealed class OverlayController : ApplicationContext
        {
            private const int ActivePollMs = 160;
            private const int StandbyPollMs = 1500;

            private readonly CodexIpcMonitor _ipc = new CodexIpcMonitor();
            private readonly RolloutSessionTracker _rollout = new RolloutSessionTracker();
            private readonly ComposerDockTracker _dockTracker;
            private readonly StripForm _strip = new StripForm();
            private readonly DetailPopoverForm _popover = new DetailPopoverForm();
            private readonly NotifyIcon _trayIcon;
            private readonly ToolStripMenuItem _trayStatusItem;
            private readonly ToolStripMenuItem _trayAutoStartItem;
            private readonly System.Windows.Forms.Timer _uiTimer;

            private volatile IntPtr _activeCodexHwnd = IntPtr.Zero;
            private SessionMetricsSnapshot _lastMetrics = new SessionMetricsSnapshot();
            private Rectangle _lastStripBounds = Rectangle.Empty;
            private DateTime _lastMetricsPollUtc = DateTime.MinValue;
            private DateTime _lastCodexPresenceCheckUtc = DateTime.MinValue;
            private DateTime _lastHeartbeatUtc = DateTime.MinValue;
            private string _lastDiagState = string.Empty;
            private bool _codexRunning;
            private bool _capturedOnce;
            private bool _popoverWanted;

            internal OverlayController()
            {
                _dockTracker = new ComposerDockTracker(delegate { return _activeCodexHwnd; });
                _strip.StripClicked += OnStripClicked;
                _strip.AutoStartToggled += OnAutoStartToggled;
                _strip.ExitRequested += delegate { ExitThread(); };
                _popover.VisibleChanged += delegate
                {
                    if (!_popover.Visible)
                    {
                        _popoverWanted = false;
                        _strip.SetPopoverOpen(false);
                    }
                };

                var trayMenu = new ContextMenuStrip();
                _trayStatusItem = new ToolStripMenuItem("状态：等待原生 Codex 启动") { Enabled = false };
                _trayAutoStartItem = new ToolStripMenuItem("开机自启（跟随原生 Codex）") { CheckOnClick = false };
                _trayAutoStartItem.Click += OnAutoStartToggled;
                trayMenu.Items.Add(_trayStatusItem);
                trayMenu.Items.Add(new ToolStripSeparator());
                trayMenu.Items.Add(_trayAutoStartItem);
                trayMenu.Items.Add("退出 Codex InputUsage", null, delegate { ExitThread(); });

                _trayIcon = new NotifyIcon
                {
                    Icon = CreateTrayIcon(),
                    Text = "Codex InputUsage — 等待原生 Codex",
                    ContextMenuStrip = trayMenu,
                    Visible = true
                };

                SyncAutoStartMenuState();
                _dockTracker.Start();

                _uiTimer = new System.Windows.Forms.Timer { Interval = ActivePollMs };
                _uiTimer.Tick += OnTick;
                _uiTimer.Start();
            }

            private void SyncAutoStartMenuState()
            {
                bool enabled = AutoStartManager.IsEnabled();
                _strip.SetAutoStartChecked(enabled);
                _trayAutoStartItem.Checked = enabled;
            }

            private void OnAutoStartToggled(object sender, EventArgs e)
            {
                bool target = !AutoStartManager.IsEnabled();
                AutoStartManager.SetEnabled(target);
                SyncAutoStartMenuState();
                Program.LogDiag("autostart toggled -> " + AutoStartManager.IsEnabled());
            }

            private void OnStripClicked(object sender, EventArgs e)
            {
                _popoverWanted = !_popoverWanted;
                _strip.SetPopoverOpen(_popoverWanted);
                if (_popoverWanted && !_lastStripBounds.IsEmpty)
                {
                    ThemePalette pal = ThemePalette.For(CodexThemeSource.Current);
                    _popover.UpdateData(_lastMetrics, pal, _lastStripBounds);
                    if (!_popover.Visible) _popover.Show();
                    _popover.PositionAbove(_lastStripBounds);
                }
                else if (_popover.Visible)
                {
                    _popover.Hide();
                }
            }

            private void EnterStandby()
            {
                _codexRunning = false;
                _activeCodexHwnd = IntPtr.Zero;
                if (_ipc.IsRunning) _ipc.Stop();
                _rollout.Reset(null, "等待", null);
                _lastMetrics = _rollout.BuildSnapshot();
                _strip.HideStrip();
                if (_popover.Visible) _popover.Hide();
                _popoverWanted = false;
                _strip.SetPopoverOpen(false);
                _uiTimer.Interval = StandbyPollMs;
                _trayStatusItem.Text = "状态：待机（等待原生 Codex 启动）";
                _trayIcon.Text = "Codex InputUsage — 待机";
                LogState("standby:codex-not-running");
            }

            private void WakeForCodex()
            {
                _codexRunning = true;
                if (!_ipc.IsRunning) _ipc.Start();
                _uiTimer.Interval = ActivePollMs;
                _trayStatusItem.Text = "状态：已随原生 Codex 激活";
                _trayIcon.Text = "Codex InputUsage — 运行中";
                LogState("active:codex-detected");
            }

            private void OnTick(object sender, EventArgs e)
            {
                DateTime now = DateTime.UtcNow;

                // 1. Check whether official Codex Desktop is running (cheap standby gate)
                if (!_codexRunning || (now - _lastCodexPresenceCheckUtc).TotalSeconds >= 2.0)
                {
                    _lastCodexPresenceCheckUtc = now;
                    bool running = NativeWin32.HasRunningCodexProcess();
                    if (!running)
                    {
                        if (_codexRunning || _uiTimer.Interval != StandbyPollMs) EnterStandby();
                        return;
                    }
                    if (!_codexRunning) WakeForCodex();
                }

                // 2. Foreground official Codex check
                string nonMatch;
                IntPtr fgCodex = NativeWin32.GetForegroundCodexWindow(out nonMatch);
                if (fgCodex == IntPtr.Zero)
                {
                    IntPtr fg = NativeWin32.GetForegroundWindow();
                    bool isOwnWindow = (_strip.IsHandleCreated && fg == _strip.Handle)
                        || (_popover.IsHandleCreated && fg == _popover.Handle);
                    if (isOwnWindow && _activeCodexHwnd != IntPtr.Zero)
                        fgCodex = _activeCodexHwnd;
                }

                _activeCodexHwnd = fgCodex;

                if (fgCodex == IntPtr.Zero)
                {
                    LogState("hidden:" + (nonMatch ?? "not-codex"));
                    _strip.HideStrip();
                    if (_popover.Visible) _popover.Hide();
                    _popoverWanted = false;
                    _strip.SetPopoverOpen(false);
                    return;
                }

                // 3. Strict zero-guess session binding (IPC only; clear immediately on disconnect or multi-window ambiguity)
                var ipcSt = _ipc.GetStatus();
                string desiredThread;
                string source;

                if (!ipcSt.IsConnected)
                {
                    desiredThread = null;
                    source = "IPC 断线";
                }
                else if (ipcSt.IsAmbiguousMultiWindow)
                {
                    desiredThread = null;
                    source = "多窗口歧义";
                }
                else if (!string.IsNullOrWhiteSpace(ipcSt.ActiveThreadId))
                {
                    desiredThread = ipcSt.ActiveThreadId;
                    source = "IPC";
                }
                else
                {
                    desiredThread = null;
                    source = "等待";
                }

                bool threadChanged = !string.Equals(_rollout.CurrentThreadId, desiredThread, StringComparison.OrdinalIgnoreCase);
                bool sourceChanged = !string.Equals(_lastMetrics.BindingSource, source, StringComparison.Ordinal);
                if (threadChanged || sourceChanged || (now - _lastMetricsPollUtc).TotalMilliseconds >= 450.0)
                {
                    _lastMetricsPollUtc = now;
                    string rolloutPath = !string.IsNullOrWhiteSpace(desiredThread)
                        ? CodexStateDb.FindRolloutFileByThreadId(desiredThread)
                        : null;
                    if (_rollout.UpdateFromDisk(desiredThread, source, rolloutPath) || threadChanged || sourceChanged)
                    {
                        _lastMetrics = _rollout.BuildSnapshot();
                    }
                }

                // 4. Dock into Codex composer toolbar
                var dock = _dockTracker.Latest;
                if (dock == null || dock.WindowHandle != fgCodex || dock.Source == DockSourceKind.None)
                {
                    LogState("dock-pending:" + (dock != null ? dock.Detail : "null"));
                    _strip.HideStrip();
                    if (_popover.Visible) _popover.Hide();
                    return;
                }

                ThemePalette pal = ThemePalette.For(CodexThemeSource.Current);
                Rectangle placed;
                string variant;
                bool shown = _strip.ApplyDockedState(dock, _lastMetrics, pal, out placed, out variant);
                if (!shown)
                {
                    LogState("dock-no-space:" + dock.Detail);
                    if (_popover.Visible) _popover.Hide();
                    return;
                }

                _lastStripBounds = placed;
                LogState(string.Format("docked:{0} bounds={1} text=\"{2}\" thread={3}({4}) | {5}",
                    dock.Source, placed, variant, MetricFormatter.ShortThreadId(_lastMetrics.ThreadId),
                    _lastMetrics.BindingSource, dock.Detail));

                if (_popoverWanted)
                {
                    _popover.UpdateData(_lastMetrics, pal, placed);
                    if (!_popover.Visible) _popover.Show();
                }

                if (!_capturedOnce && _strip.IsHandleCreated && NativeWin32.IsWindowVisible(_strip.Handle))
                {
                    _capturedOnce = true;
                    Program.CaptureRegionOnce(placed);
                }
            }

            private void LogState(string state)
            {
                var now = DateTime.UtcNow;
                if (state != _lastDiagState || (now - _lastHeartbeatUtc).TotalSeconds >= 8.0)
                {
                    _lastDiagState = state;
                    _lastHeartbeatUtc = now;
                    Program.LogDiag(state);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _uiTimer.Stop();
                    _uiTimer.Dispose();
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    _dockTracker.Dispose();
                    _ipc.Dispose();
                    _popover.Dispose();
                    _strip.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
