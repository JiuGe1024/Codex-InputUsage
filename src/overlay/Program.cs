using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexInputUsage
{
    internal static class CodexCompanionManager
    {
        private const string LegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string LegacyValueName = "CodexInputUsageOverlay";
        private static readonly Regex SectionRegex = new Regex(
            @"(?ms)\r?\n?\[mcp_servers\.codex_input_usage\]\r?\n(?:(?!\[).*(?:\r?\n|$))*",
            RegexOptions.Compiled);

        private static string GetConfigPath()
        {
            return Path.Combine(CodexThemeSource.GetCodexHome(), "config.toml");
        }

        private static string GetCompanionScriptPath()
        {
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\scripts\codex-mcp-companion.mjs"));
        }

        private static string FindNodeExecutable()
        {
            string[] candidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"nodejs\node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"nodejs\node.exe")
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i])) return candidates[i];
            }
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] dirs = pathEnv.Split(';');
            for (int i = 0; i < dirs.Length; i++)
            {
                string d = dirs[i].Trim();
                if (d.Length == 0) continue;
                try
                {
                    string p = Path.Combine(d, "node.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return "node.exe";
        }

        private static void CleanupLegacyRunKey()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, true))
                {
                    if (key != null && key.GetValue(LegacyValueName) != null)
                        key.DeleteValue(LegacyValueName, false);
                }
            }
            catch { }
        }

        internal static bool IsEnabled()
        {
            try
            {
                string cfg = GetConfigPath();
                if (!File.Exists(cfg)) return false;
                string text = File.ReadAllText(cfg, Encoding.UTF8);
                return SectionRegex.IsMatch(text);
            }
            catch { return false; }
        }

        internal static bool SetEnabled(bool enable)
        {
            CleanupLegacyRunKey();
            try
            {
                string cfg = GetConfigPath();
                if (!File.Exists(cfg)) return false;
                string content = File.ReadAllText(cfg, Encoding.UTF8);
                string cleaned = SectionRegex.Replace(content, string.Empty).TrimEnd();
                string next;
                if (enable)
                {
                    string node = FindNodeExecutable();
                    string script = GetCompanionScriptPath();
                    next = cleaned
                        + "\r\n\r\n[mcp_servers.codex_input_usage]\r\n"
                        + "command = '" + node + "'\r\n"
                        + "args = ['" + script + "']\r\n";
                }
                else
                {
                    next = cleaned + "\r\n";
                }
                File.WriteAllText(cfg, next, new UTF8Encoding(false));
                return true;
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

            bool exitWithCodex = args.Any(a => string.Equals(a, "--exit-with-codex", StringComparison.OrdinalIgnoreCase));

            bool owns;
            using (var single = new Mutex(true, @"Local\CodexInputUsage.OverlayPrototype", out owns))
            {
                if (!owns)
                {
                    LogDiag("startup skipped: another instance holds mutex");
                    return;
                }

                ResetDiagFiles();
                LogStartup(exitWithCodex);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new OverlayController(exitWithCodex))
                {
                    Application.Run(app);
                }
            }
        }

        private static void RunSelfTest(string explicitRolloutPath)
        {
            var sb = new StringBuilder();
            CodexThemeKind theme = CodexThemeSource.Detect();
            sb.AppendLine("theme=" + theme + " codexRunning=" + NativeWin32.HasRunningCodexProcess());

            // 1. Unit-verify multi-followed IPC pool + UIA disambiguation (strict no-guess)
            var matcher = new SessionMatcher();
            using (var mockIpc = new CodexIpcMonitor())
            {
                // Same Codex window follows 3 warm chats concurrently (exact real Codex behavior!)
                mockIpc.ApplyFollowingEventForTest("win-1", "local", "thread-a", true);
                mockIpc.ApplyFollowingEventForTest("win-1", "local", "thread-b", true);
                mockIpc.ApplyFollowingEventForTest("win-1", "local", "thread-c", true);
                var stPool = mockIpc.GetStatus();
                bool okPool = stPool.IsConnected && stPool.FollowedThreadIds.Length == 3;

                var cands = new List<StateThreadRecord>
                {
                    new StateThreadRecord { Id = "thread-a", Name = "阅读工作区交接文档", Title = "阅读一下工作区内的交接文档" },
                    new StateThreadRecord { Id = "thread-b", Name = "修复反重力代理访问", Title = "反重力代理连接" },
                    new StateThreadRecord { Id = "thread-c", Name = "阅读工作区交接文档", Title = "另一个同名交接" }
                };
                var corpus = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "thread-a", "这是第一段 68.6M 会话的独有正文内容，用于精确区分同名对话。" },
                    { "thread-b", "这是反重力代理配置相关的回复文本。" },
                    { "thread-c", "这是第三个同名会话完全不同的正文内容。" }
                };

                // Unique header match -> thread-b
                var mHeader = matcher.ResolveFromCandidatesForTest(cands, corpus, new string[] { "修复反重力代理访问" }, new string[0]);
                bool okHeader = mHeader.ThreadId == "thread-b" && mHeader.Source == "IPC+标题";

                // Duplicate title -> breaks tie using visible body text -> thread-a
                var mBody = matcher.ResolveFromCandidatesForTest(cands, corpus,
                    new string[] { "阅读工作区交接文档" },
                    new string[] { "这是第一段 68.6M 会话的独有正文内容" });
                bool okBody = mBody.ThreadId == "thread-a" && mBody.Source == "IPC+正文";

                // Multiple warm threads with NO matching header or body -> MUST refuse to guess (null)
                var mAmbig = matcher.ResolveFromCandidatesForTest(cands, corpus, new string[] { "无关按钮" }, new string[0]);
                bool okAmbig = mAmbig.ThreadId == null && mAmbig.Source == "待确认";

                // IPC disconnect without foreground evidence clears the existing binding.
                mockIpc.SimulateDisconnectForTest();
                var mDisc = matcher.Resolve(mockIpc.GetStatus(), new string[0], new string[0]);
                bool okDisc = mDisc.ThreadId == null && mDisc.Source == "IPC 断线";

                // Local matching (used when Codex 26.1117+ removes \\.\pipe\codex-ipc):
                // 1) truncated title prefix match, 2) body match beyond top-12, 3) single long unique snippet, 4) refuse ambiguous
                var mLocHdr = matcher.ResolveLocalFromCandidatesForTest(cands, corpus, new string[] { "修复反重力代理…" }, new string[0]);
                bool okLocHdr = mLocHdr.ThreadId == "thread-b" && mLocHdr.Source == "本地+标题";

                var mLocSingle = matcher.ResolveLocalFromCandidatesForTest(cands, corpus, new string[0],
                    new string[] { "这是第一段 68.6M 会话的独有正文内容，用于精确区分同名对话。" });
                bool okLocSingle = mLocSingle.ThreadId == "thread-a" && mLocSingle.Source == "本地+正文";

                var mLocAmbig = matcher.ResolveLocalFromCandidatesForTest(cands, corpus, new string[] { "阅读工作区交接文档" }, new string[0]);
                bool okLocAmbig = mLocAmbig.ThreadId == null && mLocAmbig.Source == "待确认";

                sb.AppendLine(string.Format("match_tests pool={0} header={1} bodyTieBreak={2} refuseAmbig={3} disconnectClear={4} localHdr={5} localSingle={6} localRefuseAmbig={7}",
                    okPool, okHeader, okBody, okAmbig, okDisc, okLocHdr, okLocSingle, okLocAmbig));
            }

            // 1b. Verify state_5.sqlite subagent filtering (Guardian review must never crowd out real threads)
            var realThreads = CodexStateDb.TryReadRecentThreads(0);
            int guardianCount = realThreads.Count(t => string.Equals(t.Name, "Guardian review", StringComparison.OrdinalIgnoreCase));
            int existingRolloutCount = realThreads.Count(t => !string.IsNullOrWhiteSpace(t.RolloutPath) && File.Exists(t.RolloutPath));
            sb.AppendLine(string.Format("db_real_threads total={0} guardianLeaked={1} withRollout={2}",
                realThreads.Count, guardianCount, existingRolloutCount));

            // 2. Test live Codex IPC pipe (1.2s window — optional on Codex 26.1117+)
            IpcRouteStatus liveIpc;
            using (var ipc = new CodexIpcMonitor())
            {
                ipc.Start();
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(1200);
                while (DateTime.UtcNow < deadline)
                {
                    var st = ipc.GetStatus();
                    if (st.IsConnected && st.FollowedThreadIds.Length > 0)
                    {
                        Thread.Sleep(250); // allow full initial replay burst to arrive
                        break;
                    }
                    Thread.Sleep(80);
                }
                liveIpc = ipc.GetStatus();
            }
            sb.AppendLine(string.Format("ipc connected={0} followedCount={1} followed=[{2}] err={3}",
                liveIpc.IsConnected,
                liveIpc.FollowedThreadIds.Length,
                string.Join(",", liveIpc.FollowedThreadIds.Select(id => MetricFormatter.ShortThreadId(id)).ToArray()),
                liveIpc.LastError ?? ""));

            // 3. Cross-check a specific thread (prefer 01a1b9da if present, else first followed or first real thread with rollout)
            string testThread = null;
            for (int i = 0; i < liveIpc.FollowedThreadIds.Length; i++)
            {
                if (liveIpc.FollowedThreadIds[i].StartsWith("01a1b9da", StringComparison.OrdinalIgnoreCase))
                {
                    testThread = liveIpc.FollowedThreadIds[i];
                    break;
                }
            }
            if (testThread == null && liveIpc.FollowedThreadIds.Length > 0)
                testThread = liveIpc.FollowedThreadIds[0];
            if (testThread == null)
            {
                for (int i = 0; i < realThreads.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(realThreads[i].RolloutPath) && File.Exists(realThreads[i].RolloutPath))
                    {
                        testThread = realThreads[i].Id;
                        break;
                    }
                }
            }

            string targetRollout = explicitRolloutPath;
            string effectiveThread = string.IsNullOrWhiteSpace(explicitRolloutPath) ? testThread : null;
            if (string.IsNullOrWhiteSpace(targetRollout) && !string.IsNullOrWhiteSpace(testThread))
                targetRollout = CodexStateDb.FindRolloutFileByThreadId(testThread);

            var tracker = new RolloutSessionTracker();
            tracker.UpdateFromDisk(effectiveThread, liveIpc.IsConnected ? "IPC+标题" : "本地+标题", targetRollout);
            var snap = tracker.BuildSnapshot();
            var quotaReader = new AccountQuotaReader();
            var liveQuota = quotaReader.GetLatestQuota(true);
            AccountQuotaReader.ApplyToSnapshot(snap, liveQuota);
            var metaRec = !string.IsNullOrWhiteSpace(testThread) ? CodexStateDb.TryReadThread(testThread) : null;
            if (metaRec != null)
            {
                snap.ModelName = metaRec.Model;
                snap.ReasoningEffort = metaRec.ReasoningEffort;
            }
            sb.AppendLine(string.Format("check_thread={0} model={1}({2}) rollout={3}",
                effectiveThread ?? "(explicit)", snap.ModelName ?? "-", snap.ReasoningEffort ?? "-", targetRollout ?? "(none)"));
            sb.AppendLine(string.Format("metrics hasUsage={0} latestTurn={1} turnCache={2} total={3} input={4} cached={5} output={6} cachePct={7} tps={8} avgTps={9} ttft={10} ctx={11}/{12} (cached={13} uncached={14} rem={15}) turns={16}",
                snap.HasUsage,
                snap.LatestTurn != null ? snap.LatestTurn.TotalTokens : 0,
                snap.LatestTurn != null ? MetricFormatter.FormatCacheTwoDecimals(snap.LatestTurn.CacheHitPct) : "--",
                snap.TotalTokens, snap.InputTokens, snap.CachedInputTokens, snap.OutputTokens,
                MetricFormatter.FormatCacheTwoDecimals(snap.SessionCacheHitPct),
                MetricFormatter.FormatTps(snap.DisplayTps, snap.TpsIsHeld),
                snap.SessionAvgTps.HasValue ? snap.SessionAvgTps.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "--",
                MetricFormatter.FormatSeconds(snap.LatestTtftMs),
                snap.ContextUsedTokens, snap.ContextWindowTokens,
                snap.ContextCachedTokens, snap.ContextUncachedInputTokens, snap.ContextRemainingTokens,
                snap.Turns.Count));
            sb.AppendLine(string.Format("quota live={0} 5hUsed={1} ({2}) weekUsed={3} ({4}) src={5}",
                snap.QuotaIsAccountLive,
                snap.PrimaryUsedPct, MetricFormatter.FormatResetCountdown(snap.PrimaryResetsAt, false),
                snap.SecondaryUsedPct, MetricFormatter.FormatResetCountdown(snap.SecondaryResetsAt, true),
                liveQuota != null ? Path.GetFileName(liveQuota.SourcePath) : "(none)"));

            var variants = StripRenderModel.BuildVariants(snap);
            for (int i = 0; i < variants.Length; i++)
                sb.AppendLine("strip" + i + "=" + variants[i].Describe());

            if (snap.ContextChannels != null && snap.ContextChannels.Count > 0)
            {
                long chSum = 0;
                var chParts = new List<string>();
                for (int i = 0; i < snap.ContextChannels.Count; i++)
                {
                    chSum += snap.ContextChannels[i].Tokens;
                    chParts.Add(snap.ContextChannels[i].Label + "=" + snap.ContextChannels[i].Tokens);
                }
                sb.AppendLine("ctx_channels sum=" + chSum + " (official=" + snap.ContextUsedTokens + ") | " + string.Join(", ", chParts.ToArray()));
            }

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
            using (var bmp = StripForm.RenderPreviewBitmap(variants[0], ThemePalette.Dark, false, true))
                bmp.Save(Path.Combine(workDir, "preview-strip-dark.png"), ImageFormat.Png);
            using (var bmp = DetailPopoverForm.RenderPreviewBitmap(snap, ThemePalette.Light))
                bmp.Save(Path.Combine(workDir, "preview-detail-light.png"), ImageFormat.Png);
            using (var bmp = DetailPopoverForm.RenderPreviewBitmap(snap, ThemePalette.Dark))
                bmp.Save(Path.Combine(workDir, "preview-detail-dark.png"), ImageFormat.Png);

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

        private static void LogStartup(bool exitWithCodex)
        {
            uint dpi = 0;
            try { dpi = NativeWin32.GetDpiForSystem(); } catch { }
            string screens = string.Join("; ", Screen.AllScreens.Select(s =>
                string.Format("{0}(Bounds={1} Work={2})", s.DeviceName, s.Bounds, s.WorkingArea)).ToArray());
            LogDiag(string.Format("startup pid={0} desktop={1} dpi={2} theme={3} codexCompanion={4} exitWithCodex={5} screens=[{6}]",
                Process.GetCurrentProcess().Id, NativeWin32.GetDesktopName(), dpi,
                CodexThemeSource.Current, CodexCompanionManager.IsEnabled(), exitWithCodex, screens));
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
            private const int ActivePollMs = 250;
            private const int StandbyPollMs = 1500;

            private readonly SynchronizationContext _uiContext;
            private readonly HostWindowEvents _hostEvents = new HostWindowEvents();
            private readonly CodexIpcMonitor _ipc = new CodexIpcMonitor();
            private readonly SessionMatcher _matcher = new SessionMatcher();
            private readonly AccountQuotaReader _quotaReader = new AccountQuotaReader();
            private readonly RolloutSessionTracker _rollout = new RolloutSessionTracker();
            private readonly ComposerDockTracker _dockTracker;
            private readonly StripForm _strip = new StripForm();
            private readonly DetailPopoverForm _popover = new DetailPopoverForm();
            private readonly NotifyIcon _trayIcon;
            private readonly ToolStripMenuItem _trayStatusItem;
            private readonly ToolStripMenuItem _trayAutoStartItem;
            private readonly System.Windows.Forms.Timer _uiTimer;

            private FileSystemWatcher _rolloutWatcher;
            private string _watchedRolloutPath = string.Empty;
            private int _pendingAsyncRefresh;
            private int _rolloutDirtyFlag;

            private volatile IntPtr _activeCodexHwnd = IntPtr.Zero;
            private SessionMetricsSnapshot _lastMetrics = new SessionMetricsSnapshot();
            private Rectangle _lastStripBounds = Rectangle.Empty;
            private Rectangle _anchorWindowRect = Rectangle.Empty;
            private Rectangle _anchorStripBounds = Rectangle.Empty;
            private DateTime _lastMetricsPollUtc = DateTime.MinValue;
            private DateTime _lastCodexPresenceCheckUtc = DateTime.MinValue;
            private DateTime _lastHeartbeatUtc = DateTime.MinValue;
            private string _lastDiagState = string.Empty;
            private string _lastMatchDetail = string.Empty;
            private string _lastFingerprintSig = string.Empty;
            private readonly bool _exitWithCodex;
            private readonly DateTime _startedUtc = DateTime.UtcNow;
            private DateTime _codexMissingSinceUtc = DateTime.MinValue;
            private bool _everSawCodex;
            private bool _codexRunning;
            private bool _capturedOnce;
            private bool _popoverWanted;

            internal OverlayController(bool exitWithCodex)
            {
                _exitWithCodex = exitWithCodex;
                _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
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

                // Event-driven Win32 hooks for <1ms foreground switch & 60fps glued window drag!
                _hostEvents.ForegroundChanged += OnForegroundChanged;
                _hostEvents.TrackedWindowMoved += OnTrackedWindowMoved;
                _hostEvents.TrackedWindowMoveSizeEnded += OnTrackedWindowMoveSizeEnded;
                _hostEvents.TrackedWindowMinimized += OnTrackedWindowMinimized;

                // Wake UI immediately when background UIA or IPC finishes an update
                _dockTracker.SnapshotUpdated += delegate { PostUiRefresh(false); };
                _ipc.RouteUpdated += delegate
                {
                    _dockTracker.WakeNow(false, true);
                    PostUiRefresh(true);
                };

                var trayMenu = new ContextMenuStrip();
                _trayStatusItem = new ToolStripMenuItem("状态：等待原生 Codex 启动") { Enabled = false };
                _trayAutoStartItem = new ToolStripMenuItem("跟随原生 Codex 自动启动") { CheckOnClick = false };
                _trayAutoStartItem.Click += OnAutoStartToggled;
                trayMenu.Items.Add(_trayStatusItem);
                trayMenu.Items.Add(new ToolStripSeparator());
                trayMenu.Items.Add(_trayAutoStartItem);
                trayMenu.Items.Add("退出 Codex InputUsage", null, delegate { ExitThread(); });

                _trayIcon = new NotifyIcon
                {
                    Icon = CreateTrayIcon(),
                    Text = "Codex InputUsage — 跟随原生 Codex",
                    ContextMenuStrip = trayMenu,
                    Visible = true
                };

                SyncAutoStartMenuState();
                _dockTracker.Start();

                _uiTimer = new System.Windows.Forms.Timer { Interval = ActivePollMs };
                _uiTimer.Tick += OnTick;
                _uiTimer.Start();
            }

            private void PostUiRefresh(bool markRolloutDirty)
            {
                if (markRolloutDirty) Interlocked.Exchange(ref _rolloutDirtyFlag, 1);
                if (Interlocked.Exchange(ref _pendingAsyncRefresh, 1) != 0) return;
                try
                {
                    _uiContext.Post(delegate
                    {
                        Interlocked.Exchange(ref _pendingAsyncRefresh, 0);
                        if (!_strip.IsDisposed) SyncNow(false);
                    }, null);
                }
                catch
                {
                    Interlocked.Exchange(ref _pendingAsyncRefresh, 0);
                }
            }

            private void OnForegroundChanged(object sender, EventArgs e)
            {
                // Instant (<1ms) hide when switching away or frame-1 restore when switching back to Codex!
                SyncNow(true);
            }

            private void OnTrackedWindowMinimized(object sender, EventArgs e)
            {
                _strip.HideStrip();
                if (_popover.Visible) _popover.Hide();
                _popoverWanted = false;
                _strip.SetPopoverOpen(false);
            }

            private void OnTrackedWindowMoved(object sender, EventArgs e)
            {
                IntPtr hwnd = _activeCodexHwnd;
                if (hwnd == IntPtr.Zero || !_strip.Visible || _anchorWindowRect.IsEmpty || _anchorStripBounds.IsEmpty)
                    return;

                Rectangle curWin = NativeWin32.GetWindowRectangle(hwnd);
                if (curWin.IsEmpty) return;

                if (curWin.Size == _anchorWindowRect.Size)
                {
                    // Pure window drag: translate strip pixel-for-pixel at 60+ FPS with zero UIA/repaint latency!
                    int dx = curWin.X - _anchorWindowRect.X;
                    int dy = curWin.Y - _anchorWindowRect.Y;
                    int nextX = _anchorStripBounds.X + dx;
                    int nextY = _anchorStripBounds.Y + dy;
                    _lastStripBounds = _strip.MoveFast(nextX, nextY);
                    if (_popoverWanted && _popover.Visible)
                        _popover.PositionAbove(_lastStripBounds);
                }
                else
                {
                    // Window resize/maximize: predict bottom-anchored horizontal shift immediately,
                    // then wake UIA tracker to settle exact coordinates.
                    int dy = curWin.Bottom - _anchorWindowRect.Bottom;
                    int dx = (curWin.Width - _anchorWindowRect.Width) / 2 + (curWin.X - _anchorWindowRect.X);
                    int nextX = _anchorStripBounds.X + dx;
                    int nextY = _anchorStripBounds.Y + dy;
                    _lastStripBounds = _strip.MoveFast(nextX, nextY);
                    if (_popoverWanted && _popover.Visible)
                        _popover.PositionAbove(_lastStripBounds);
                    _dockTracker.WakeNow(true, false);
                }
            }

            private void OnTrackedWindowMoveSizeEnded(object sender, EventArgs e)
            {
                _dockTracker.WakeNow(true, true);
            }

            private void WatchRolloutFile(string rolloutPath)
            {
                string norm = rolloutPath ?? string.Empty;
                if (string.Equals(_watchedRolloutPath, norm, StringComparison.OrdinalIgnoreCase)) return;
                _watchedRolloutPath = norm;

                if (_rolloutWatcher != null)
                {
                    try { _rolloutWatcher.EnableRaisingEvents = false; _rolloutWatcher.Dispose(); } catch { }
                    _rolloutWatcher = null;
                }

                if (string.IsNullOrWhiteSpace(norm)) return;
                try
                {
                    string dir = Path.GetDirectoryName(norm);
                    string file = Path.GetFileName(norm);
                    if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(file) || !Directory.Exists(dir))
                        return;
                    var w = new FileSystemWatcher(dir, file)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                    };
                    w.Changed += delegate { PostUiRefresh(true); };
                    w.EnableRaisingEvents = true;
                    _rolloutWatcher = w;
                }
                catch { }
            }

            private void SyncAutoStartMenuState()
            {
                bool enabled = CodexCompanionManager.IsEnabled();
                _strip.SetAutoStartChecked(enabled);
                _trayAutoStartItem.Checked = enabled;
            }

            private void OnAutoStartToggled(object sender, EventArgs e)
            {
                bool target = !CodexCompanionManager.IsEnabled();
                CodexCompanionManager.SetEnabled(target);
                SyncAutoStartMenuState();
                Program.LogDiag("codex companion auto-launch toggled -> " + CodexCompanionManager.IsEnabled());
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
                _hostEvents.SetTrackedWindow(IntPtr.Zero);
                WatchRolloutFile(null);
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
                SyncNow(false);
            }

            private void SyncNow(bool fromForegroundEvent)
            {
                DateTime now = DateTime.UtcNow;

                if (!_codexRunning || (now - _lastCodexPresenceCheckUtc).TotalSeconds >= 2.0)
                {
                    _lastCodexPresenceCheckUtc = now;
                    bool running = NativeWin32.HasRunningCodexProcess();
                    if (!running)
                    {
                        if (_exitWithCodex)
                        {
                            if (_codexMissingSinceUtc == DateTime.MinValue) _codexMissingSinceUtc = now;
                            bool graceDone = _everSawCodex
                                ? (now - _codexMissingSinceUtc).TotalSeconds >= 3.0
                                : (now - _startedUtc).TotalSeconds >= 15.0;
                            if (graceDone)
                            {
                                Program.LogDiag("codex exited -> closing overlay (--exit-with-codex)");
                                ExitThread();
                                return;
                            }
                        }
                        if (_codexRunning || _uiTimer.Interval != StandbyPollMs) EnterStandby();
                        return;
                    }
                    _everSawCodex = true;
                    _codexMissingSinceUtc = DateTime.MinValue;
                    if (!_codexRunning) WakeForCodex();
                }

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

                bool gainedFocus = fgCodex != IntPtr.Zero && _activeCodexHwnd != fgCodex;
                _activeCodexHwnd = fgCodex;
                _hostEvents.SetTrackedWindow(fgCodex);

                if (fgCodex == IntPtr.Zero)
                {
                    LogState("hidden:" + (nonMatch ?? "not-codex"));
                    _strip.HideStrip();
                    if (_popover.Visible) _popover.Hide();
                    _popoverWanted = false;
                    _strip.SetPopoverOpen(false);
                    return;
                }

                if (gainedFocus || fromForegroundEvent)
                {
                    _dockTracker.WakeNow(false, true);
                }

                var dock = _dockTracker.Latest;
                if (dock == null || dock.WindowHandle != fgCodex || dock.Source == DockSourceKind.None)
                {
                    LogState("dock-pending:" + (dock != null ? dock.Detail : "null"));
                    _strip.HideStrip();
                    if (_popover.Visible) _popover.Hide();
                    return;
                }

                // Adjust cached dock snapshot instantly if Codex was moved while in the background
                Rectangle liveWinRect = NativeWin32.GetWindowRectangle(fgCodex);
                ComposerDockSnapshot effectiveDock = dock;
                if (!liveWinRect.IsEmpty && !dock.WindowRect.IsEmpty && liveWinRect != dock.WindowRect)
                {
                    if (liveWinRect.Size == dock.WindowRect.Size)
                    {
                        int dx = liveWinRect.X - dock.WindowRect.X;
                        int dy = liveWinRect.Y - dock.WindowRect.Y;
                        effectiveDock = new ComposerDockSnapshot
                        {
                            WindowHandle = dock.WindowHandle,
                            Source = dock.Source,
                            ReferenceRect = OffsetRect(dock.ReferenceRect, dx, dy),
                            RowRect = OffsetRect(dock.RowRect, dx, dy),
                            ComposerRect = OffsetRect(dock.ComposerRect, dx, dy),
                            WindowRect = liveWinRect,
                            LeftBarrierX = dock.LeftBarrierX + dx,
                            HeaderTexts = dock.HeaderTexts,
                            BodyTexts = dock.BodyTexts,
                            Detail = dock.Detail
                        };
                    }
                    else
                    {
                        _dockTracker.WakeNow(true, false);
                    }
                }

                var ipcSt = _ipc.GetStatus();
                string fpSig = ipcSt.Version + "|" + string.Join("\u001f", effectiveDock.HeaderTexts ?? new string[0])
                    + "|" + (effectiveDock.BodyTexts != null && effectiveDock.BodyTexts.Length > 0 ? effectiveDock.BodyTexts[0] : string.Empty);
                bool fpChanged = !string.Equals(fpSig, _lastFingerprintSig, StringComparison.Ordinal);
                bool rolloutDirty = Interlocked.Exchange(ref _rolloutDirtyFlag, 0) != 0;

                if (fpChanged || rolloutDirty || (now - _lastMetricsPollUtc).TotalMilliseconds >= 450.0)
                {
                    _lastFingerprintSig = fpSig;
                    _lastMetricsPollUtc = now;
                    SessionMatchResult matched = _matcher.Resolve(ipcSt, effectiveDock.HeaderTexts, effectiveDock.BodyTexts);
                    _lastMatchDetail = matched.Detail ?? matched.Source;
                    WatchRolloutFile(matched.RolloutPath);

                    bool threadChanged = !string.Equals(_rollout.CurrentThreadId, matched.ThreadId, StringComparison.OrdinalIgnoreCase);
                    bool sourceChanged = !string.Equals(_lastMetrics.BindingSource, matched.Source, StringComparison.Ordinal);
                    bool rolloutUpdated = _rollout.UpdateFromDisk(matched.ThreadId, matched.Source, matched.RolloutPath);
                    var liveQuota = _quotaReader.GetLatestQuota(rolloutUpdated || threadChanged);
                    if (rolloutUpdated || threadChanged || sourceChanged || _popoverWanted)
                    {
                        var nextMetrics = _rollout.BuildSnapshot();
                        nextMetrics.ModelName = matched.ModelName;
                        nextMetrics.ReasoningEffort = matched.ReasoningEffort;
                        AccountQuotaReader.ApplyToSnapshot(nextMetrics, liveQuota);
                        _lastMetrics = nextMetrics;
                    }
                }

                ThemePalette pal = ThemePalette.For(CodexThemeSource.Current);
                Rectangle placed;
                string variant;
                bool shown = _strip.ApplyDockedState(effectiveDock, _lastMetrics, pal, out placed, out variant);
                if (!shown)
                {
                    LogState("dock-no-space:" + effectiveDock.Detail);
                    if (_popover.Visible) _popover.Hide();
                    return;
                }

                _lastStripBounds = placed;
                _anchorWindowRect = effectiveDock.WindowRect;
                _anchorStripBounds = placed;

                LogState(string.Format("docked:{0} bounds={1} text=\"{2}\" thread={3}({4}) match=[{5}]",
                    effectiveDock.Source, placed, variant, MetricFormatter.ShortThreadId(_lastMetrics.ThreadId),
                    _lastMetrics.BindingSource, _lastMatchDetail));

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

            private static Rectangle OffsetRect(Rectangle r, int dx, int dy)
            {
                return r.IsEmpty ? r : new Rectangle(r.X + dx, r.Y + dy, r.Width, r.Height);
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
                    WatchRolloutFile(null);
                    _hostEvents.Dispose();
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
