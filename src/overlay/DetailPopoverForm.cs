using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexInputUsage
{
    internal enum PopoverTab
    {
        Session = 0,
        History = 1,
        Turns = 2
    }

    internal sealed class DetailPopoverForm : Form
    {
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;

        private const int WhMouseLl = 14;
        private const int WhKeyboardLl = 13;
        private const int WmLButtonDown = 0x0201;
        private const int WmRButtonDown = 0x0204;
        private const int WmKeyDown = 0x0100;
        private const int VkEscape = 0x1B;

        private const int PopoverWidth = 516;
        private const int Pad = 14;
        private const int HeaderHeight = 38;
        private const int TabBarHeight = 34;
        private const int CardGridHeight = 138;
        private const int QuotaSectionHeight = 72;
        private const int ContextBarHeight = 172;
        private const int TableHeaderHeight = 44;
        private const int RowHeight = 24;
        private const int InspectorHeight = 28;
        private const int SessionVisibleRows = 5;
        private const int DetailVisibleRows = 16;

        private const int HistSummaryHeight = 70;
        private const int HistChartHeight = 162;
        private const int HistTopThreadsHeight = 154;

        private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct MsllHookStruct
        {
            internal NativeWin32.NativePoint Pt;
            internal uint MouseData, Flags, Time;
            internal IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdllHookStruct
        {
            internal uint VkCode, ScanCode, Flags, Time;
            internal IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        private ThemePalette _palette = ThemePalette.Light;
        private SessionMetricsSnapshot _snapshot = new SessionMetricsSnapshot();
        private HistoryStatsSnapshot _history = new HistoryStatsSnapshot();
        private PopoverTab _activeTab = PopoverTab.Session;
        private Rectangle _anchorStrip = Rectangle.Empty;
        private Rectangle _closeButtonRect = Rectangle.Empty;
        private Rectangle _pinButtonRect = Rectangle.Empty;
        private Rectangle _tabSessionRect = Rectangle.Empty;
        private Rectangle _tabHistoryRect = Rectangle.Empty;
        private Rectangle _tabTurnsRect = Rectangle.Empty;
        private Rectangle _tableRowsRect = Rectangle.Empty;
        private Rectangle _scrollTrackRect = Rectangle.Empty;
        private Rectangle _scrollThumbRect = Rectangle.Empty;
        private Rectangle[] _dayBarRects = new Rectangle[0];

        private bool _closeHovered;
        private bool _pinHovered;
        private int _hoveredTab = -1;
        private bool _isPinned;
        private int _scrollOffset;
        private int _visibleRows = SessionVisibleRows;
        private int _hoveredRowIndex = -1;
        private int _selectedTurnIndex = -1;
        private int _hoveredDayIndex = -1;
        private int _selectedDayIndex = -1;
        private bool _draggingScroll;
        private int _dragStartY;
        private int _dragStartOffset;

        private readonly LowLevelProc _mouseProc;
        private readonly LowLevelProc _keyProc;
        private IntPtr _mouseHook = IntPtr.Zero;
        private IntPtr _keyHook = IntPtr.Zero;

        internal event EventHandler HistoryTabActivated;

        internal bool IsPinned { get { return _isPinned; } }
        internal PopoverTab ActiveTab { get { return _activeTab; } }

        internal DetailPopoverForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            _mouseProc = OnLowLevelMouse;
            _keyProc = OnLowLevelKey;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExToolWindow | WsExNoActivate;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmMouseActivate)
            {
                m.Result = (IntPtr)MaNoActivate;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            SyncDismissHooks();
        }

        protected override void Dispose(bool disposing)
        {
            UninstallDismissHooks();
            base.Dispose(disposing);
        }

        private void SyncDismissHooks()
        {
            if (Visible && !_isPinned && !IsDisposed)
                InstallDismissHooks();
            else
                UninstallDismissHooks();
        }

        private void InstallDismissHooks()
        {
            if (_mouseHook == IntPtr.Zero)
                _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, IntPtr.Zero, 0);
            if (_keyHook == IntPtr.Zero)
                _keyHook = SetWindowsHookEx(WhKeyboardLl, _keyProc, IntPtr.Zero, 0);
        }

        private void UninstallDismissHooks()
        {
            if (_mouseHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_mouseHook); } catch { }
                _mouseHook = IntPtr.Zero;
            }
            if (_keyHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_keyHook); } catch { }
                _keyHook = IntPtr.Zero;
            }
        }

        private IntPtr OnLowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Visible && !_isPinned && !_draggingScroll)
            {
                int msg = wParam.ToInt32();
                if (msg == WmLButtonDown || msg == WmRButtonDown)
                {
                    var info = (MsllHookStruct)Marshal.PtrToStructure(lParam, typeof(MsllHookStruct));
                    var pt = new Point(info.Pt.X, info.Pt.Y);
                    if (!Bounds.Contains(pt) && !_anchorStrip.Contains(pt))
                    {
                        try { BeginInvoke((Action)delegate { if (Visible && !_isPinned) Hide(); }); } catch { }
                    }
                }
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private IntPtr OnLowLevelKey(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Visible && !_isPinned && wParam.ToInt32() == WmKeyDown)
            {
                var info = (KbdllHookStruct)Marshal.PtrToStructure(lParam, typeof(KbdllHookStruct));
                if (info.VkCode == VkEscape)
                {
                    try { BeginInvoke((Action)delegate { if (Visible && !_isPinned) Hide(); }); } catch { }
                }
            }
            return CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        internal void UpdateData(SessionMetricsSnapshot snapshot, ThemePalette palette, Rectangle anchorStrip)
        {
            if (!string.Equals(_snapshot.ThreadId, snapshot == null ? null : snapshot.ThreadId, StringComparison.OrdinalIgnoreCase))
            {
                _scrollOffset = 0;
                _selectedTurnIndex = -1;
                _hoveredRowIndex = -1;
            }
            _snapshot = snapshot ?? new SessionMetricsSnapshot();
            _palette = palette ?? ThemePalette.Light;
            _anchorStrip = anchorStrip;

            PositionAbove(anchorStrip);
            Invalidate();
        }

        internal void UpdateHistoryData(HistoryStatsSnapshot history)
        {
            _history = history ?? new HistoryStatsSnapshot();
            if (_activeTab == PopoverTab.History && Visible)
                Invalidate();
        }

        internal void PositionAbove(Rectangle anchor)
        {
            if (anchor.IsEmpty) return;
            _anchorStrip = anchor;

            var work = Screen.FromPoint(anchor.Location).WorkingArea;
            int maxHeight = Math.Max(320, anchor.Top - work.Top - 10);
            int height;

            if (_activeTab == PopoverTab.History)
            {
                int modelH = ComputeHistoryModelHeight(_history);
                int desiredHistory = HeaderHeight + TabBarHeight + HistSummaryHeight + HistChartHeight + modelH + HistTopThreadsHeight + 12;
                height = Math.Min(desiredHistory, maxHeight);
            }
            else
            {
                int rows = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
                bool detail = _activeTab == PopoverTab.Turns;
                int rowLimit = detail ? DetailVisibleRows : SessionVisibleRows;
                int targetRows = Math.Max(1, Math.Min(rowLimit, rows));
                int chromeHeight = TurnTableChromeHeight(_activeTab);
                height = Math.Min(chromeHeight + targetRows * RowHeight, maxHeight);
                _visibleRows = Math.Max(1, Math.Min(targetRows, (height - chromeHeight) / RowHeight));
                _scrollOffset = detail ? Math.Min(_scrollOffset, Math.Max(0, rows - _visibleRows)) : 0;
            }

            int x = anchor.Right - PopoverWidth;
            if (x < work.Left + 8) x = work.Left + 8;
            if (x + PopoverWidth > work.Right - 8) x = work.Right - PopoverWidth - 8;
            int y = anchor.Top - height - 8;
            if (y < work.Top + 8) y = work.Top + 8;

            Bounds = new Rectangle(x, y, PopoverWidth, height);
            if (IsHandleCreated && Visible)
            {
                NativeWin32.SetWindowPos(
                    Handle, NativeWin32.HWND_TOPMOST,
                    x, y, PopoverWidth, height,
                    NativeWin32.SWP_NOACTIVATE | NativeWin32.SWP_SHOWWINDOW);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_activeTab != PopoverTab.Turns) return;
            int totalRows = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
            int maxScroll = Math.Max(0, totalRows - _visibleRows);
            if (maxScroll <= 0) return;
            int delta = e.Delta > 0 ? -1 : 1;
            int next = Math.Max(0, Math.Min(maxScroll, _scrollOffset + delta));
            if (next != _scrollOffset)
            {
                _scrollOffset = next;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || _activeTab != PopoverTab.Turns) return;

            int totalRows = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
            int maxScroll = Math.Max(0, totalRows - _visibleRows);
            if (maxScroll > 0 && !_scrollTrackRect.IsEmpty)
            {
                if (_scrollThumbRect.Contains(e.Location))
                {
                    _draggingScroll = true;
                    _dragStartY = e.Y;
                    _dragStartOffset = _scrollOffset;
                    Capture = true;
                    return;
                }
                if (_scrollTrackRect.Contains(e.Location))
                {
                    double ratio = (double)(e.Y - _scrollTrackRect.Top) / Math.Max(1, _scrollTrackRect.Height);
                    _scrollOffset = Math.Max(0, Math.Min(maxScroll, (int)Math.Round(ratio * maxScroll)));
                    Invalidate();
                    return;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_draggingScroll)
            {
                int totalRows = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
                int maxScroll = Math.Max(0, totalRows - _visibleRows);
                int usableTrack = Math.Max(1, _scrollTrackRect.Height - _scrollThumbRect.Height);
                int deltaPx = e.Y - _dragStartY;
                int next = _dragStartOffset + (int)Math.Round(deltaPx * (double)maxScroll / usableTrack);
                next = Math.Max(0, Math.Min(maxScroll, next));
                if (next != _scrollOffset)
                {
                    _scrollOffset = next;
                    Invalidate();
                }
                return;
            }

            bool overClose = _closeButtonRect.Contains(e.Location);
            bool overPin = _pinButtonRect.Contains(e.Location);
            int nextHoverTab = -1;
            if (_tabSessionRect.Contains(e.Location)) nextHoverTab = 0;
            else if (_tabHistoryRect.Contains(e.Location)) nextHoverTab = 1;
            else if (_tabTurnsRect.Contains(e.Location)) nextHoverTab = 2;

            int nextHoverRow = -1;
            int nextHoverDay = -1;
            if (_activeTab != PopoverTab.History)
            {
                if (!_tableRowsRect.IsEmpty && _tableRowsRect.Contains(e.Location))
                {
                    int r = (e.Y - _tableRowsRect.Top) / RowHeight;
                    int count = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
                    if (r >= 0 && r < _visibleRows && (_scrollOffset + r) < count)
                        nextHoverRow = r;
                }
            }
            else if (_dayBarRects != null)
            {
                for (int i = 0; i < _dayBarRects.Length; i++)
                {
                    if (_dayBarRects[i].Contains(e.Location))
                    {
                        nextHoverDay = i;
                        break;
                    }
                }
            }

            bool needRepaint = (overClose != _closeHovered)
                || (overPin != _pinHovered)
                || (nextHoverTab != _hoveredTab)
                || (nextHoverRow != _hoveredRowIndex)
                || (nextHoverDay != _hoveredDayIndex);
            _closeHovered = overClose;
            _pinHovered = overPin;
            _hoveredTab = nextHoverTab;
            _hoveredRowIndex = nextHoverRow;
            _hoveredDayIndex = nextHoverDay;
            bool hand = overClose || overPin || nextHoverTab >= 0 || nextHoverRow >= 0 || nextHoverDay >= 0
                || (_activeTab == PopoverTab.Turns && !_scrollThumbRect.IsEmpty && _scrollThumbRect.Contains(e.Location));
            Cursor = hand ? Cursors.Hand : Cursors.Default;
            if (needRepaint) Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_draggingScroll) return;
            if (_closeHovered || _pinHovered || _hoveredTab >= 0 || _hoveredRowIndex >= 0 || _hoveredDayIndex >= 0)
            {
                _closeHovered = false;
                _pinHovered = false;
                _hoveredTab = -1;
                _hoveredRowIndex = -1;
                _hoveredDayIndex = -1;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_draggingScroll)
            {
                _draggingScroll = false;
                Capture = false;
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            if (_closeButtonRect.Contains(e.Location))
            {
                Hide();
                return;
            }

            if (_pinButtonRect.Contains(e.Location))
            {
                _isPinned = !_isPinned;
                SyncDismissHooks();
                Invalidate();
                return;
            }

            PopoverTab? targetTab = null;
            if (_tabSessionRect.Contains(e.Location)) targetTab = PopoverTab.Session;
            else if (_tabHistoryRect.Contains(e.Location)) targetTab = PopoverTab.History;
            else if (_tabTurnsRect.Contains(e.Location)) targetTab = PopoverTab.Turns;
            if (targetTab.HasValue)
            {
                if (_activeTab != targetTab.Value)
                {
                    _activeTab = targetTab.Value;
                    _hoveredRowIndex = -1;
                    _selectedTurnIndex = -1;
                    _hoveredDayIndex = -1;
                    if (_activeTab == PopoverTab.History && HistoryTabActivated != null)
                        HistoryTabActivated(this, EventArgs.Empty);
                    PositionAbove(_anchorStrip);
                    Invalidate();
                }
                return;
            }

            if (_activeTab != PopoverTab.History)
            {
                if (!_tableRowsRect.IsEmpty && _tableRowsRect.Contains(e.Location))
                {
                    int r = (e.Y - _tableRowsRect.Top) / RowHeight;
                    int count = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
                    int revIdx = count - 1 - (_scrollOffset + r);
                    if (r >= 0 && r < _visibleRows && revIdx >= 0 && revIdx < count)
                    {
                        int turnNum = _snapshot.Turns[revIdx].Index;
                        _selectedTurnIndex = (_selectedTurnIndex == turnNum) ? -1 : turnNum;
                        Invalidate();
                    }
                }
            }
            else if (_dayBarRects != null)
            {
                for (int i = 0; i < _dayBarRects.Length; i++)
                {
                    if (_dayBarRects[i].Contains(e.Location))
                    {
                        _selectedDayIndex = (_selectedDayIndex == i) ? -1 : i;
                        Invalidate();
                        return;
                    }
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            RenderCard(
                e.Graphics, ClientSize, _snapshot, _history, _activeTab, _palette,
                _scrollOffset, _visibleRows, _hoveredRowIndex, _selectedTurnIndex,
                _hoveredDayIndex, _selectedDayIndex, _hoveredTab,
                _isPinned, _pinHovered, _closeHovered,
                out _pinButtonRect, out _closeButtonRect,
                out _tabSessionRect, out _tabHistoryRect, out _tabTurnsRect,
                out _tableRowsRect, out _scrollTrackRect, out _scrollThumbRect,
                out _dayBarRects);
        }

        private static int TurnTableChromeHeight(PopoverTab tab)
        {
            int chrome = HeaderHeight + TabBarHeight + TableHeaderHeight + InspectorHeight + 12;
            return tab == PopoverTab.Session ? chrome + CardGridHeight + QuotaSectionHeight + ContextBarHeight : chrome;
        }

        internal static Bitmap RenderPreviewBitmap(SessionMetricsSnapshot snap, ThemePalette palette, PopoverTab tab = PopoverTab.Session)
        {
            int rows = snap != null && snap.Turns != null ? snap.Turns.Count : 0;
            int limit = tab == PopoverTab.Turns ? DetailVisibleRows : SessionVisibleRows;
            int vis = Math.Max(1, Math.Min(limit, rows));
            int height = TurnTableChromeHeight(tab) + vis * RowHeight;
            var bmp = new Bitmap(PopoverWidth, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Rectangle r1, r2, r3, r4, r5, r6, r7, r8;
                Rectangle[] dBars;
                RenderCard(g, new Size(PopoverWidth, height), snap ?? new SessionMetricsSnapshot(), new HistoryStatsSnapshot(),
                    tab, palette,
                    0, vis, 0, -1, -1, -1, -1, false, false, false,
                    out r1, out r2, out r3, out r4, out r5, out r6, out r7, out r8, out dBars);
            }
            return bmp;
        }

        internal static Bitmap RenderHistoryPreviewBitmap(SessionMetricsSnapshot snap, HistoryStatsSnapshot history, ThemePalette palette)
        {
            var hist = history ?? new HistoryStatsSnapshot();
            int height = HeaderHeight + TabBarHeight + HistSummaryHeight + HistChartHeight + ComputeHistoryModelHeight(hist) + HistTopThreadsHeight + 12;
            var bmp = new Bitmap(PopoverWidth, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Rectangle r1, r2, r3, r4, r5, r6, r7, r8;
                Rectangle[] dBars;
                RenderCard(g, new Size(PopoverWidth, height), snap ?? new SessionMetricsSnapshot(), hist,
                    PopoverTab.History, palette,
                    0, 0, -1, -1, -1, -1, -1, false, false, false,
                    out r1, out r2, out r3, out r4, out r5, out r6, out r7, out r8, out dBars);
            }
            return bmp;
        }

        private static int ComputeHistoryModelHeight(HistoryStatsSnapshot hist)
        {
            int rows = hist != null && hist.Models7d != null ? Math.Min(4, hist.Models7d.Count) : 0;
            if (rows <= 0) return 76;
            return 56 + (rows * 20);
        }

        private static void RenderCard(
            Graphics g,
            Size size,
            SessionMetricsSnapshot snap,
            HistoryStatsSnapshot history,
            PopoverTab activeTab,
            ThemePalette pal,
            int scrollOffset,
            int visibleRows,
            int hoveredRow,
            int selectedTurnIndex,
            int hoveredDay,
            int selectedDay,
            int hoveredTab,
            bool isPinned,
            bool pinHovered,
            bool closeHovered,
            out Rectangle pinRect,
            out Rectangle closeRect,
            out Rectangle tabSessionRect,
            out Rectangle tabHistoryRect,
            out Rectangle tabTurnsRect,
            out Rectangle tableRowsRect,
            out Rectangle scrollTrackRect,
            out Rectangle scrollThumbRect,
            out Rectangle[] dayBarRects)
        {
            tableRowsRect = Rectangle.Empty;
            scrollTrackRect = Rectangle.Empty;
            scrollThumbRect = Rectangle.Empty;
            dayBarRects = new Rectangle[0];

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            g.Clear(pal.SurfaceBg);
            using (var borderPen = new Pen(pal.BorderStrong, 1f))
                g.DrawRectangle(borderPen, 0, 0, size.Width - 1, size.Height - 1);

            using (var titleFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point))
            using (var badgeFont = new Font("Segoe UI", 7.8f, FontStyle.Bold, GraphicsUnit.Point))
            using (var labelFont = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular, GraphicsUnit.Point))
            using (var heroFont = new Font("Segoe UI Semibold", 13.0f, FontStyle.Regular, GraphicsUnit.Point))
            using (var subFont = new Font("Segoe UI", 7.9f, FontStyle.Regular, GraphicsUnit.Point))
            using (var tableHeadFont = new Font("Microsoft YaHei UI", 7.8f, FontStyle.Bold, GraphicsUnit.Point))
            using (var monoFont = new Font("Consolas", 8.4f, FontStyle.Regular, GraphicsUnit.Point))
            using (var monoBoldFont = new Font("Consolas", 8.4f, FontStyle.Bold, GraphicsUnit.Point))
            {
                int y = 0;

                // 1. Header bar
                DrawHeader(g, size.Width, y, snap, history, activeTab, pal, titleFont, badgeFont, subFont, isPinned, pinHovered, closeHovered, out pinRect, out closeRect);
                y += HeaderHeight;
                using (var sep = new Pen(pal.Border, 1f))
                    g.DrawLine(sep, 1, y, size.Width - 2, y);

                // 2. Segmented Tab Bar
                DrawTabBar(g, size.Width, y, activeTab, hoveredTab, pal, tableHeadFont, labelFont, out tabSessionRect, out tabHistoryRect, out tabTurnsRect);
                y += TabBarHeight;

                if (activeTab == PopoverTab.Turns)
                {
                    DrawTurnTable(g, size, y, snap, pal, true, scrollOffset, visibleRows, hoveredRow, selectedTurnIndex,
                        tableHeadFont, subFont, out tableRowsRect, out scrollTrackRect, out scrollThumbRect);
                    return;
                }

                if (activeTab == PopoverTab.History)
                {
                    RenderHistoryBody(g, size, y, history ?? new HistoryStatsSnapshot(), pal,
                        hoveredDay, selectedDay,
                        labelFont, heroFont, subFont, tableHeadFont, monoFont, monoBoldFont,
                        out dayBarRects);
                    return;
                }

                // 3. Session View: 2x2 KPI Cards
                int gridTop = y + 8;
                int gap = 8;
                int cardW = (size.Width - (Pad * 2) - gap) / 2;
                int cardH = 56;

                // Card 1: TPS + Session Avg TPS
                string tpsVal = MetricFormatter.FormatTps(snap.DisplayTps, snap.TpsIsHeld);
                string tpsHero = tpsVal == "--" ? "-- tok/s" : (tpsVal + " tok/s");
                string avgPart = snap.SessionAvgTps.HasValue
                    ? ("均速 " + snap.SessionAvgTps.Value.ToString("0.0", CultureInfo.InvariantCulture) + " · ")
                    : string.Empty;
                string tpsSub = string.Format("{0}首字 {1} · 活跃 {2}",
                    avgPart,
                    MetricFormatter.FormatSeconds(snap.LatestTtftMs),
                    MetricFormatter.FormatSeconds(snap.LatestModelActiveMs));
                Color tpsColor = snap.DisplayTps.HasValue ? pal.ForTier(MetricFormatter.ClassifySpeed(snap.DisplayTps)) : pal.TextMuted;
                DrawKpiCard(g, new Rectangle(Pad, gridTop, cardW, cardH),
                    snap.IsStreaming ? "输出速度 (生成中)" : "输出速度 (TPS)",
                    tpsHero, tpsSub, tpsColor, pal, labelFont, heroFont, subFont);

                // Card 2: Cache Hit Rate (Turn / Session — 2 decimal places)
                var latest = snap.LatestTurn;
                double? turnCachePct = latest != null ? latest.CacheHitPct : null;
                double? sessCachePct = snap.SessionCacheHitPct;
                string cacheHero = (turnCachePct.HasValue && sessCachePct.HasValue)
                    ? string.Format("{0} / {1}", MetricFormatter.FormatCacheTwoDecimals(turnCachePct), MetricFormatter.FormatCacheTwoDecimals(sessCachePct))
                    : MetricFormatter.FormatCacheTwoDecimals(turnCachePct ?? sessCachePct);
                string cacheSub = snap.HasUsage
                    ? string.Format("累计命中 {0} / {1}", MetricFormatter.CompactTokens(snap.CachedInputTokens), MetricFormatter.CompactTokens(snap.InputTokens))
                    : "等待会话数据";
                double? tierPct = turnCachePct ?? sessCachePct;
                Color cacheColor = tierPct.HasValue ? pal.ForTier(MetricFormatter.ClassifyCache(tierPct)) : pal.TextMuted;
                using (var cacheHeroFont = new Font("Segoe UI Semibold", 11.8f, FontStyle.Regular, GraphicsUnit.Point))
                    DrawKpiCard(g, new Rectangle(Pad + cardW + gap, gridTop, cardW, cardH),
                        "缓存命中率 (本轮 / 累计)", cacheHero, cacheSub, cacheColor, pal, labelFont, cacheHeroFont, subFont);

                // Card 3: Latest Turn
                string turnHero = latest != null ? MetricFormatter.CompactTokens(latest.TotalTokens) : "--";
                string turnSub = latest != null
                    ? string.Format("入 {0} · 出 {1} · 缓存 {2}",
                        MetricFormatter.CompactTokens(latest.InputTokens),
                        MetricFormatter.CompactTokens(latest.OutputTokens),
                        MetricFormatter.FormatCacheTwoDecimals(latest.CacheHitPct))
                    : "尚无已完成轮次";
                DrawKpiCard(g, new Rectangle(Pad, gridTop + cardH + gap, cardW, cardH),
                    latest != null ? ("最近一轮 #" + latest.Index) : "最近一轮",
                    turnHero, turnSub, pal.TextPrimary, pal, labelFont, heroFont, subFont);

                // Card 4: Session Cumulative
                string cumHero = snap.HasUsage ? MetricFormatter.CompactTokens(snap.TotalTokens) : "--";
                string cumSub = snap.HasUsage
                    ? string.Format("入 {0} · 出 {1} · 推理 {2}",
                        MetricFormatter.CompactTokens(snap.InputTokens),
                        MetricFormatter.CompactTokens(snap.OutputTokens),
                        MetricFormatter.CompactTokens(snap.ReasoningOutputTokens))
                    : "尚无累计用量";
                DrawKpiCard(g, new Rectangle(Pad + cardW + gap, gridTop + cardH + gap, cardW, cardH),
                    "会话累计", cumHero, cumSub, pal.TextPrimary, pal, labelFont, heroFont, subFont);

                y += CardGridHeight;

                // 3. Dedicated Account-Level Quota Cards (5h + Weekly + Reset Countdown)
                DrawQuotaSection(g, size.Width, y, snap, pal, labelFont, subFont, monoBoldFont);
                y += QuotaSectionHeight;

                // 4. 10-Channel Context Window Card
                DrawContextCard(g, size.Width, y, snap, pal, subFont);
                y += ContextBarHeight;

                using (var sep = new Pen(pal.Border, 1f))
                    g.DrawLine(sep, Pad, y, size.Width - Pad, y);

                // Recent turns on the session page.
                DrawTurnTable(g, size, y, snap, pal, false, 0, visibleRows, hoveredRow, selectedTurnIndex,
                    tableHeadFont, subFont,
                    out tableRowsRect, out scrollTrackRect, out scrollThumbRect);
            }
        }

        private static void DrawHeader(
            Graphics g, int width, int y, SessionMetricsSnapshot snap, HistoryStatsSnapshot history, PopoverTab activeTab, ThemePalette pal,
            Font titleFont, Font badgeFont, Font subFont,
            bool isPinned, bool pinHovered, bool closeHovered,
            out Rectangle pinRect, out Rectangle closeRect)
        {
            using (var titleBrush = new SolidBrush(pal.TextPrimary))
                g.DrawString("Codex 用量监控", titleFont, titleBrush, Pad, y + 10);

            int curX = Pad + 114;

            if (activeTab == PopoverTab.History)
            {
                int thCount = history != null ? history.SevenDayThreads : 0;
                curX = DrawSmallPill(g, curX, y + 10, string.Format("近 7 日 · {0} 会话", thCount), badgeFont, pal.AccentBlue);
            }
            else
            {
                // Binding source pill
                string src = string.IsNullOrWhiteSpace(snap.BindingSource) ? "等待" : snap.BindingSource;
                Color badgeColor;
                string badgeText;
                if (src.StartsWith("IPC+", StringComparison.Ordinal) || src == "IPC"
                    || src.StartsWith("本地+", StringComparison.Ordinal))
                {
                    badgeColor = pal.TierGood;
                    badgeText = src == "IPC" ? "IPC 绑定" : src;
                }
                else if (src == "待确认" || src == "多窗口歧义")
                {
                    badgeColor = pal.TierWarn;
                    badgeText = "待确认";
                }
                else if (src == "IPC 断线")
                {
                    badgeColor = pal.TierDanger;
                    badgeText = "IPC 断线";
                }
                else
                {
                    badgeColor = pal.TextMuted;
                    badgeText = "等待会话";
                }

                curX = DrawSmallPill(g, curX, y + 10, badgeText, badgeFont, badgeColor);

                // Model & reasoning effort pill (from state_5.sqlite)
                string modelBadge = MetricFormatter.FormatModelBadge(snap.ModelName, snap.ReasoningEffort);
                if (!string.IsNullOrEmpty(modelBadge))
                {
                    curX = DrawSmallPill(g, curX + 6, y + 10, modelBadge, badgeFont, pal.AccentBlue);
                }
                else if (!string.IsNullOrWhiteSpace(snap.ThreadId))
                {
                    using (var tidBrush = new SolidBrush(pal.TextMuted))
                        g.DrawString(MetricFormatter.ShortThreadId(snap.ThreadId), subFont, tidBrush, curX + 8, y + 11);
                }
            }

            // Right buttons: Pin toggle + Close
            closeRect = new Rectangle(width - Pad - 22, y + 8, 22, 22);
            pinRect = new Rectangle(closeRect.Left - 46, y + 8, 42, 22);

            Color pinAccent = isPinned ? pal.AccentBlue : (pinHovered ? pal.TextPrimary : pal.TextSecondary);
            if (isPinned || pinHovered)
            {
                using (var pPath = StripForm.CreateRoundedPath(pinRect, 4))
                using (var pFill = new SolidBrush(isPinned ? Color.FromArgb(26, pal.AccentBlue) : pal.RowHoverBg))
                    g.FillPath(pFill, pPath);
                if (isPinned)
                {
                    using (var pPath = StripForm.CreateRoundedPath(pinRect, 4))
                    using (var pPen = new Pen(Color.FromArgb(95, pal.AccentBlue), 1f))
                        g.DrawPath(pPen, pPath);
                }
            }
            using (var pinBrush = new SolidBrush(pinAccent))
                g.DrawString(isPinned ? "已钉" : "钉住", badgeFont, pinBrush, pinRect.X + 7, pinRect.Y + 4);

            if (closeHovered)
            {
                using (var path = StripForm.CreateRoundedPath(closeRect, 4))
                using (var fill = new SolidBrush(pal.RowHoverBg))
                    g.FillPath(fill, path);
            }
            using (var xPen = new Pen(closeHovered ? pal.TextPrimary : pal.TextSecondary, 1.5f))
            {
                int cx = closeRect.X + 7;
                int cy = closeRect.Y + 7;
                g.DrawLine(xPen, cx, cy, cx + 8, cy + 8);
                g.DrawLine(xPen, cx + 8, cy, cx, cy + 8);
            }
        }

        private static void DrawTabBar(
            Graphics g, int width, int y, PopoverTab activeTab, int hoveredTab, ThemePalette pal,
            Font boldFont, Font regularFont,
            out Rectangle tabSessionRect, out Rectangle tabHistoryRect, out Rectangle tabTurnsRect)
        {
            var outer = new Rectangle(Pad, y + 6, width - (Pad * 2), 26);
            using (var oPath = StripForm.CreateRoundedPath(outer, 6))
            using (var oFill = new SolidBrush(pal.CardBg))
            using (var oPen = new Pen(pal.Border, 1f))
            {
                g.FillPath(oFill, oPath);
                g.DrawPath(oPen, oPath);
            }

            int innerPad = 2;
            int tabWidth = (outer.Width - 4 * innerPad) / 3;
            int tabHeight = outer.Height - 2 * innerPad;
            int tabY = outer.Y + innerPad;
            tabSessionRect = new Rectangle(outer.X + innerPad, tabY, tabWidth, tabHeight);
            tabTurnsRect = new Rectangle(tabSessionRect.Right + innerPad, tabY, tabWidth, tabHeight);
            tabHistoryRect = new Rectangle(tabTurnsRect.Right + innerPad, tabY, outer.Right - tabTurnsRect.Right - 2 * innerPad, tabHeight);

            DrawSingleTab(g, tabSessionRect, "当前会话", activeTab == PopoverTab.Session, hoveredTab == 0, pal, boldFont, regularFont);
            DrawSingleTab(g, tabTurnsRect, "逐轮明细", activeTab == PopoverTab.Turns, hoveredTab == 2, pal, boldFont, regularFont);
            DrawSingleTab(g, tabHistoryRect, "今日与 7 日汇总", activeTab == PopoverTab.History, hoveredTab == 1, pal, boldFont, regularFont);
        }

        private static void DrawSingleTab(
            Graphics g, Rectangle rect, string text, bool active, bool hovered,
            ThemePalette pal, Font boldFont, Font regularFont)
        {
            if (active)
            {
                using (var path = StripForm.CreateRoundedPath(rect, 5))
                using (var fill = new SolidBrush(pal.Kind == CodexThemeKind.Dark ? pal.RowHoverBg : pal.SurfaceBg))
                using (var pen = new Pen(pal.BorderStrong, 1f))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }
            }
            else if (hovered)
            {
                using (var path = StripForm.CreateRoundedPath(rect, 5))
                using (var fill = new SolidBrush(pal.RowHoverBg))
                    g.FillPath(fill, path);
            }

            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var brush = new SolidBrush(active ? pal.TextPrimary : (hovered ? pal.TextPrimary : pal.TextSecondary)))
                g.DrawString(text, active ? boldFont : regularFont, brush, rect, fmt);
        }

        private static void RenderHistoryBody(
            Graphics g, Size size, int topY, HistoryStatsSnapshot hist, ThemePalette pal,
            int hoveredDay, int selectedDay,
            Font labelFont, Font heroFont, Font subFont, Font headFont, Font monoFont, Font monoBoldFont,
            out Rectangle[] dayBarRects)
        {
            int y = topY;

            // 1. 2x1 Summary KPI Cards (Today vs Last 7 Days)
            int gap = 8;
            int cardW = (size.Width - (Pad * 2) - gap) / 2;
            int cardH = 58;
            int cardTop = y + 6;

            double todayShare = hist.SevenDayTokens > 0 ? (hist.TodayTokens * 100.0 / hist.SevenDayTokens) : 0.0;
            string todayVal = hist.TodayTokens > 0 ? MetricFormatter.CompactTokens(hist.TodayTokens) : "0";
            string todaySub = hist.TodayThreads > 0
                ? string.Format("{0} 个活跃会话 · 占近 7 日 {1:0.0}%", hist.TodayThreads, todayShare)
                : "今日暂无新增消耗";
            DrawKpiCard(g, new Rectangle(Pad, cardTop, cardW, cardH),
                "今日消耗", todayVal, todaySub, pal.AccentBlue, pal, labelFont, heroFont, subFont);

            string weekVal = hist.SevenDayTokens > 0 ? MetricFormatter.CompactTokens(hist.SevenDayTokens) : "0";
            string weekSub = string.Format("{0} 个活跃会话 · 日均 {1}",
                hist.SevenDayThreads,
                MetricFormatter.CompactTokens(hist.SevenDayAvgDailyTokens));
            DrawKpiCard(g, new Rectangle(Pad + cardW + gap, cardTop, cardW, cardH),
                "近 7 日合计", weekVal, weekSub, pal.TextPrimary, pal, labelFont, heroFont, subFont);

            y += HistSummaryHeight;

            // 2. 7-Day Bar Trend Card
            DrawSevenDayChartCard(g, size.Width, y, hist, pal, hoveredDay, selectedDay,
                labelFont, headFont, subFont, monoFont, out dayBarRects);
            y += HistChartHeight;

            // 3. Model Distribution Card (7d + all-time footnote)
            int modelH = ComputeHistoryModelHeight(hist);
            DrawModelDistributionCard(g, size.Width, y, modelH, hist, pal, labelFont, headFont, subFont, monoBoldFont);
            y += modelH;

            // 4. Top 5 Active Threads Card (7d)
            DrawTopThreadsCard(g, size.Width, y, hist, pal, labelFont, headFont, subFont, monoFont, monoBoldFont);
        }

        private static void DrawSevenDayChartCard(
            Graphics g, int width, int y, HistoryStatsSnapshot hist, ThemePalette pal,
            int hoveredDay, int selectedDay,
            Font labelFont, Font headFont, Font subFont, Font monoFont,
            out Rectangle[] dayBarRects)
        {
            var cardRect = new Rectangle(Pad, y + 2, width - (Pad * 2), HistChartHeight - 8);
            using (var cPath = StripForm.CreateRoundedPath(cardRect, 7))
            using (var cFill = new SolidBrush(pal.CardBg))
            using (var cPen = new Pen(pal.Border, 1f))
            {
                g.FillPath(cFill, cPath);
                g.DrawPath(cPen, cPath);
            }

            var days = hist.Last7Days ?? new List<DailyUsagePoint>();
            dayBarRects = new Rectangle[days.Count];

            // Determine focused day for top-right exact readout (hover > selected > peak/today)
            int focusIdx = -1;
            if (hoveredDay >= 0 && hoveredDay < days.Count) focusIdx = hoveredDay;
            else if (selectedDay >= 0 && selectedDay < days.Count) focusIdx = selectedDay;
            else if (days.Count > 0)
            {
                focusIdx = days.Count - 1;
                long peak = days[focusIdx].Tokens;
                for (int i = 0; i < days.Count; i++)
                {
                    if (days[i].Tokens > peak) { peak = days[i].Tokens; focusIdx = i; }
                }
            }

            using (var titleBrush = new SolidBrush(pal.TextPrimary))
                g.DrawString("近 7 日趋势", headFont, titleBrush, cardRect.X + 12, cardRect.Y + 9);

            if (focusIdx >= 0 && focusIdx < days.Count)
            {
                var fp = days[focusIdx];
                string tip = string.Format("{0} {1} · {2} tok · {3} 会话",
                    fp.DayLabel, fp.WeekdayLabel, MetricFormatter.ExactTokens(fp.Tokens), fp.ThreadCount);
                using (var tipBrush = new SolidBrush(fp.IsToday ? pal.AccentBlue : pal.TextSecondary))
                using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
                    g.DrawString(tip, subFont, tipBrush, cardRect.Right - 12, cardRect.Y + 9, rightFmt);
            }

            if (days.Count == 0) return;

            long maxTok = 1;
            for (int i = 0; i < days.Count; i++)
            {
                if (days[i].Tokens > maxTok) maxTok = days[i].Tokens;
            }

            int plotLeft = cardRect.X + 14;
            int plotRight = cardRect.Right - 14;
            int plotW = plotRight - plotLeft;
            int slotW = plotW / days.Count;
            int barW = Math.Min(34, Math.Max(18, slotW - 22));
            int chartTop = cardRect.Y + 42;
            int chartBottom = cardRect.Bottom - 34;
            int maxBarH = Math.Max(20, chartBottom - chartTop);

            // Subtle baseline
            using (var basePen = new Pen(pal.Border, 1f))
                g.DrawLine(basePen, plotLeft, chartBottom, plotRight, chartBottom);

            Color idleBarCol = pal.Kind == CodexThemeKind.Dark
                ? Color.FromArgb(175, 96, 165, 250)
                : Color.FromArgb(165, 59, 130, 246);
            Color activeBarCol = pal.AccentBlue;

            using (var centerFmt = new StringFormat { Alignment = StringAlignment.Center })
            using (var valFont = new Font("Segoe UI", 7.4f, FontStyle.Regular, GraphicsUnit.Point))
            using (var dayFont = new Font("Microsoft YaHei UI", 7.6f, FontStyle.Regular, GraphicsUnit.Point))
            using (var dayBoldFont = new Font("Microsoft YaHei UI", 7.6f, FontStyle.Bold, GraphicsUnit.Point))
            using (var mutedBrush = new SolidBrush(pal.TextMuted))
            using (var secBrush = new SolidBrush(pal.TextSecondary))
            using (var priBrush = new SolidBrush(pal.TextPrimary))
            using (var accentBrush = new SolidBrush(pal.AccentBlue))
            {
                for (int i = 0; i < days.Count; i++)
                {
                    var d = days[i];
                    int sx = plotLeft + (i * slotW);
                    var hitRect = new Rectangle(sx + 2, cardRect.Y + 28, slotW - 4, cardRect.Bottom - cardRect.Y - 32);
                    dayBarRects[i] = hitRect;

                    bool isHov = i == hoveredDay;
                    bool isSel = i == selectedDay;
                    if (isHov || isSel)
                    {
                        using (var hp = StripForm.CreateRoundedPath(hitRect, 5))
                        using (var hb = new SolidBrush(isSel ? Color.FromArgb(20, pal.AccentBlue) : pal.RowHoverBg))
                            g.FillPath(hb, hp);
                    }

                    int bx = sx + ((slotW - barW) / 2);
                    int bh = d.Tokens > 0 ? Math.Max(5, (int)Math.Round(maxBarH * (d.Tokens / (double)maxTok))) : 2;
                    int by = chartBottom - bh;

                    var barRect = new Rectangle(bx, by, barW, bh);
                    Color col = d.Tokens <= 0
                        ? pal.TrackBg
                        : ((d.IsToday || isHov || isSel) ? activeBarCol : idleBarCol);
                    using (var bp = StripForm.CreateRoundedPath(barRect, Math.Min(4, bh / 2)))
                    using (var bb = new SolidBrush(col))
                        g.FillPath(bb, bp);

                    string topVal = d.Tokens > 0 ? MetricFormatter.CompactTokens(d.Tokens) : "0";
                    Brush vBrush = (d.IsToday || isHov || isSel) ? priBrush : mutedBrush;
                    g.DrawString(topVal, valFont, vBrush, sx + (slotW / 2f), by - 14, centerFmt);

                    g.DrawString(d.DayLabel, d.IsToday ? dayBoldFont : dayFont, d.IsToday ? accentBrush : secBrush,
                        sx + (slotW / 2f), chartBottom + 4, centerFmt);
                    g.DrawString(d.WeekdayLabel, valFont, mutedBrush,
                        sx + (slotW / 2f), chartBottom + 18, centerFmt);
                }
            }
        }

        private static void DrawModelDistributionCard(
            Graphics g, int width, int y, int sectionHeight, HistoryStatsSnapshot hist, ThemePalette pal,
            Font labelFont, Font headFont, Font subFont, Font monoBoldFont)
        {
            var cardRect = new Rectangle(Pad, y + 2, width - (Pad * 2), sectionHeight - 8);
            using (var cPath = StripForm.CreateRoundedPath(cardRect, 7))
            using (var cFill = new SolidBrush(pal.CardBg))
            using (var cPen = new Pen(pal.Border, 1f))
            {
                g.FillPath(cFill, cPath);
                g.DrawPath(cPen, cPath);
            }

            int ix = cardRect.X + 12;
            int iw = cardRect.Width - 24;

            using (var titleBrush = new SolidBrush(pal.TextPrimary))
                g.DrawString("按模型分布 (近 7 日)", headFont, titleBrush, ix, cardRect.Y + 9);

            string allNote = string.Format("历史总计 {0} · {1} 会话",
                MetricFormatter.CompactTokens(hist.AllTimeTokens), hist.AllTimeThreads);
            using (var noteBrush = new SolidBrush(pal.TextMuted))
            using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
                g.DrawString(allNote, subFont, noteBrush, cardRect.Right - 12, cardRect.Y + 9, rightFmt);

            var models = hist.Models7d ?? new List<ModelUsageBucket>();
            if (models.Count == 0 || hist.SevenDayTokens <= 0)
            {
                using (var emptyBrush = new SolidBrush(pal.TextMuted))
                    g.DrawString("近 7 日暂无模型 Token 记录", labelFont, emptyBrush, ix, cardRect.Y + 38);
                return;
            }

            // Prepare up to 4 display rows (fold tail into "其他模型")
            var display = new List<ModelUsageBucket>();
            if (models.Count <= 4)
            {
                display.AddRange(models);
            }
            else
            {
                for (int i = 0; i < 3; i++) display.Add(models[i]);
                long otherTok = 0;
                int otherCnt = 0;
                for (int i = 3; i < models.Count; i++)
                {
                    otherTok += models[i].Tokens;
                    otherCnt += models[i].ThreadCount;
                }
                display.Add(new ModelUsageBucket
                {
                    ModelName = "其他模型",
                    Tokens = otherTok,
                    ThreadCount = otherCnt,
                    SharePct = hist.SevenDayTokens > 0 ? (otherTok * 100.0 / hist.SevenDayTokens) : 0.0
                });
            }

            Color[] modelColors = new Color[]
            {
                pal.AccentBlue,
                pal.CtxToolResults,
                pal.CtxUserInput,
                pal.CtxSystemTools
            };

            // Segmented horizontal bar
            int barY = cardRect.Y + 30;
            int barH = 7;
            var trackRect = new Rectangle(ix, barY, iw, barH);
            using (var tp = StripForm.CreateRoundedPath(trackRect, 3))
            using (var tb = new SolidBrush(pal.TrackBg))
                g.FillPath(tb, tp);

            int gapPx = 2;
            int totalGap = Math.Max(0, display.Count - 1) * gapPx;
            int availPx = Math.Max(display.Count * 4, iw - totalGap);
            int curX = ix;
            for (int i = 0; i < display.Count && curX < ix + iw; i++)
            {
                int segW = (i == display.Count - 1)
                    ? ((ix + iw) - curX)
                    : Math.Max(5, (int)Math.Round(availPx * (display[i].Tokens / (double)hist.SevenDayTokens)));
                if (curX + segW > ix + iw) segW = (ix + iw) - curX;
                if (segW <= 0) break;
                var segRect = new Rectangle(curX, barY, segW, barH);
                using (var sp = StripForm.CreateRoundedPath(segRect, 3))
                using (var sb = new SolidBrush(modelColors[i % modelColors.Length]))
                    g.FillPath(sb, sp);
                curX += segW + gapPx;
            }

            // Rows
            int rowStartY = cardRect.Y + 44;
            int rowStep = 20;
            using (var priBrush = new SolidBrush(pal.TextPrimary))
            using (var secBrush = new SolidBrush(pal.TextSecondary))
            using (var mutedBrush = new SolidBrush(pal.TextMuted))
            using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
            {
                for (int i = 0; i < display.Count; i++)
                {
                    var m = display[i];
                    int ry = rowStartY + (i * rowStep);
                    Color col = modelColors[i % modelColors.Length];

                    var sw = new Rectangle(ix, ry + 5, 7, 7);
                    using (var swp = StripForm.CreateRoundedPath(sw, 2))
                    using (var swb = new SolidBrush(col))
                        g.FillPath(swb, swp);

                    g.DrawString(m.ModelName, subFont, priBrush, ix + 13, ry + 1);

                    // Mini progress track in middle
                    int barLeft = ix + 150;
                    int miniW = 128;
                    var miniTrack = new Rectangle(barLeft, ry + 6, miniW, 5);
                    using (var mtp = StripForm.CreateRoundedPath(miniTrack, 2))
                    using (var mtb = new SolidBrush(pal.TrackBg))
                        g.FillPath(mtb, mtp);
                    int mfw = Math.Max(4, (int)Math.Round(miniW * Math.Min(100.0, m.SharePct) / 100.0));
                    var miniFill = new Rectangle(barLeft, ry + 6, Math.Min(miniW, mfw), 5);
                    using (var mfp = StripForm.CreateRoundedPath(miniFill, 2))
                    using (var mfb = new SolidBrush(col))
                        g.FillPath(mfb, mfp);

                    g.DrawString(MetricFormatter.CompactTokens(m.Tokens), monoBoldFont, priBrush, cardRect.Right - 110, ry + 1, rightFmt);
                    g.DrawString(string.Format("{0:0.0}%", m.SharePct), subFont, secBrush, cardRect.Right - 60, ry + 1, rightFmt);
                    g.DrawString(string.Format("{0} 会话", m.ThreadCount), subFont, mutedBrush, cardRect.Right - 12, ry + 1, rightFmt);
                }
            }
        }

        private static void DrawTopThreadsCard(
            Graphics g, int width, int y, HistoryStatsSnapshot hist, ThemePalette pal,
            Font labelFont, Font headFont, Font subFont, Font monoFont, Font monoBoldFont)
        {
            var cardRect = new Rectangle(Pad, y + 2, width - (Pad * 2), HistTopThreadsHeight - 6);
            using (var cPath = StripForm.CreateRoundedPath(cardRect, 7))
            using (var cFill = new SolidBrush(pal.CardBg))
            using (var cPen = new Pen(pal.Border, 1f))
            {
                g.FillPath(cFill, cPath);
                g.DrawPath(cPen, cPath);
            }

            int ix = cardRect.X + 12;
            using (var titleBrush = new SolidBrush(pal.TextPrimary))
                g.DrawString("近 7 日高消耗会话", headFont, titleBrush, ix, cardRect.Y + 9);
            using (var hintBrush = new SolidBrush(pal.TextMuted))
            using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
                g.DrawString("按会话最后活跃日汇总", subFont, hintBrush, cardRect.Right - 12, cardRect.Y + 9, rightFmt);

            var top = hist.TopThreads7d ?? new List<TopThreadItem>();
            if (top.Count == 0)
            {
                using (var emptyBrush = new SolidBrush(pal.TextMuted))
                    g.DrawString("近 7 日暂无活跃会话记录", labelFont, emptyBrush, ix, cardRect.Y + 38);
                return;
            }

            DateTime today = DateTime.Now.Date;
            int rowStartY = cardRect.Y + 31;
            int rowH = 22;

            using (var priBrush = new SolidBrush(pal.TextPrimary))
            using (var secBrush = new SolidBrush(pal.TextSecondary))
            using (var mutedBrush = new SolidBrush(pal.TextMuted))
            using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
            using (var ellipFmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                for (int i = 0; i < Math.Min(5, top.Count); i++)
                {
                    var item = top[i];
                    int ry = rowStartY + (i * rowH);
                    if (i % 2 == 1)
                    {
                        using (var alt = new SolidBrush(pal.RowAltBg))
                            g.FillRectangle(alt, cardRect.X + 6, ry, cardRect.Width - 12, rowH);
                    }

                    g.DrawString((i + 1).ToString(CultureInfo.InvariantCulture) + ".", monoFont, mutedBrush, ix, ry + 3);

                    var nameRect = new RectangleF(ix + 22, ry + 3, 212, 17);
                    g.DrawString(item.DisplayName, labelFont, priBrush, nameRect, ellipFmt);

                    string shortModel = item.ModelName.Length > 14 ? item.ModelName.Substring(0, 14) : item.ModelName;
                    g.DrawString(shortModel, subFont, secBrush, cardRect.Right - 118, ry + 3, rightFmt);

                    g.DrawString(MetricFormatter.CompactTokens(item.Tokens), monoBoldFont, priBrush, cardRect.Right - 56, ry + 3, rightFmt);

                    string dateStr;
                    if (item.LastActiveLocal.Date == today) dateStr = "今天";
                    else if (item.LastActiveLocal.Date == today.AddDays(-1)) dateStr = "昨天";
                    else dateStr = item.LastActiveLocal.ToString("MM/dd");
                    g.DrawString(dateStr, subFont, mutedBrush, cardRect.Right - 12, ry + 3, rightFmt);
                }
            }
        }

        private static int DrawSmallPill(Graphics g, int x, int y, string text, Font font, Color accent)
        {
            SizeF sz = g.MeasureString(text, font);
            var rect = new Rectangle(x, y, (int)Math.Ceiling(sz.Width) + 12, 18);
            using (var path = StripForm.CreateRoundedPath(rect, 4))
            using (var fill = new SolidBrush(Color.FromArgb(24, accent)))
            using (var pen = new Pen(Color.FromArgb(88, accent), 1f))
            using (var brush = new SolidBrush(accent))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
                g.DrawString(text, font, brush, rect.X + 6, rect.Y + 2);
            }
            return rect.Right;
        }

        private static void DrawKpiCard(
            Graphics g, Rectangle rect, string title, string value, string sub, Color valueColor,
            ThemePalette pal, Font labelFont, Font heroFont, Font subFont)
        {
            using (var path = StripForm.CreateRoundedPath(rect, 6))
            using (var fill = new SolidBrush(pal.CardBg))
            using (var pen = new Pen(pal.Border, 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            using (var lblBrush = new SolidBrush(pal.TextSecondary))
            using (var valBrush = new SolidBrush(valueColor))
            using (var subBrush = new SolidBrush(pal.TextMuted))
            {
                g.DrawString(title, labelFont, lblBrush, rect.X + 10, rect.Y + 6);
                g.DrawString(value, heroFont, valBrush, rect.X + 9, rect.Y + 19);
                g.DrawString(sub, subFont, subBrush, rect.X + 10, rect.Y + 40);
            }
        }

        private static void DrawQuotaSection(
            Graphics g, int width, int y, SessionMetricsSnapshot snap, ThemePalette pal,
            Font labelFont, Font subFont, Font monoBoldFont)
        {
            int gap = 8;
            int cardW = (width - (Pad * 2) - gap) / 2;
            int cardH = QuotaSectionHeight - 10;

            DrawSingleQuotaCard(
                g, new Rectangle(Pad, y + 2, cardW, cardH),
                "5h 额度余量", snap.PrimaryUsedPct, snap.PrimaryResetsAt, false,
                snap.QuotaIsAccountLive ? "实时" : null, pal, labelFont, subFont, monoBoldFont);

            string creditNote = (snap.HasCredits && !string.IsNullOrWhiteSpace(snap.CreditsBalance) && snap.CreditsBalance != "0")
                ? ("余额 $" + snap.CreditsBalance)
                : (snap.UnlimitedCredits ? "无限额度" : null);
            DrawSingleQuotaCard(
                g, new Rectangle(Pad + cardW + gap, y + 2, cardW, cardH),
                "周额度余量", snap.SecondaryUsedPct, snap.SecondaryResetsAt, true,
                creditNote, pal, labelFont, subFont, monoBoldFont);
        }

        private static void DrawSingleQuotaCard(
            Graphics g, Rectangle rect, string title, double? usedPct, long? resetsAt, bool isWeekly,
            string rightTag, ThemePalette pal, Font labelFont, Font subFont, Font monoBoldFont)
        {
            using (var path = StripForm.CreateRoundedPath(rect, 6))
            using (var fill = new SolidBrush(pal.CardBg))
            using (var pen = new Pen(pal.Border, 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            int ix = rect.X + 10;
            int iw = rect.Width - 20;

            using (var lblBrush = new SolidBrush(pal.TextSecondary))
                g.DrawString(title, labelFont, lblBrush, ix, rect.Y + 7);

            bool hasVal = usedPct.HasValue;
            double rem = hasVal ? Math.Max(0.0, Math.Min(100.0, 100.0 - usedPct.Value)) : 0.0;
            string countdown = MetricFormatter.FormatResetCountdown(resetsAt, isWeekly);
            bool alreadyReset = countdown == "已重置";
            if (hasVal && alreadyReset) rem = 100.0;

            Color tierCol = hasVal ? pal.ForTier(MetricFormatter.ClassifyQuotaRemaining(rem)) : pal.TextMuted;
            string pctStr = hasVal ? string.Format("{0:0}%", rem) : "--";
            using (var pctBrush = new SolidBrush(tierCol))
            using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
                g.DrawString(pctStr, monoBoldFont, pctBrush, rect.Right - 10, rect.Y + 7, rightFmt);

            // Progress track
            var trackRect = new Rectangle(ix, rect.Y + 27, iw, 6);
            using (var tPath = StripForm.CreateRoundedPath(trackRect, 3))
            using (var tBrush = new SolidBrush(pal.TrackBg))
                g.FillPath(tBrush, tPath);

            if (hasVal && rem > 0)
            {
                int fw = Math.Max(6, (int)Math.Round(iw * rem / 100.0));
                var fillRect = new Rectangle(ix, rect.Y + 27, Math.Min(iw, fw), 6);
                using (var fPath = StripForm.CreateRoundedPath(fillRect, 3))
                using (var fBrush = new SolidBrush(tierCol))
                    g.FillPath(fBrush, fPath);
            }

            // Bottom line: reset countdown on left + live/credit tag on right
            string footLeft = !string.IsNullOrEmpty(countdown) ? countdown : (hasVal ? "配额已同步" : "等待配额数据");
            using (var mBrush = new SolidBrush(pal.TextMuted))
                g.DrawString(footLeft, subFont, mBrush, ix, rect.Y + 40);

            if (!string.IsNullOrEmpty(rightTag))
            {
                using (var tagBrush = new SolidBrush(pal.TextSecondary))
                using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
                    g.DrawString(rightTag, subFont, tagBrush, rect.Right - 10, rect.Y + 40, rightFmt);
            }
        }

        private static void DrawContextCard(Graphics g, int width, int y, SessionMetricsSnapshot snap, ThemePalette pal, Font subFont)
        {
            var cardRect = new Rectangle(Pad, y + 2, width - (Pad * 2), ContextBarHeight - 8);
            using (var cardPath = StripForm.CreateRoundedPath(cardRect, 7))
            using (var cardFill = new SolidBrush(pal.CardBg))
            using (var cardPen = new Pen(pal.Border, 1f))
            {
                g.FillPath(cardFill, cardPath);
                g.DrawPath(cardPen, cardPath);
            }

            int innerX = cardRect.X + 12;
            int innerW = cardRect.Width - 24;

            DrawLayersIcon(g, innerX, cardRect.Y + 9, pal.TextPrimary);
            using (var hdrFont = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Bold, GraphicsUnit.Point))
            using (var hdrBrush = new SolidBrush(pal.TextPrimary))
                g.DrawString("上下文窗口", hdrFont, hdrBrush, innerX + 18, cardRect.Y + 8);

            string rightUsage = snap.ContextWindowTokens > 0
                ? string.Format("{0} / {1} ({2:0.0}%)  ·  剩余 {3}",
                    MetricFormatter.CompactTokensLower(snap.ContextUsedTokens),
                    MetricFormatter.CompactTokensLower(snap.ContextWindowTokens),
                    snap.ContextPct ?? 0.0,
                    MetricFormatter.CompactTokensLower(snap.ContextRemainingTokens))
                : (snap.ContextUsedTokens > 0 ? (MetricFormatter.CompactTokensLower(snap.ContextUsedTokens) + " / --") : "-- / --");
            using (var rightFont = new Font("Segoe UI", 8.4f, FontStyle.Regular, GraphicsUnit.Point))
            using (var rightBrush = new SolidBrush(pal.TextSecondary))
            {
                SizeF rSz = g.MeasureString(rightUsage, rightFont);
                g.DrawString(rightUsage, rightFont, rightBrush, cardRect.Right - 12 - rSz.Width, cardRect.Y + 9);
            }

            int barY = cardRect.Y + 32;
            int barH = 7;
            var trackRect = new Rectangle(innerX, barY, innerW, barH);
            using (var trackPath = StripForm.CreateRoundedPath(trackRect, 3))
            using (var trackBrush = new SolidBrush(pal.TrackBg))
                g.FillPath(trackBrush, trackPath);

            var channels = snap.ContextChannels ?? new List<ContextChannelItem>();
            if (snap.ContextWindowTokens > 0 && snap.ContextUsedTokens > 0 && channels.Count > 0)
            {
                double win = (double)snap.ContextWindowTokens;
                int gapPx = 2;
                var nonZero = new List<ContextChannelItem>();
                for (int i = 0; i < channels.Count; i++)
                {
                    if (channels[i].Tokens > 0) nonZero.Add(channels[i]);
                }

                if (nonZero.Count > 0)
                {
                    int totalBarPx = Math.Min(innerW, Math.Max(8, (int)Math.Round(innerW * Math.Min(1.0, snap.ContextUsedTokens / win))));
                    int totalGapPx = Math.Max(0, nonZero.Count - 1) * gapPx;
                    int availPx = Math.Max(nonZero.Count * 3, totalBarPx - totalGapPx);
                    int curX = innerX;
                    long usedSum = Math.Max(1, snap.ContextUsedTokens);

                    for (int i = 0; i < nonZero.Count && curX < innerX + innerW; i++)
                    {
                        var ch = nonZero[i];
                        int segW = (int)Math.Round(availPx * (ch.Tokens / (double)usedSum));
                        if (segW < 4) segW = 4;
                        if (curX + segW > innerX + innerW) segW = (innerX + innerW) - curX;
                        if (segW <= 0) break;

                        var segRect = new Rectangle(curX, barY, segW, barH);
                        using (var segPath = StripForm.CreateRoundedPath(segRect, 3))
                        using (var segBrush = new SolidBrush(pal.ForContextChannel(ch.Key)))
                            g.FillPath(segBrush, segPath);

                        curX += segW + gapPx;
                    }
                }
            }

            int gridY = cardRect.Y + 47;
            int colGap = 18;
            int colW = (innerW - colGap) / 2;
            int itemRowH = 21;

            using (var chLabelFont = new Font("Microsoft YaHei UI", 8.3f, FontStyle.Regular, GraphicsUnit.Point))
            using (var chValFont = new Font("Segoe UI Semibold", 8.6f, FontStyle.Regular, GraphicsUnit.Point))
            using (var lblBrush = new SolidBrush(pal.TextSecondary))
            using (var valBrush = new SolidBrush(pal.TextPrimary))
            using (var zeroBrush = new SolidBrush(pal.TextMuted))
            using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
            {
                int displayCount = Math.Min(10, channels.Count);
                for (int i = 0; i < displayCount; i++)
                {
                    int row = i / 2;
                    int col = i % 2;
                    int cx = innerX + (col * (colW + colGap));
                    int cy = gridY + (row * itemRowH);

                    var ch = channels[i];
                    var swRect = new Rectangle(cx, cy + 5, 7, 7);
                    using (var swPath = StripForm.CreateRoundedPath(swRect, 2))
                    using (var swBrush = new SolidBrush(pal.ForContextChannel(ch.Key)))
                        g.FillPath(swBrush, swPath);

                    g.DrawString(ch.Label, chLabelFont, lblBrush, cx + 13, cy + 2);
                    string valText = ch.Tokens > 0 ? MetricFormatter.CompactTokensLower(ch.Tokens) : "0";
                    g.DrawString(valText, chValFont, ch.Tokens > 0 ? valBrush : zeroBrush, cx + colW, cy + 1, rightFmt);
                }
            }
        }

        private static void DrawLayersIcon(Graphics g, int x, int y, Color color)
        {
            using (var pen = new Pen(color, 1.3f))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawPolygon(pen, new PointF[]
                {
                    new PointF(x + 6f, y + 1f),
                    new PointF(x + 12f, y + 4f),
                    new PointF(x + 6f, y + 7f),
                    new PointF(x + 0f, y + 4f)
                });
                g.DrawLines(pen, new PointF[]
                {
                    new PointF(x + 0f, y + 7.5f),
                    new PointF(x + 6f, y + 10.5f),
                    new PointF(x + 12f, y + 7.5f)
                });
                g.DrawLines(pen, new PointF[]
                {
                    new PointF(x + 0f, y + 10.5f),
                    new PointF(x + 6f, y + 13.5f),
                    new PointF(x + 12f, y + 10.5f)
                });
            }
        }

        private static void DrawTurnTable(
            Graphics g, Size size, int topY, SessionMetricsSnapshot snap, ThemePalette pal,
            bool detail, int scrollOffset, int visibleRows, int hoveredRow, int selectedTurnIndex,
            Font headFont, Font subFont,
            out Rectangle tableRowsRect, out Rectangle scrollTrackRect, out Rectangle scrollThumbRect)
        {
            tableRowsRect = Rectangle.Empty;
            scrollTrackRect = Rectangle.Empty;
            scrollThumbRect = Rectangle.Empty;

            var turns = snap.Turns ?? new List<TurnSummary>();
            int count = turns.Count;
            bool needScroll = detail && count > visibleRows;
            int rightEdge = size.Width - Pad - (needScroll ? 12 : 0);
            int colIndex = Pad;
            int colModel = Pad + 40;
            int colSpeed = Pad + 153;
            int colFirst = Pad + 239;
            int colTokens = Pad + 290;

            using (var titleBrush = new SolidBrush(pal.TextPrimary))
            using (var mutedBrush = new SolidBrush(pal.TextSecondary))
            using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
            {
                if (!detail)
                    g.DrawString("最近五轮", headFont, titleBrush, Pad, topY + 7);
                else if (count > 0)
                {
                    string range = string.Format("{0}–{1} / {2} 轮", scrollOffset + 1, Math.Min(count, scrollOffset + visibleRows), count);
                    g.DrawString(range, headFont, mutedBrush, rightEdge, topY + 7, rightFmt);
                }

                int headY = topY + 25;
                g.DrawString("步序", headFont, mutedBrush, colIndex, headY);
                g.DrawString("模型", headFont, mutedBrush, colModel, headY);
                g.DrawString("速度", headFont, mutedBrush, colSpeed, headY);
                g.DrawString("首字", headFont, mutedBrush, colFirst, headY);
                g.DrawString("上下文 / 思考 / 正文", headFont, mutedBrush, colTokens, headY);
                g.DrawString("缓存", headFont, mutedBrush, rightEdge, headY, rightFmt);
            }

            int rowsStartY = topY + TableHeaderHeight;
            if (count == 0)
            {
                using (var emptyBrush = new SolidBrush(pal.TextMuted))
                    g.DrawString("当前会话暂无逐轮用量记录", headFont, emptyBrush, Pad, rowsStartY + 4);
                return;
            }

            int vis = Math.Min(visibleRows, count);
            tableRowsRect = new Rectangle(Pad - 4, rowsStartY, (rightEdge - Pad) + 8, vis * RowHeight);
            TurnSummary inspected = null;

            using (var dataFont = new Font("Segoe UI", 9.1f, FontStyle.Regular, GraphicsUnit.Point))
            using (var valueFont = new Font("Segoe UI", 9.1f, FontStyle.Bold, GraphicsUnit.Point))
            using (var modelFormat = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
            using (var textBrush = new SolidBrush(pal.TextPrimary))
            using (var secBrush = new SolidBrush(pal.TextSecondary))
            using (var accentBrush = new SolidBrush(pal.AccentBlue))
            using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
            {
                for (int i = 0; i < vis; i++)
                {
                    int revIndex = count - 1 - (scrollOffset + i);
                    if (revIndex < 0) break;
                    var t = turns[revIndex];
                    int ry = rowsStartY + (i * RowHeight);
                    bool isSel = t.Index == selectedTurnIndex;
                    bool isHov = i == hoveredRow;

                    if (isSel)
                    {
                        using (var sb = new SolidBrush(Color.FromArgb(24, pal.AccentBlue)))
                            g.FillRectangle(sb, Pad - 4, ry, (rightEdge - Pad) + 8, RowHeight);
                        inspected = t;
                    }
                    else if (isHov)
                    {
                        using (var hb = new SolidBrush(pal.RowHoverBg))
                            g.FillRectangle(hb, Pad - 4, ry, (rightEdge - Pad) + 8, RowHeight);
                        if (inspected == null) inspected = t;
                    }
                    else if (i % 2 == 1)
                    {
                        using (var alt = new SolidBrush(pal.RowAltBg))
                            g.FillRectangle(alt, Pad - 4, ry, (rightEdge - Pad) + 8, RowHeight);
                    }

                    int ty = ry + 3;
                    g.DrawString("#" + t.Index.ToString(CultureInfo.InvariantCulture), dataFont, isSel ? accentBrush : secBrush, colIndex, ty);

                    string model = MetricFormatter.FormatModelBadge(t.Model, t.Effort);
                    var modelRect = new RectangleF(colModel, ty, colSpeed - colModel - 4, RowHeight - 3);
                    g.DrawString(model.Length > 0 ? model : "—", dataFont, textBrush, modelRect, modelFormat);

                    string speed = t.Tps.HasValue ? t.Tps.Value.ToString("0.0", CultureInfo.InvariantCulture) + " tok/s" : "—";
                    g.DrawString(speed, valueFont, textBrush, colSpeed, ty);
                    g.DrawString(MetricFormatter.FormatSeconds(t.TtftMs), dataFont, textBrush, colFirst, ty);

                    string context = t.ContextInputTokens.HasValue ? MetricFormatter.CompactTokens(t.ContextInputTokens.Value) : "—";
                    string usage = context + " / " + MetricFormatter.CompactTokens(t.ReasoningOutputTokens)
                        + " / " + MetricFormatter.CompactTokens(Math.Max(0, t.OutputTokens - t.ReasoningOutputTokens));
                    g.DrawString(usage, dataFont, textBrush, colTokens, ty);

                    Color cacheColor = t.CacheHitPct.HasValue ? pal.ForTier(MetricFormatter.ClassifyCache(t.CacheHitPct)) : pal.TextMuted;
                    using (var cacheBrush = new SolidBrush(cacheColor))
                        g.DrawString(MetricFormatter.FormatCachePrecise(t.CacheHitPct), valueFont, cacheBrush, rightEdge, ty, rightFmt);
                }
            }

            // Visual draggable scrollbar when turns exceed visible rows
            if (needScroll)
            {
                int trackH = vis * RowHeight;
                scrollTrackRect = new Rectangle(size.Width - Pad - 6, rowsStartY, 6, trackH);
                using (var tp = StripForm.CreateRoundedPath(scrollTrackRect, 3))
                using (var tb = new SolidBrush(pal.TrackBg))
                    g.FillPath(tb, tp);

                int maxScroll = Math.Max(1, count - vis);
                int thumbH = Math.Min(trackH, Math.Max(12, (int)Math.Round(trackH * (vis / (double)count))));
                int thumbTop = rowsStartY + (int)Math.Round((trackH - thumbH) * (scrollOffset / (double)maxScroll));
                scrollThumbRect = new Rectangle(scrollTrackRect.X, thumbTop, 6, thumbH);
                using (var thp = StripForm.CreateRoundedPath(scrollThumbRect, 3))
                using (var thb = new SolidBrush(pal.BorderStrong))
                    g.FillPath(thb, thp);
            }

            // Bottom exact-turn inspector bar (defaults to hovered/selected or latest turn)
            if (inspected == null && selectedTurnIndex > 0)
            {
                for (int i = 0; i < turns.Count; i++)
                {
                    if (turns[i].Index == selectedTurnIndex) { inspected = turns[i]; break; }
                }
            }
            if (inspected == null) inspected = turns[count - 1];

            int inspY = rowsStartY + (vis * RowHeight) + 4;
            var inspRect = new Rectangle(Pad, inspY, size.Width - (Pad * 2), InspectorHeight - 4);
            using (var ip = StripForm.CreateRoundedPath(inspRect, 5))
            using (var ib = new SolidBrush(pal.CardBg))
            using (var ipen = new Pen(pal.Border, 1f))
            {
                g.FillPath(ib, ip);
                g.DrawPath(ipen, ip);
            }

            long toolMs = (inspected.WallDurationMs.HasValue && inspected.WallDurationMs.Value > inspected.ModelActiveMs)
                ? (inspected.WallDurationMs.Value - inspected.ModelActiveMs)
                : 0;
            string detailLine = string.Format("#{0} 精确  总 {1} · 命中 {2} · 推理 {3} · 总耗时 {4} (工具 {5})",
                inspected.Index,
                MetricFormatter.ExactTokens(inspected.TotalTokens),
                MetricFormatter.ExactTokens(inspected.CachedInputTokens),
                MetricFormatter.ExactTokens(inspected.ReasoningOutputTokens),
                MetricFormatter.FormatSeconds(inspected.WallDurationMs),
                MetricFormatter.FormatSeconds(toolMs));
            using (var db = new SolidBrush(pal.TextSecondary))
                g.DrawString(detailLine, subFont, db, inspRect.X + 8, inspRect.Y + 5);
        }
    }
}
