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

    internal sealed partial class DetailPopoverForm : Form
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

        private const int PopoverWidth = 540;
        private const int PopoverHeight = 608;
        private const int CornerRadius = 12;
        private const int Pad = 14;
        private const int HeaderHeight = 38;
        private const int TabBarHeight = 34;
        private const int CardGridHeight = 126;
        private const int QuotaSectionHeight = 62;
        private const int ContextBarHeight = 140;
        private const int TableHeaderHeight = 44;
        private const int RowHeight = 24;
        private const int InspectorHeight = 28;
        private const int SessionVisibleRows = 5;

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
        private PopoverTab _activeTab = PopoverTab.Session;
        private Rectangle _anchorStrip = Rectangle.Empty;
        private float _hostDpiScale = 1f;
        private float _dpiScale = 1f;
        private int _logicalHeight;
        private int _bodyScroll;
        private int _maxBodyScroll;
        private Rectangle _closeButtonRect = Rectangle.Empty;
        private Rectangle _pinButtonRect = Rectangle.Empty;
        private Rectangle _tabSessionRect = Rectangle.Empty;
        private Rectangle _tabHistoryRect = Rectangle.Empty;
        private Rectangle _tabTurnsRect = Rectangle.Empty;
        private Rectangle _tableRowsRect = Rectangle.Empty;
        private Rectangle _scrollTrackRect = Rectangle.Empty;
        private Rectangle _scrollThumbRect = Rectangle.Empty;

        private bool _closeHovered;
        private bool _pinHovered;
        private int _hoveredTab = -1;
        private bool _isPinned;
        private int _scrollOffset;
        private int _visibleRows = SessionVisibleRows;
        private int _hoveredRowIndex = -1;
        private int _selectedTurnIndex = -1;
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
            // PositionAbove uses the host's DPI, including when this window crosses first.
            if (m.Msg == OverlayDpi.WmDpiChanged) return;
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
                    Point local = new Point(pt.X - Left, pt.Y - Top);
                    bool inside = Bounds.Contains(pt) && (Region == null || Region.IsVisible(local));
                    if (!inside && !_anchorStrip.Contains(pt))
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

        internal void UpdateData(SessionMetricsSnapshot snapshot, ThemePalette palette, Rectangle anchorStrip, float dpiScale)
        {
            if (!string.Equals(_snapshot.ThreadId, snapshot == null ? null : snapshot.ThreadId, StringComparison.OrdinalIgnoreCase))
            {
                _scrollOffset = 0;
                if (_activeTab != PopoverTab.History) _bodyScroll = 0;
                _turnsView.SelectedModel = null;
                _turnsView.Mouse = new Point(-1, -1);
                _selectedTurnIndex = -1;
                _hoveredRowIndex = -1;
            }
            _snapshot = snapshot ?? new SessionMetricsSnapshot();
            _palette = palette ?? ThemePalette.Light;
            _hostDpiScale = dpiScale;
            _anchorStrip = anchorStrip;

            PositionAbove(anchorStrip);
            Invalidate();
        }

        internal void UpdateHistoryData(UsageHistoryData data)
        {
            _usage.Update(data, DateTime.Now);
            if (_activeTab == PopoverTab.History && Visible)
            {
                PositionAbove(_anchorStrip);
                Invalidate();
            }
        }

        internal void PositionAbove(Rectangle anchor)
        {
            if (anchor.IsEmpty) return;
            _anchorStrip = anchor;

            var work = Screen.FromPoint(anchor.Location).WorkingArea;
            // Very narrow work areas may not fit a full-width panel at the host scale.
            float scale = Math.Min(_hostDpiScale, work.Width / (PopoverWidth + 16f));
            int rows = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
            Rectangle bounds = CalculateBounds(anchor, work, scale);
            int logicalHeight = Math.Min(PopoverHeight, (int)Math.Floor((bounds.Height + 0.49f) / scale));
            _visibleRows = VisibleTurnRows(_activeTab, rows, logicalHeight);
            _scrollOffset = _activeTab == PopoverTab.Turns ? Math.Min(_scrollOffset, Math.Max(0, rows - _visibleRows)) : 0;
            int contentHeight = _activeTab == PopoverTab.History
                ? HistoryHeight(_usage)
                : ShowingSummary ? TurnsSummaryHeight(_turnsView.Models(_snapshot).Length)
                : TurnTableChromeHeight(_activeTab) + _visibleRows * RowHeight;
            _maxBodyScroll = Math.Max(0, contentHeight - logicalHeight);
            _bodyScroll = Math.Min(_bodyScroll, _maxBodyScroll);

            bool changedLayout = _dpiScale != scale || _logicalHeight != logicalHeight;
            if (_dpiScale != scale && _draggingScroll)
            {
                _draggingScroll = false;
                Capture = false;
            }
            _dpiScale = scale;
            _logicalHeight = logicalHeight;

            bool changedSize = Size != bounds.Size;
            if (Bounds != bounds) Bounds = bounds;
            if (changedLayout || changedSize || Region == null)
            {
                using (var frame = StripForm.CreateRoundedPath(new Rectangle(Point.Empty, bounds.Size),
                    OverlayDpi.Pixels(CornerRadius, scale)))
                    Region = new Region(frame);
            }
            if (IsHandleCreated && Visible)
            {
                NativeWin32.SetWindowPos(
                    Handle, NativeWin32.HWND_TOPMOST,
                    bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    NativeWin32.SWP_NOACTIVATE | NativeWin32.SWP_SHOWWINDOW);
            }
            if (changedLayout) Invalidate();
        }

        internal static Rectangle CalculateBounds(Rectangle anchor, Rectangle work, float scale)
        {
            int gap = Math.Min(OverlayDpi.Pixels(8, scale), (work.Height - 1) / 2);
            int width = Math.Min(work.Width, OverlayDpi.Pixels(PopoverWidth, scale));
            int maxHeight = work.Height - 2 * gap;
            int above = anchor.Top - work.Top - 2 * gap;
            int below = work.Bottom - anchor.Bottom - 2 * gap;
            int desired = OverlayDpi.Pixels(PopoverHeight, scale);
            bool openAbove = desired <= above || above >= below;
            int sideHeight = openAbove ? above : below;
            int height = Math.Min(Math.Min(desired, maxHeight), Math.Max(OverlayDpi.Pixels(320, scale), sideHeight));
            int margin = Math.Min(gap, (work.Width - width) / 2);
            int x = Math.Max(work.Left + margin, Math.Min(anchor.Right - width, work.Right - margin - width));
            int y = openAbove ? anchor.Top - height - gap : anchor.Bottom + gap;
            y = Math.Max(work.Top + gap, Math.Min(y, work.Bottom - gap - height));
            return new Rectangle(x, y, width, height);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int bodyTop = _activeTab == PopoverTab.Turns ? TurnsBodyTop : HeaderHeight + TabBarHeight;
            Point location = OverlayDpi.ToLogical(e.Location, _dpiScale);
            if (e.Delta == 0 || location.Y < bodyTop) return;
            if (_activeTab == PopoverTab.History && HandleUsageWheel(location, e.Delta)) return;
            if (_activeTab != PopoverTab.Turns || ShowingSummary)
            {
                int scroll = Math.Max(0, Math.Min(_maxBodyScroll, _bodyScroll - e.Delta / 3));
                if (scroll != _bodyScroll)
                {
                    _bodyScroll = scroll;
                    _turnsView.Mouse = new Point(-1, -1);
                    _usage.Mouse = new Point(-1, -1);
                    _hoveredRowIndex = -1;
                    Invalidate();
                }
                return;
            }
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
            Point location = OverlayDpi.ToLogical(e.Location, _dpiScale);
            if (e.Button != MouseButtons.Left || _activeTab != PopoverTab.Turns || ShowingSummary) return;

            int totalRows = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
            int maxScroll = Math.Max(0, totalRows - _visibleRows);
            if (maxScroll > 0 && !_scrollTrackRect.IsEmpty)
            {
                if (_scrollThumbRect.Contains(location))
                {
                    _draggingScroll = true;
                    _dragStartY = location.Y;
                    _dragStartOffset = _scrollOffset;
                    Capture = true;
                    return;
                }
                if (_scrollTrackRect.Contains(location))
                {
                    double ratio = (double)(location.Y - _scrollTrackRect.Top) / Math.Max(1, _scrollTrackRect.Height);
                    _scrollOffset = Math.Max(0, Math.Min(maxScroll, (int)Math.Round(ratio * maxScroll)));
                    Invalidate();
                    return;
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point location = OverlayDpi.ToLogical(e.Location, _dpiScale);

            if (_draggingScroll)
            {
                int totalRows = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
                int maxScroll = Math.Max(0, totalRows - _visibleRows);
                int usableTrack = Math.Max(1, _scrollTrackRect.Height - _scrollThumbRect.Height);
                int deltaPx = location.Y - _dragStartY;
                int next = _dragStartOffset + (int)Math.Round(deltaPx * (double)maxScroll / usableTrack);
                next = Math.Max(0, Math.Min(maxScroll, next));
                if (next != _scrollOffset)
                {
                    _scrollOffset = next;
                    Invalidate();
                }
                return;
            }

            bool turnsChanged = _activeTab == PopoverTab.Turns && _turnsView.Mouse != location;
            if (turnsChanged) _turnsView.Mouse = location;
            bool overTurnsControl = _activeTab == PopoverTab.Turns && _turnsView.Buttons.Exists(rect => rect.Contains(location));
            bool overClose = _closeButtonRect.Contains(location);
            bool overPin = _pinButtonRect.Contains(location);
            int nextHoverTab = -1;
            if (_tabSessionRect.Contains(location)) nextHoverTab = 0;
            else if (_tabHistoryRect.Contains(location)) nextHoverTab = 1;
            else if (_tabTurnsRect.Contains(location)) nextHoverTab = 2;

            Point bodyLocation = new Point(location.X, location.Y + _bodyScroll);
            bool overBody = location.Y >= HeaderHeight + TabBarHeight;
            int nextHoverRow = -1;
            bool usageChanged = _activeTab == PopoverTab.History && _usage.Mouse != location;
            if (usageChanged) _usage.Mouse = location;
            var usageHit = _activeTab == PopoverTab.History ? _usage.Hit(location) : null;
            if (_activeTab != PopoverTab.History)
            {
                if (overBody && _tableRowsRect.Contains(bodyLocation))
                {
                    int r = (bodyLocation.Y - _tableRowsRect.Top) / RowHeight;
                    int count = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
                    if (r >= 0 && r < _visibleRows && (_scrollOffset + r) < count)
                        nextHoverRow = r;
                }
            }
            bool needRepaint = turnsChanged || usageChanged || (overClose != _closeHovered)
                || (overPin != _pinHovered)
                || (nextHoverTab != _hoveredTab)
                || (nextHoverRow != _hoveredRowIndex);
            _closeHovered = overClose;
            _pinHovered = overPin;
            _hoveredTab = nextHoverTab;
            _hoveredRowIndex = nextHoverRow;
            bool hand = overTurnsControl || overClose || overPin || nextHoverTab >= 0 || nextHoverRow >= 0 || (usageHit != null && usageHit.Click != null)
                || (_activeTab == PopoverTab.Turns && !_scrollThumbRect.IsEmpty && _scrollThumbRect.Contains(location));
            Cursor = hand ? Cursors.Hand : Cursors.Default;
            if (needRepaint) Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_draggingScroll) return;
            if (_activeTab == PopoverTab.Turns || _activeTab == PopoverTab.History || _closeHovered || _pinHovered || _hoveredTab >= 0 || _hoveredRowIndex >= 0)
            {
                _turnsView.Mouse = new Point(-1, -1);
                _usage.Mouse = new Point(-1, -1);
                _closeHovered = false;
                _pinHovered = false;
                _hoveredTab = -1;
                _hoveredRowIndex = -1;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Point location = OverlayDpi.ToLogical(e.Location, _dpiScale);
            if (_draggingScroll)
            {
                _draggingScroll = false;
                Capture = false;
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            if (_closeButtonRect.Contains(location))
            {
                Hide();
                return;
            }

            if (_pinButtonRect.Contains(location))
            {
                _isPinned = !_isPinned;
                SyncDismissHooks();
                Invalidate();
                return;
            }

            PopoverTab? targetTab = null;
            if (_tabSessionRect.Contains(location)) targetTab = PopoverTab.Session;
            else if (_tabHistoryRect.Contains(location)) targetTab = PopoverTab.History;
            else if (_tabTurnsRect.Contains(location)) targetTab = PopoverTab.Turns;
            if (targetTab.HasValue)
            {
                if (_activeTab != targetTab.Value)
                {
                    _activeTab = targetTab.Value;
                    _usage.Mouse = new Point(-1, -1);
                    _turnsView.Mouse = new Point(-1, -1);
                    _bodyScroll = 0;
                    _hoveredRowIndex = -1;
                    _selectedTurnIndex = -1;
                    if (_activeTab == PopoverTab.History && HistoryTabActivated != null)
                        HistoryTabActivated(this, EventArgs.Empty);
                    PositionAbove(_anchorStrip);
                    Invalidate();
                }
                return;
            }

            if (_activeTab == PopoverTab.Turns)
            {
                int button = _turnsView.Buttons.FindIndex(rect => rect.Contains(location));
                if (button >= 0)
                {
                    bool wasSummary = ShowingSummary;
                    _turnsView.Actions[button]();
                    if (wasSummary != ShowingSummary)
                    {
                        _bodyScroll = 0;
                        _scrollOffset = 0;
                        _hoveredRowIndex = -1;
                        _selectedTurnIndex = -1;
                        _turnsView.Mouse = new Point(-1, -1);
                        PositionAbove(_anchorStrip);
                    }
                    Invalidate();
                    return;
                }
                if (ShowingSummary || location.Y < TurnsBodyTop) return;
            }
            if (_activeTab == PopoverTab.History)
            {
                ClickUsage(location);
                return;
            }
            if (location.Y < HeaderHeight + TabBarHeight) return;
            location.Offset(0, _bodyScroll);
            if (_tableRowsRect.Contains(location))
            {
                int row = (location.Y - _tableRowsRect.Top) / RowHeight;
                int count = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
                int index = count - 1 - (_scrollOffset + row);
                if (row < _visibleRows && index >= 0 && index < count)
                {
                    int turnNum = _snapshot.Turns[index].Index;
                    _selectedTurnIndex = _selectedTurnIndex == turnNum ? -1 : turnNum;
                    Invalidate();
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_logicalHeight <= 0) return;
            var state = e.Graphics.Save();
            e.Graphics.ScaleTransform(_dpiScale, _dpiScale);
            RenderCard(
                e.Graphics, new Size(PopoverWidth, _logicalHeight), _snapshot, _usage, _activeTab, _palette,
                _bodyScroll, _scrollOffset, _visibleRows, _hoveredRowIndex, _selectedTurnIndex,
                _hoveredTab,
                _isPinned, _pinHovered, _closeHovered, _turnsView,
                out _pinButtonRect, out _closeButtonRect,
                out _tabSessionRect, out _tabHistoryRect, out _tabTurnsRect,
                out _tableRowsRect, out _scrollTrackRect, out _scrollThumbRect);
            e.Graphics.Restore(state);
        }

        private static int TurnTableChromeHeight(PopoverTab tab)
        {
            int chrome = HeaderHeight + TabBarHeight + TableHeaderHeight + InspectorHeight + 12;
            return tab == PopoverTab.Session ? chrome + CardGridHeight + QuotaSectionHeight + ContextBarHeight : chrome + TurnsToolbarHeight - 24;
        }

        private static int VisibleTurnRows(PopoverTab tab, int rows, int logicalHeight)
        {
            if (tab == PopoverTab.History) return 0;
            int limit = tab == PopoverTab.Turns ? (logicalHeight - TurnTableChromeHeight(tab)) / RowHeight : SessionVisibleRows;
            return Math.Max(1, Math.Min(limit, rows));
        }

        internal static Bitmap RenderPreviewBitmap(SessionMetricsSnapshot snap, ThemePalette palette, PopoverTab tab = PopoverTab.Session, float scale = 1f, bool turnsSummary = false)
        {
            int rows = snap != null && snap.Turns != null ? snap.Turns.Count : 0;
            int vis = VisibleTurnRows(tab, rows, PopoverHeight);
            int height = PopoverHeight;
            var bmp = new Bitmap(OverlayDpi.Pixels(PopoverWidth, scale), OverlayDpi.Pixels(height, scale), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.ScaleTransform(scale, scale);
                Rectangle r1, r2, r3, r4, r5, r6, r7, r8;
                RenderCard(g, new Size(PopoverWidth, height), snap ?? new SessionMetricsSnapshot(), new UsageView(),
                    tab, palette,
                    0, 0, vis, 0, -1, -1, false, false, false, new TurnsView { Summary = turnsSummary },
                    out r1, out r2, out r3, out r4, out r5, out r6, out r7, out r8);
            }
            return bmp;
        }

        internal static Bitmap RenderHistoryPreviewBitmap(SessionMetricsSnapshot snap, UsageHistoryData data, DateTime now, ThemePalette palette, float scale = 1f, UsagePeriod period = UsagePeriod.Week, int scroll = 0, Point? mouse = null)
        {
            var view = new UsageView();
            view.Update(data, now);
            view.Select(period);
            if (mouse.HasValue) view.Mouse = mouse.Value;
            int height = PopoverHeight;
            var bmp = new Bitmap(OverlayDpi.Pixels(PopoverWidth, scale), OverlayDpi.Pixels(height, scale), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.ScaleTransform(scale, scale);
                Rectangle r1, r2, r3, r4, r5, r6, r7, r8;
                RenderCard(g, new Size(PopoverWidth, height), snap ?? new SessionMetricsSnapshot(), view,
                    PopoverTab.History, palette,
                    scroll, 0, 0, -1, -1, -1, false, false, false, new TurnsView(),
                    out r1, out r2, out r3, out r4, out r5, out r6, out r7, out r8);
            }
            return bmp;
        }

        private static void RenderCard(
            Graphics g,
            Size size,
            SessionMetricsSnapshot snap,
            UsageView usage,
            PopoverTab activeTab,
            ThemePalette pal,
            int bodyScroll,
            int scrollOffset,
            int visibleRows,
            int hoveredRow,
            int selectedTurnIndex,
            int hoveredTab,
            bool isPinned,
            bool pinHovered,
            bool closeHovered,
            TurnsView turnsView,
            out Rectangle pinRect,
            out Rectangle closeRect,
            out Rectangle tabSessionRect,
            out Rectangle tabHistoryRect,
            out Rectangle tabTurnsRect,
            out Rectangle tableRowsRect,
            out Rectangle scrollTrackRect,
            out Rectangle scrollThumbRect)
        {
            tableRowsRect = Rectangle.Empty;
            scrollTrackRect = Rectangle.Empty;
            scrollThumbRect = Rectangle.Empty;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var frame = StripForm.CreateRoundedPath(new Rectangle(0, 0, size.Width - 1, size.Height - 1), CornerRadius))
            using (var surface = new SolidBrush(pal.SurfaceBg))
            using (var borderPen = new Pen(pal.BorderStrong, 1f))
            {
                g.FillPath(surface, frame);
                g.DrawPath(borderPen, frame);
            }

            using (var titleFont = OverlayDpi.FontAt96Dpi("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
            using (var badgeFont = OverlayDpi.FontAt96Dpi("Segoe UI", 7.8f, FontStyle.Bold))
            using (var labelFont = OverlayDpi.FontAt96Dpi("Microsoft YaHei UI", 8f, FontStyle.Regular))
            using (var heroFont = OverlayDpi.FontAt96Dpi("Segoe UI Semibold", 13.0f, FontStyle.Regular))
            using (var subFont = OverlayDpi.FontAt96Dpi("Segoe UI", 7.9f, FontStyle.Regular))
            using (var tableHeadFont = OverlayDpi.FontAt96Dpi("Microsoft YaHei UI", 7.8f, FontStyle.Bold))
            using (var monoFont = OverlayDpi.FontAt96Dpi("Consolas", 8.4f, FontStyle.Regular))
            using (var monoBoldFont = OverlayDpi.FontAt96Dpi("Consolas", 8.4f, FontStyle.Bold))
            {
                int y = 0;

                // 1. Header bar
                DrawHeader(g, size.Width, y, snap, usage.Snapshot, activeTab, pal, titleFont, badgeFont, subFont, isPinned, pinHovered, closeHovered, out pinRect, out closeRect);
                y += HeaderHeight;
                using (var sep = new Pen(pal.Border, 1f))
                    g.DrawLine(sep, 1, y, size.Width - 2, y);

                // 2. Segmented Tab Bar
                DrawTabBar(g, size.Width, y, activeTab, hoveredTab, pal, tableHeadFont, labelFont, out tabSessionRect, out tabHistoryRect, out tabTurnsRect);
                y += TabBarHeight;
                turnsView.Buttons.Clear();
                turnsView.Actions.Clear();
                if (activeTab == PopoverTab.Turns)
                {
                    if (turnsView.Summary) turnsView.Models(snap);
                    DrawTurnsToolbar(g, size, snap, pal, turnsView, scrollOffset, visibleRows, tableHeadFont, labelFont);
                    y += TurnsToolbarHeight;
                    if (turnsView.Summary)
                    {
                        g.SetClip(new Rectangle(1, y, size.Width - 2, Math.Max(0, size.Height - y - 2)), CombineMode.Intersect);
                        g.TranslateTransform(0, -bodyScroll);
                        RenderTurnsSummary(g, size, snap, pal, turnsView, bodyScroll, tableHeadFont, labelFont, subFont);
                        return;
                    }
                }
                if (bodyScroll > 0 || activeTab == PopoverTab.History)
                {
                    g.SetClip(new Rectangle(1, y, size.Width - 2, Math.Max(0, size.Height - y - 1)), CombineMode.Intersect);
                    g.TranslateTransform(0, -bodyScroll);
                }

                if (activeTab == PopoverTab.Turns)
                {
                    DrawTurnTable(g, size, y, snap, pal, true, scrollOffset, visibleRows, hoveredRow, selectedTurnIndex,
                        tableHeadFont, subFont, out tableRowsRect, out scrollTrackRect, out scrollThumbRect);
                    return;
                }

                if (activeTab == PopoverTab.History)
                {
                    RenderUsageBody(g, size, y, usage, bodyScroll, pal,
                        tableHeadFont, labelFont, subFont, heroFont, monoFont, monoBoldFont);
                    return;
                }

                // 3. Session View: 2x2 KPI Cards
                int gridTop = y + 5;
                int gap = 8;
                int rowGap = 6;
                int cardW = (size.Width - (Pad * 2) - gap) / 2;
                int cardH = 54;

                // Card 1: TPS + Session Avg TPS
                string tpsVal = MetricFormatter.FormatTps(snap.DisplayTps, snap.TpsIsHeld);
                string tpsHero = tpsVal == "--" ? "-- tok/s" : (tpsVal + " tok/s");
                string avgHero = snap.SessionAvgTps.HasValue
                    ? snap.SessionAvgTps.Value.ToString("0.0", CultureInfo.InvariantCulture) + " tok/s"
                    : "—";
                string tpsSub = string.Format("首字 {0} · 活跃 {1}",
                    MetricFormatter.FormatSeconds(snap.LatestTtftMs),
                    MetricFormatter.FormatSeconds(snap.LatestModelActiveMs));
                Color tpsColor = snap.DisplayTps.HasValue ? pal.ForTier(MetricFormatter.ClassifySpeed(snap.DisplayTps)) : pal.TextMuted;
                DrawKpiCard(g, new Rectangle(Pad, gridTop, cardW, cardH),
                    snap.IsStreaming ? "输出速度 · 生成中" : "输出速度",
                    tpsHero, tpsSub, tpsColor, pal, labelFont, heroFont, subFont);
                int speedDivider = Pad + cardW / 2;
                using (var divider = new Pen(pal.Border))
                using (var caption = new SolidBrush(pal.TextSecondary))
                using (var average = new SolidBrush(snap.SessionAvgTps.HasValue ? pal.AccentBlue : pal.TextMuted))
                {
                    g.DrawLine(divider, speedDivider, gridTop + 9, speedDivider, gridTop + 37);
                    g.DrawString("会话平均速度", labelFont, caption, speedDivider + 8, gridTop + 6);
                    g.DrawString(avgHero, heroFont, average, speedDivider + 7, gridTop + 19);
                }

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
                using (var cacheHeroFont = OverlayDpi.FontAt96Dpi("Segoe UI Semibold", 11.8f, FontStyle.Regular))
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
                DrawKpiCard(g, new Rectangle(Pad, gridTop + cardH + rowGap, cardW, cardH),
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
                DrawKpiCard(g, new Rectangle(Pad + cardW + gap, gridTop + cardH + rowGap, cardW, cardH),
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
                curX = DrawSmallPill(g, curX, y + 10, history.PeriodLabel + " · " + history.Total.Sessions + " 会话", badgeFont, pal.AccentBlue);
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
                string modelBadge = string.IsNullOrWhiteSpace(snap.ModelName) && snap.LatestTurn != null
                    ? MetricFormatter.FormatModelBadge(snap.LatestTurn.Model, snap.LatestTurn.Effort)
                    : MetricFormatter.FormatModelBadge(snap.ModelName, snap.ReasoningEffort);
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

            DrawSingleTab(g, tabSessionRect, "当前会话", PopoverTab.Session, activeTab == PopoverTab.Session, hoveredTab == 0, pal, boldFont, regularFont);
            DrawSingleTab(g, tabTurnsRect, "调用明细", PopoverTab.Turns, activeTab == PopoverTab.Turns, hoveredTab == 2, pal, boldFont, regularFont);
            DrawSingleTab(g, tabHistoryRect, "用量统计", PopoverTab.History, activeTab == PopoverTab.History, hoveredTab == 1, pal, boldFont, regularFont);
        }

        private static void DrawSingleTab(
            Graphics g, Rectangle rect, string text, PopoverTab tab, bool active, bool hovered,
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

            var font = active ? boldFont : regularFont;
            Color color = active || hovered ? pal.TextPrimary : pal.TextSecondary;
            using (var fmt = new StringFormat { LineAlignment = StringAlignment.Center })
            using (var brush = new SolidBrush(color))
            using (var pen = new Pen(color, 1.15f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                const int iconSlot = 17;
                int x = rect.X + (rect.Width - iconSlot - (int)Math.Ceiling(g.MeasureString(text, font).Width)) / 2;
                int y = rect.Y + (rect.Height - 12) / 2;
                switch (tab)
                {
                    case PopoverTab.Session:
                        g.DrawPolygon(pen, new[] { new Point(x + 1, y + 1), new Point(x + 11, y + 1), new Point(x + 11, y + 8),
                            new Point(x + 6, y + 8), new Point(x + 3, y + 11), new Point(x + 3, y + 8), new Point(x + 1, y + 8) });
                        break;
                    case PopoverTab.Turns:
                        for (int line = y + 2; line <= y + 10; line += 4)
                        {
                            g.DrawLine(pen, x + 1, line, x + 2, line);
                            g.DrawLine(pen, x + 5, line, x + 11, line);
                        }
                        break;
                    case PopoverTab.History:
                        g.DrawLine(pen, x + 1, y + 11, x + 11, y + 11);
                        g.DrawLine(pen, x + 2, y + 8, x + 2, y + 6);
                        g.DrawLine(pen, x + 6, y + 8, x + 6, y + 2);
                        g.DrawLine(pen, x + 10, y + 8, x + 10, y + 4);
                        break;
                }
                g.DrawString(text, font, brush, new RectangleF(x + iconSlot, rect.Y, rect.Right - x - iconSlot, rect.Height), fmt);
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
                g.DrawString("高消耗会话 · Top 5", headFont, titleBrush, ix, cardRect.Y + 9);
            using (var hintBrush = new SolidBrush(pal.TextMuted))
            using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
                g.DrawString("按所选时段统计", subFont, hintBrush, cardRect.Right - 12, cardRect.Y + 9, rightFmt);

            var top = hist.TopThreads;
            if (top.Length == 0)
            {
                using (var emptyBrush = new SolidBrush(pal.TextMuted))
                    g.DrawString("所选时段暂无活跃会话", labelFont, emptyBrush, ix, cardRect.Y + 38);
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
                for (int i = 0; i < top.Length; i++)
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

                    string shortModel = MetricFormatter.FormatModelName(item.ModelName);
                    var modelRect = new Rectangle(cardRect.Right - 234, ry + 3, 116, 16);
                    g.DrawString(shortModel, subFont, secBrush, modelRect, ellipFmt);

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

            DateTime utcNow = DateTime.UtcNow;
            DrawSingleQuotaCard(
                g, new Rectangle(Pad, y + 2, cardW, cardH),
                "5h 额度余量", snap.PrimaryUsedPct, snap.PrimaryResetsAt, false, utcNow,
                snap.QuotaIsAccountLive ? "实时" : null, pal, labelFont, subFont, monoBoldFont);

            string creditNote = (snap.HasCredits && !string.IsNullOrWhiteSpace(snap.CreditsBalance) && snap.CreditsBalance != "0")
                ? ("余额 $" + snap.CreditsBalance)
                : (snap.UnlimitedCredits ? "无限额度" : null);
            DrawSingleQuotaCard(
                g, new Rectangle(Pad + cardW + gap, y + 2, cardW, cardH),
                "周额度余量", snap.SecondaryUsedPct, snap.SecondaryResetsAt, true, utcNow,
                creditNote, pal, labelFont, subFont, monoBoldFont);
        }

        private static void DrawSingleQuotaCard(
            Graphics g, Rectangle rect, string title, double? usedPct, long? resetsAt, bool isWeekly, DateTime utcNow,
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

            double? remaining = MetricFormatter.ResolveQuotaRemainingPct(usedPct, resetsAt, utcNow);
            bool hasVal = remaining.HasValue;
            double rem = remaining.GetValueOrDefault();
            string countdown = hasVal ? MetricFormatter.FormatResetCountdown(resetsAt, isWeekly, utcNow) : string.Empty;

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
                g.DrawString(footLeft, subFont, mBrush, ix, rect.Y + 36);

            if (!string.IsNullOrEmpty(rightTag))
            {
                using (var tagBrush = new SolidBrush(pal.TextSecondary))
                using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
                    g.DrawString(rightTag, subFont, tagBrush, rect.Right - 10, rect.Y + 36, rightFmt);
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
            using (var hdrFont = OverlayDpi.FontAt96Dpi("Microsoft YaHei UI", 9.0f, FontStyle.Bold))
            using (var hdrBrush = new SolidBrush(pal.TextPrimary))
                g.DrawString("上下文窗口", hdrFont, hdrBrush, innerX + 18, cardRect.Y + 8);

            string rightUsage = snap.ContextWindowTokens > 0
                ? string.Format("{0} / {1} ({2:0.0}%)  ·  剩余 {3}",
                    MetricFormatter.CompactTokensLower(snap.ContextUsedTokens),
                    MetricFormatter.CompactTokensLower(snap.ContextWindowTokens),
                    snap.ContextPct ?? 0.0,
                    MetricFormatter.CompactTokensLower(snap.ContextRemainingTokens))
                : (snap.ContextUsedTokens > 0 ? (MetricFormatter.CompactTokensLower(snap.ContextUsedTokens) + " / --") : "-- / --");
            using (var rightFont = OverlayDpi.FontAt96Dpi("Segoe UI", 8.4f, FontStyle.Regular))
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

            int gridY = cardRect.Y + 43;
            int colGap = 18;
            int colW = (innerW - colGap) / 2;
            int itemRowH = 17;

            using (var chLabelFont = OverlayDpi.FontAt96Dpi("Microsoft YaHei UI", 8.3f, FontStyle.Regular))
            using (var chValFont = OverlayDpi.FontAt96Dpi("Segoe UI Semibold", 8.6f, FontStyle.Regular))
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
            int colSpeed = Pad + 136;
            int colFirst = Pad + 226;
            int colTokens = Pad + 273;
            int colTotal = rightEdge - 53;

            using (var titleBrush = new SolidBrush(pal.TextPrimary))
            using (var mutedBrush = new SolidBrush(pal.TextSecondary))
            using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
            {
                if (!detail)
                    g.DrawString("最近五轮", headFont, titleBrush, Pad, topY + 7);

                int headY = topY + (detail ? 1 : 25);
                g.DrawString("步序", headFont, mutedBrush, colIndex, headY);
                g.DrawString("模型", headFont, mutedBrush, colModel, headY);
                g.DrawString("速度", headFont, mutedBrush, colSpeed, headY);
                g.DrawString("首字", headFont, mutedBrush, colFirst, headY);
                g.DrawString("上下文 / 思考 / 正文", headFont, mutedBrush, colTokens, headY);
                g.DrawString("Token 消耗", headFont, mutedBrush, colTotal, headY, rightFmt);
                g.DrawString("缓存", headFont, mutedBrush, rightEdge, headY, rightFmt);
            }

            int rowsStartY = topY + TableHeaderHeight - (detail ? 24 : 0);
            if (count == 0)
            {
                using (var emptyBrush = new SolidBrush(pal.TextMuted))
                    g.DrawString("当前会话暂无逐轮用量记录", headFont, emptyBrush, Pad, rowsStartY + 4);
                return;
            }

            int vis = Math.Min(visibleRows, count);
            tableRowsRect = new Rectangle(Pad - 4, rowsStartY, (rightEdge - Pad) + 8, vis * RowHeight);
            TurnSummary inspected = null;

            using (var dataFont = OverlayDpi.FontAt96Dpi("Segoe UI", 9.1f, FontStyle.Regular))
            using (var valueFont = OverlayDpi.FontAt96Dpi("Segoe UI", 9.1f, FontStyle.Bold))
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
                    g.DrawString(MetricFormatter.CompactTokens(t.TotalTokens), valueFont, textBrush, colTotal, ty, rightFmt);

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
