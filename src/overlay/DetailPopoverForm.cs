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
        private const int CardGridHeight = 138;
        private const int QuotaSectionHeight = 72;
        private const int ContextBarHeight = 172;
        private const int TableHeaderHeight = 44;
        private const int RowHeight = 24;
        private const int InspectorHeight = 28;
        private const int MaxVisibleRows = 6;

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
        private Rectangle _anchorStrip = Rectangle.Empty;
        private Rectangle _closeButtonRect = Rectangle.Empty;
        private Rectangle _pinButtonRect = Rectangle.Empty;
        private Rectangle _tableRowsRect = Rectangle.Empty;
        private Rectangle _scrollTrackRect = Rectangle.Empty;
        private Rectangle _scrollThumbRect = Rectangle.Empty;

        private bool _closeHovered;
        private bool _pinHovered;
        private bool _isPinned;
        private int _scrollOffset;
        private int _visibleRows = MaxVisibleRows;
        private int _hoveredRowIndex = -1;
        private int _selectedTurnIndex = -1;
        private bool _draggingScroll;
        private int _dragStartY;
        private int _dragStartOffset;

        private readonly LowLevelProc _mouseProc;
        private readonly LowLevelProc _keyProc;
        private IntPtr _mouseHook = IntPtr.Zero;
        private IntPtr _keyHook = IntPtr.Zero;

        internal bool IsPinned { get { return _isPinned; } }

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
            _snapshot = snapshot ?? new SessionMetricsSnapshot();
            _palette = palette ?? ThemePalette.Light;
            _anchorStrip = anchorStrip;

            int totalRows = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
            int maxScroll = Math.Max(0, totalRows - _visibleRows);
            if (_scrollOffset > maxScroll) _scrollOffset = maxScroll;

            PositionAbove(anchorStrip);
            Invalidate();
        }

        internal void PositionAbove(Rectangle anchor)
        {
            if (anchor.IsEmpty) return;
            _anchorStrip = anchor;

            int rows = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
            int targetRows = Math.Max(1, Math.Min(MaxVisibleRows, rows));
            int chromeHeight = HeaderHeight + CardGridHeight + QuotaSectionHeight + ContextBarHeight + TableHeaderHeight + InspectorHeight + 12;
            int desiredHeight = chromeHeight + (targetRows * RowHeight);

            var work = Screen.FromPoint(anchor.Location).WorkingArea;
            int maxHeight = Math.Max(320, anchor.Top - work.Top - 10);
            int height = Math.Min(desiredHeight, maxHeight);
            _visibleRows = Math.Max(1, Math.Min(MaxVisibleRows, (height - chromeHeight) / RowHeight));

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
            if (e.Button != MouseButtons.Left) return;

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
            int nextHoverRow = -1;
            if (!_tableRowsRect.IsEmpty && _tableRowsRect.Contains(e.Location))
            {
                int r = (e.Y - _tableRowsRect.Top) / RowHeight;
                int count = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
                if (r >= 0 && r < _visibleRows && (_scrollOffset + r) < count)
                    nextHoverRow = r;
            }

            bool needRepaint = (overClose != _closeHovered) || (overPin != _pinHovered) || (nextHoverRow != _hoveredRowIndex);
            _closeHovered = overClose;
            _pinHovered = overPin;
            _hoveredRowIndex = nextHoverRow;
            bool hand = overClose || overPin || nextHoverRow >= 0 || (!_scrollThumbRect.IsEmpty && _scrollThumbRect.Contains(e.Location));
            Cursor = hand ? Cursors.Hand : Cursors.Default;
            if (needRepaint) Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_draggingScroll) return;
            if (_closeHovered || _pinHovered || _hoveredRowIndex >= 0)
            {
                _closeHovered = false;
                _pinHovered = false;
                _hoveredRowIndex = -1;
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

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            RenderCard(
                e.Graphics, ClientSize, _snapshot, _palette,
                _scrollOffset, _visibleRows, _hoveredRowIndex, _selectedTurnIndex,
                _isPinned, _pinHovered, _closeHovered,
                out _pinButtonRect, out _closeButtonRect,
                out _tableRowsRect, out _scrollTrackRect, out _scrollThumbRect);
        }

        internal static Bitmap RenderPreviewBitmap(SessionMetricsSnapshot snap, ThemePalette palette)
        {
            int rows = snap != null && snap.Turns != null ? snap.Turns.Count : 0;
            int vis = Math.Max(1, Math.Min(MaxVisibleRows, rows));
            int height = HeaderHeight + CardGridHeight + QuotaSectionHeight + ContextBarHeight + TableHeaderHeight + (vis * RowHeight) + InspectorHeight + 12;
            var bmp = new Bitmap(PopoverWidth, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Rectangle r1, r2, r3, r4, r5;
                RenderCard(g, new Size(PopoverWidth, height), snap ?? new SessionMetricsSnapshot(), palette,
                    0, vis, 0, -1, false, false, false,
                    out r1, out r2, out r3, out r4, out r5);
            }
            return bmp;
        }

        private static void RenderCard(
            Graphics g,
            Size size,
            SessionMetricsSnapshot snap,
            ThemePalette pal,
            int scrollOffset,
            int visibleRows,
            int hoveredRow,
            int selectedTurnIndex,
            bool isPinned,
            bool pinHovered,
            bool closeHovered,
            out Rectangle pinRect,
            out Rectangle closeRect,
            out Rectangle tableRowsRect,
            out Rectangle scrollTrackRect,
            out Rectangle scrollThumbRect)
        {
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

                // 1. Header bar (Title + IPC badge + Model/Effort pill + Pin + Close)
                DrawHeader(g, size.Width, y, snap, pal, titleFont, badgeFont, subFont, isPinned, pinHovered, closeHovered, out pinRect, out closeRect);
                y += HeaderHeight;
                using (var sep = new Pen(pal.Border, 1f))
                    g.DrawLine(sep, 1, y, size.Width - 2, y);

                // 2. 2x2 KPI Cards
                int gridTop = y + 10;
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

                // 5. Per-Turn Table with Visual Scrollbar + Interactive Row Inspector
                DrawTurnTable(g, size, y, snap, pal, scrollOffset, visibleRows, hoveredRow, selectedTurnIndex,
                    labelFont, tableHeadFont, subFont, monoFont, monoBoldFont,
                    out tableRowsRect, out scrollTrackRect, out scrollThumbRect);
            }
        }

        private static void DrawHeader(
            Graphics g, int width, int y, SessionMetricsSnapshot snap, ThemePalette pal,
            Font titleFont, Font badgeFont, Font subFont,
            bool isPinned, bool pinHovered, bool closeHovered,
            out Rectangle pinRect, out Rectangle closeRect)
        {
            using (var titleBrush = new SolidBrush(pal.TextPrimary))
                g.DrawString("Codex 用量监控", titleFont, titleBrush, Pad, y + 10);

            int curX = Pad + 114;

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
            int scrollOffset, int visibleRows, int hoveredRow, int selectedTurnIndex,
            Font labelFont, Font headFont, Font subFont, Font monoFont, Font monoBoldFont,
            out Rectangle tableRowsRect, out Rectangle scrollTrackRect, out Rectangle scrollThumbRect)
        {
            tableRowsRect = Rectangle.Empty;
            scrollTrackRect = Rectangle.Empty;
            scrollThumbRect = Rectangle.Empty;

            var turns = snap.Turns ?? new List<TurnSummary>();
            int count = turns.Count;
            bool needScroll = count > visibleRows;
            int scrollReserve = needScroll ? 12 : 0;

            using (var titleBrush = new SolidBrush(pal.TextPrimary))
            using (var mutedBrush = new SolidBrush(pal.TextMuted))
            {
                g.DrawString(string.Format("逐轮明细 ({0})", count), headFont, titleBrush, Pad, topY + 7);
                string hint = needScroll
                    ? string.Format("悬停/点击行看精确明细 · {0}-{1}/{2}", scrollOffset + 1, Math.Min(count, scrollOffset + visibleRows), count)
                    : "悬停或点击行查看精确明细";
                SizeF hSz = g.MeasureString(hint, labelFont);
                g.DrawString(hint, labelFont, mutedBrush, size.Width - Pad - hSz.Width, topY + 6);
            }

            int rightEdge = size.Width - Pad - scrollReserve;
            int colIdx = Pad;
            int colTime = Pad + 30;
            int colTps = Pad + 84;
            int colTtft = Pad + 144;
            int colIn = Pad + 204;
            int colOut = Pad + 284;
            int colTot = Pad + 344;

            int headY = topY + 25;
            using (var headBrush = new SolidBrush(pal.TextSecondary))
            using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
            {
                g.DrawString("#", headFont, headBrush, colIdx, headY);
                g.DrawString("时间", headFont, headBrush, colTime, headY);
                g.DrawString("速度", headFont, headBrush, colTps + 48, headY, rightFmt);
                g.DrawString("首字", headFont, headBrush, colTtft + 44, headY, rightFmt);
                g.DrawString("输入", headFont, headBrush, colIn + 64, headY, rightFmt);
                g.DrawString("输出", headFont, headBrush, colOut + 46, headY, rightFmt);
                g.DrawString("总计", headFont, headBrush, colTot + 52, headY, rightFmt);
                g.DrawString("缓存", headFont, headBrush, rightEdge, headY, rightFmt);
            }

            int rowsStartY = topY + TableHeaderHeight;
            if (count == 0)
            {
                using (var emptyBrush = new SolidBrush(pal.TextMuted))
                    g.DrawString("当前会话暂无逐轮用量记录", labelFont, emptyBrush, Pad, rowsStartY + 4);
                return;
            }

            int vis = Math.Min(visibleRows, count);
            tableRowsRect = new Rectangle(Pad - 4, rowsStartY, (rightEdge - Pad) + 8, vis * RowHeight);
            TurnSummary inspected = null;

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

                    int ty = ry + 4;
                    g.DrawString(t.Index.ToString(CultureInfo.InvariantCulture), monoFont, isSel ? accentBrush : secBrush, colIdx, ty);
                    string timeStr = t.CompletedAtLocal.HasValue
                        ? t.CompletedAtLocal.Value.ToString("HH:mm", CultureInfo.InvariantCulture)
                        : (t.Complete ? "--" : "进行中");
                    g.DrawString(timeStr, monoFont, secBrush, colTime, ty);

                    string tpsStr = t.Tps.HasValue ? (MetricFormatter.FormatTps(t.Tps, false) + "/s") : "--";
                    Color tpsCol = t.Tps.HasValue ? pal.ForTier(MetricFormatter.ClassifySpeed(t.Tps)) : pal.TextMuted;
                    using (var tpsBrush = new SolidBrush(tpsCol))
                        g.DrawString(tpsStr, monoBoldFont, tpsBrush, colTps + 48, ty, rightFmt);

                    string ttftStr = MetricFormatter.FormatSeconds(t.TtftMs);
                    Color ttftCol = t.TtftMs.HasValue ? pal.ForTier(MetricFormatter.ClassifyTtft(t.TtftMs)) : pal.TextMuted;
                    using (var ttftBrush = new SolidBrush(ttftCol))
                        g.DrawString(ttftStr, monoFont, ttftBrush, colTtft + 44, ty, rightFmt);

                    g.DrawString(MetricFormatter.CompactTokens(t.InputTokens), monoFont, textBrush, colIn + 64, ty, rightFmt);
                    g.DrawString(MetricFormatter.CompactTokens(t.OutputTokens), monoFont, textBrush, colOut + 46, ty, rightFmt);
                    g.DrawString(MetricFormatter.CompactTokens(t.TotalTokens), monoBoldFont, textBrush, colTot + 52, ty, rightFmt);

                    string cacheStr = MetricFormatter.FormatCacheTwoDecimals(t.CacheHitPct);
                    Color cacheCol = t.CacheHitPct.HasValue ? pal.ForTier(MetricFormatter.ClassifyCache(t.CacheHitPct)) : pal.TextMuted;
                    using (var cacheBrush = new SolidBrush(cacheCol))
                        g.DrawString(cacheStr, monoBoldFont, cacheBrush, rightEdge, ty, rightFmt);
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
                int thumbH = Math.Max(22, (int)Math.Round(trackH * (vis / (double)count)));
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
