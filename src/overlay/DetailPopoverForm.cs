using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

namespace CodexInputUsage
{
    internal sealed class DetailPopoverForm : Form
    {
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;
        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;

        private const int PopoverWidth = 496;
        private const int Pad = 14;
        private const int HeaderHeight = 36;
        private const int CardGridHeight = 140;
        private const int ContextBarHeight = 186;
        private const int TableHeaderHeight = 46;
        private const int RowHeight = 24;
        private const int MaxVisibleRows = 6;

        private ThemePalette _palette = ThemePalette.Light;
        private SessionMetricsSnapshot _snapshot = new SessionMetricsSnapshot();
        private Rectangle _closeButtonRect = Rectangle.Empty;
        private bool _closeHovered;
        private int _scrollOffset;

        internal DetailPopoverForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
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

        internal void UpdateData(SessionMetricsSnapshot snapshot, ThemePalette palette, Rectangle anchorStrip)
        {
            _snapshot = snapshot ?? new SessionMetricsSnapshot();
            _palette = palette ?? ThemePalette.Light;

            int totalRows = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
            int maxScroll = Math.Max(0, totalRows - MaxVisibleRows);
            if (_scrollOffset > maxScroll) _scrollOffset = maxScroll;

            PositionAbove(anchorStrip);
            Invalidate();
        }

        internal void PositionAbove(Rectangle anchor)
        {
            if (anchor.IsEmpty) return;
            int rows = _snapshot.Turns != null ? _snapshot.Turns.Count : 0;
            int visibleRows = Math.Max(1, Math.Min(MaxVisibleRows, rows));
            int desiredHeight = HeaderHeight + CardGridHeight + ContextBarHeight + TableHeaderHeight + (visibleRows * RowHeight) + 16;

            var work = Screen.FromPoint(anchor.Location).WorkingArea;
            int maxHeight = Math.Max(220, anchor.Top - work.Top - 10);
            int height = Math.Min(desiredHeight, maxHeight);

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
            int maxScroll = Math.Max(0, totalRows - MaxVisibleRows);
            if (maxScroll <= 0) return;
            int delta = e.Delta > 0 ? -1 : 1;
            int next = Math.Max(0, Math.Min(maxScroll, _scrollOffset + delta));
            if (next != _scrollOffset)
            {
                _scrollOffset = next;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool overClose = _closeButtonRect.Contains(e.Location);
            if (overClose != _closeHovered)
            {
                _closeHovered = overClose;
                Cursor = overClose ? Cursors.Hand : Cursors.Default;
                Invalidate(_closeButtonRect);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_closeHovered)
            {
                _closeHovered = false;
                Cursor = Cursors.Default;
                Invalidate(_closeButtonRect);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && _closeButtonRect.Contains(e.Location))
            {
                Hide();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            RenderCard(e.Graphics, ClientSize, _snapshot, _palette, _scrollOffset, _closeHovered, out _closeButtonRect);
        }

        internal static Bitmap RenderPreviewBitmap(SessionMetricsSnapshot snap, ThemePalette palette)
        {
            int rows = snap != null && snap.Turns != null ? snap.Turns.Count : 0;
            int visibleRows = Math.Max(1, Math.Min(MaxVisibleRows, rows));
            int height = HeaderHeight + CardGridHeight + ContextBarHeight + TableHeaderHeight + (visibleRows * RowHeight) + 16;
            var bmp = new Bitmap(PopoverWidth, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Rectangle dummy;
                RenderCard(g, new Size(PopoverWidth, height), snap ?? new SessionMetricsSnapshot(), palette, 0, false, out dummy);
            }
            return bmp;
        }

        private static void RenderCard(
            Graphics g,
            Size size,
            SessionMetricsSnapshot snap,
            ThemePalette pal,
            int scrollOffset,
            bool closeHovered,
            out Rectangle closeRect)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            g.Clear(pal.SurfaceBg);
            using (var borderPen = new Pen(pal.BorderStrong, 1f))
                g.DrawRectangle(borderPen, 0, 0, size.Width - 1, size.Height - 1);

            using (var titleFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point))
            using (var badgeFont = new Font("Segoe UI", 7.8f, FontStyle.Bold, GraphicsUnit.Point))
            using (var labelFont = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular, GraphicsUnit.Point))
            using (var heroFont = new Font("Segoe UI Semibold", 13.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (var subFont = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point))
            using (var tableHeadFont = new Font("Microsoft YaHei UI", 7.8f, FontStyle.Bold, GraphicsUnit.Point))
            using (var monoFont = new Font("Consolas", 8.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (var monoBoldFont = new Font("Consolas", 8.5f, FontStyle.Bold, GraphicsUnit.Point))
            {
                // 1. Header bar
                int y = 0;
                DrawHeader(g, size.Width, y, snap, pal, titleFont, badgeFont, subFont, closeHovered, out closeRect);
                y += HeaderHeight;
                using (var sep = new Pen(pal.Border, 1f))
                    g.DrawLine(sep, 1, y, size.Width - 2, y);

                // 2. 2x2 KPI Cards (Antigravity usage-monitor style)
                int gridTop = y + 10;
                int gap = 8;
                int cardW = (size.Width - (Pad * 2) - gap) / 2;
                int cardH = 58;

                // Card 1: TPS
                string tpsVal = MetricFormatter.FormatTps(snap.DisplayTps, snap.TpsIsHeld);
                string tpsHero = tpsVal == "--" ? "-- tok/s" : (tpsVal + " tok/s");
                string tpsSub = string.Format("首字 {0}  ·  活跃 {1}",
                    MetricFormatter.FormatSeconds(snap.LatestTtftMs),
                    MetricFormatter.FormatSeconds(snap.LatestModelActiveMs));
                Color tpsColor = snap.DisplayTps.HasValue ? pal.ForTier(MetricFormatter.ClassifySpeed(snap.DisplayTps)) : pal.TextMuted;
                DrawKpiCard(g, new Rectangle(Pad, gridTop, cardW, cardH),
                    snap.IsStreaming ? "输出速度 (生成中)" : "输出速度 (TPS)",
                    tpsHero, tpsSub, tpsColor, pal, labelFont, heroFont, subFont);

                // Card 2: Cache Hit Rate (Turn / Session — 2 decimal places in popover)
                var latest = snap.LatestTurn;
                double? turnCachePct = latest != null ? latest.CacheHitPct : null;
                double? sessCachePct = snap.SessionCacheHitPct;
                string cacheHero;
                if (turnCachePct.HasValue && sessCachePct.HasValue)
                    cacheHero = string.Format("{0} / {1}", MetricFormatter.FormatCacheTwoDecimals(turnCachePct), MetricFormatter.FormatCacheTwoDecimals(sessCachePct));
                else
                    cacheHero = MetricFormatter.FormatCacheTwoDecimals(turnCachePct ?? sessCachePct);
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

                // 3. Context Window Bar + 5h/Weekly Quota Badges
                DrawContextAndQuotaStrip(g, size.Width, y, snap, pal, labelFont, subFont, monoBoldFont);
                y += ContextBarHeight;

                using (var sep = new Pen(pal.Border, 1f))
                    g.DrawLine(sep, Pad, y, size.Width - Pad, y);

                // 4. Per-Turn Table
                DrawTurnTable(g, size, y, snap, pal, scrollOffset, labelFont, tableHeadFont, monoFont, monoBoldFont);
            }
        }

        private static void DrawHeader(
            Graphics g, int width, int y, SessionMetricsSnapshot snap, ThemePalette pal,
            Font titleFont, Font badgeFont, Font subFont, bool closeHovered, out Rectangle closeRect)
        {
            using (var titleBrush = new SolidBrush(pal.TextPrimary))
                g.DrawString("Codex 用量监控", titleFont, titleBrush, Pad, y + 9);

            // Binding badge
            string src = string.IsNullOrWhiteSpace(snap.BindingSource) ? "等待" : snap.BindingSource;
            Color badgeColor;
            string badgeText;
            if (src.StartsWith("IPC+", StringComparison.Ordinal) || src == "IPC")
            {
                badgeColor = pal.TierGood;
                badgeText = src == "IPC" ? "IPC 绑定" : src;
            }
            else if (src == "待确认" || src == "多窗口歧义")
            {
                badgeColor = pal.TierWarn;
                badgeText = "会话待确认";
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
            SizeF bSz = g.MeasureString(badgeText, badgeFont);
            var badgeRect = new Rectangle(Pad + 114, y + 9, (int)Math.Ceiling(bSz.Width) + 12, 18);
            using (var path = StripForm.CreateRoundedPath(badgeRect, 4))
            using (var fill = new SolidBrush(Color.FromArgb(26, badgeColor)))
            using (var pen = new Pen(Color.FromArgb(95, badgeColor), 1f))
            using (var textBrush = new SolidBrush(badgeColor))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
                g.DrawString(badgeText, badgeFont, textBrush, badgeRect.X + 6, badgeRect.Y + 2);
            }

            // Short Thread ID
            if (!string.IsNullOrWhiteSpace(snap.ThreadId))
            {
                string tid = MetricFormatter.ShortThreadId(snap.ThreadId);
                using (var tidBrush = new SolidBrush(pal.TextMuted))
                    g.DrawString(tid, subFont, tidBrush, badgeRect.Right + 8, y + 10);
            }

            // Close button
            closeRect = new Rectangle(width - Pad - 22, y + 7, 22, 22);
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
                g.DrawString(sub, subFont, subBrush, rect.X + 10, rect.Y + 41);
            }
        }

        private static void DrawContextAndQuotaStrip(
            Graphics g, int width, int y, SessionMetricsSnapshot snap, ThemePalette pal,
            Font labelFont, Font subFont, Font monoBoldFont)
        {
            var cardRect = new Rectangle(Pad, y + 4, width - (Pad * 2), ContextBarHeight - 10);
            using (var cardPath = StripForm.CreateRoundedPath(cardRect, 7))
            using (var cardFill = new SolidBrush(pal.CardBg))
            using (var cardPen = new Pen(pal.Border, 1f))
            {
                g.FillPath(cardFill, cardPath);
                g.DrawPath(cardPen, cardPath);
            }

            int innerX = cardRect.X + 12;
            int innerW = cardRect.Width - 24;

            // Row 1: Stacked-layers icon + bold "上下文窗口" on left; right-aligned "271.7k / 475.0k (57.2%)"
            DrawLayersIcon(g, innerX, cardRect.Y + 9, pal.TextPrimary);
            using (var hdrFont = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Bold, GraphicsUnit.Point))
            using (var hdrBrush = new SolidBrush(pal.TextPrimary))
                g.DrawString("上下文窗口", hdrFont, hdrBrush, innerX + 18, cardRect.Y + 8);

            string rightUsage = snap.ContextWindowTokens > 0
                ? string.Format("{0} / {1} ({2:0.0}%)",
                    MetricFormatter.CompactTokensLower(snap.ContextUsedTokens),
                    MetricFormatter.CompactTokensLower(snap.ContextWindowTokens),
                    snap.ContextPct ?? 0.0)
                : (snap.ContextUsedTokens > 0 ? (MetricFormatter.CompactTokensLower(snap.ContextUsedTokens) + " / --") : "-- / --");
            using (var rightFont = new Font("Segoe UI", 8.8f, FontStyle.Regular, GraphicsUnit.Point))
            using (var rightBrush = new SolidBrush(pal.TextSecondary))
            {
                SizeF rSz = g.MeasureString(rightUsage, rightFont);
                g.DrawString(rightUsage, rightFont, rightBrush, cardRect.Right - 12 - rSz.Width, cardRect.Y + 9);
            }

            // Row 2: Gapped 10-channel rounded segmented bar (matches Antigravity usage-monitor screenshot!)
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
                        if (segW < 4) segW = 4; // small non-zero channels stay cleanly visible as pill dots
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

            // Row 3: 2-column x 5-row channel breakdown grid (colored rounded square + label + right-aligned bold count)
            int gridY = cardRect.Y + 47;
            int colGap = 18;
            int colW = (innerW - colGap) / 2;
            int itemRowH = 20;

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

            // Compact footer inside card: remaining space + 5h/weekly quota
            int footY = cardRect.Bottom - 20;
            using (var sep = new Pen(pal.Border, 1f))
                g.DrawLine(sep, innerX, footY - 3, cardRect.Right - 12, footY - 3);

            if (snap.ContextWindowTokens > 0)
            {
                double pct = snap.ContextPct ?? 0.0;
                Color remCol = pct >= 85.0 ? pal.TierDanger : (pct >= 70.0 ? pal.TierWarn : pal.TextMuted);
                using (var rb = new SolidBrush(remCol))
                    g.DrawString("剩余可用 " + MetricFormatter.CompactTokensLower(snap.ContextRemainingTokens), subFont, rb, innerX, footY);
            }

            var quotaParts = new List<string>();
            if (snap.PrimaryUsedPct.HasValue)
            {
                double rem = Math.Max(0, Math.Min(100, 100.0 - snap.PrimaryUsedPct.Value));
                quotaParts.Add(string.Format("5h 余 {0:0}%", rem));
            }
            if (snap.SecondaryUsedPct.HasValue)
            {
                double rem = Math.Max(0, Math.Min(100, 100.0 - snap.SecondaryUsedPct.Value));
                quotaParts.Add(string.Format("周余 {0:0}%", rem));
            }
            if (quotaParts.Count > 0)
            {
                string qText = string.Join("  ·  ", quotaParts.ToArray());
                SizeF qSz = g.MeasureString(qText, subFont);
                using (var qb = new SolidBrush(pal.TextSecondary))
                    g.DrawString(qText, subFont, qb, cardRect.Right - 12 - qSz.Width, footY);
            }
        }

        private static void DrawLayersIcon(Graphics g, int x, int y, Color color)
        {
            using (var pen = new Pen(color, 1.3f))
            {
                pen.LineJoin = LineJoin.Round;
                // Top diamond
                g.DrawPolygon(pen, new PointF[]
                {
                    new PointF(x + 6f, y + 1f),
                    new PointF(x + 12f, y + 4f),
                    new PointF(x + 6f, y + 7f),
                    new PointF(x + 0f, y + 4f)
                });
                // Middle chevron
                g.DrawLines(pen, new PointF[]
                {
                    new PointF(x + 0f, y + 7.5f),
                    new PointF(x + 6f, y + 10.5f),
                    new PointF(x + 12f, y + 7.5f)
                });
                // Bottom chevron
                g.DrawLines(pen, new PointF[]
                {
                    new PointF(x + 0f, y + 10.5f),
                    new PointF(x + 6f, y + 13.5f),
                    new PointF(x + 12f, y + 10.5f)
                });
            }
        }

        private static void DrawTurnTable(
            Graphics g, Size size, int topY, SessionMetricsSnapshot snap, ThemePalette pal, int scrollOffset,
            Font labelFont, Font headFont, Font monoFont, Font monoBoldFont)
        {
            var turns = snap.Turns ?? new List<TurnSummary>();
            int count = turns.Count;

            using (var titleBrush = new SolidBrush(pal.TextPrimary))
            using (var mutedBrush = new SolidBrush(pal.TextMuted))
            {
                g.DrawString(string.Format("逐轮明细 ({0})", count), headFont, titleBrush, Pad, topY + 8);
                if (count > MaxVisibleRows)
                {
                    string hint = string.Format("最新在前 · 滚轮翻页 ({0}-{1}/{2})",
                        scrollOffset + 1, Math.Min(count, scrollOffset + MaxVisibleRows), count);
                    SizeF hSz = g.MeasureString(hint, labelFont);
                    g.DrawString(hint, labelFont, mutedBrush, size.Width - Pad - hSz.Width, topY + 7);
                }
            }

            // Column X positions inside [Pad .. Width - Pad]
            int colIdx = Pad;
            int colTime = Pad + 32;
            int colTps = Pad + 86;
            int colTtft = Pad + 148;
            int colIn = Pad + 206;
            int colOut = Pad + 286;
            int colTot = Pad + 344;
            int colRight = size.Width - Pad;

            int headY = topY + 26;
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
                g.DrawString("缓存", headFont, headBrush, colRight, headY, rightFmt);
            }

            int rowsStartY = topY + TableHeaderHeight;
            if (count == 0)
            {
                using (var emptyBrush = new SolidBrush(pal.TextMuted))
                    g.DrawString("当前会话暂无逐轮用量记录", labelFont, emptyBrush, Pad, rowsStartY + 4);
                return;
            }

            int visible = Math.Min(MaxVisibleRows, Math.Max(1, (size.Height - rowsStartY - 8) / RowHeight));
            using (var textBrush = new SolidBrush(pal.TextPrimary))
            using (var secBrush = new SolidBrush(pal.TextSecondary))
            using (var rightFmt = new StringFormat { Alignment = StringAlignment.Far })
            {
                for (int i = 0; i < visible; i++)
                {
                    int revIndex = count - 1 - (scrollOffset + i);
                    if (revIndex < 0) break;
                    var t = turns[revIndex];
                    int ry = rowsStartY + (i * RowHeight);

                    if (i % 2 == 1)
                    {
                        using (var alt = new SolidBrush(pal.RowAltBg))
                            g.FillRectangle(alt, Pad - 4, ry, size.Width - (Pad * 2) + 8, RowHeight);
                    }

                    int ty = ry + 4;
                    g.DrawString(t.Index.ToString(CultureInfo.InvariantCulture), monoFont, secBrush, colIdx, ty);
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
                        g.DrawString(cacheStr, monoBoldFont, cacheBrush, colRight, ty, rightFmt);
                }
            }
        }
    }
}
