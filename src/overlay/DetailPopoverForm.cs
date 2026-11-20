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

        private const int PopoverWidth = 486;
        private const int Pad = 14;
        private const int HeaderHeight = 36;
        private const int CardGridHeight = 140;
        private const int ContextBarHeight = 46;
        private const int TableHeaderHeight = 46;
        private const int RowHeight = 24;
        private const int MaxVisibleRows = 7;

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

                // Card 2: Cache Hit Rate
                double? cachePct = snap.SessionCacheHitPct;
                string cacheHero = MetricFormatter.FormatCachePrecise(cachePct);
                string cacheSub = snap.HasUsage
                    ? string.Format("命中 {0} / {1}", MetricFormatter.CompactTokens(snap.CachedInputTokens), MetricFormatter.CompactTokens(snap.InputTokens))
                    : "等待会话数据";
                Color cacheColor = cachePct.HasValue ? pal.ForTier(MetricFormatter.ClassifyCache(cachePct)) : pal.TextMuted;
                DrawKpiCard(g, new Rectangle(Pad + cardW + gap, gridTop, cardW, cardH),
                    "缓存命中率", cacheHero, cacheSub, cacheColor, pal, labelFont, heroFont, subFont);

                // Card 3: Latest Turn
                var latest = snap.LatestTurn;
                string turnHero = latest != null ? MetricFormatter.CompactTokens(latest.TotalTokens) : "--";
                string turnSub = latest != null
                    ? string.Format("入 {0} · 出 {1} · 缓存 {2}",
                        MetricFormatter.CompactTokens(latest.InputTokens),
                        MetricFormatter.CompactTokens(latest.OutputTokens),
                        MetricFormatter.FormatCacheCompact(latest.CacheHitPct))
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
            int barX = Pad;
            int barW = width - (Pad * 2);

            string ctxText = snap.ContextWindowTokens > 0
                ? string.Format("上下文  {0} / {1} ({2:0}%)",
                    MetricFormatter.CompactTokens(snap.ContextUsedTokens),
                    MetricFormatter.CompactTokens(snap.ContextWindowTokens),
                    snap.ContextPct ?? 0.0)
                : "上下文  --";

            using (var lblBrush = new SolidBrush(pal.TextSecondary))
                g.DrawString(ctxText, labelFont, lblBrush, barX, y + 6);

            // Right side: 5h & Weekly Quota remaining %
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
                using (var qBrush = new SolidBrush(pal.TextSecondary))
                    g.DrawString(qText, subFont, qBrush, width - Pad - qSz.Width, y + 6);
            }

            // Context progress track
            var trackRect = new Rectangle(barX, y + 26, barW, 7);
            using (var trackPath = StripForm.CreateRoundedPath(trackRect, 3))
            using (var trackBrush = new SolidBrush(pal.TrackBg))
                g.FillPath(trackBrush, trackPath);

            if (snap.ContextPct.HasValue && snap.ContextPct.Value > 0)
            {
                double pct = Math.Max(0, Math.Min(100, snap.ContextPct.Value));
                int fillW = Math.Max(6, (int)Math.Round(barW * pct / 100.0));
                Color fillCol = pct >= 85.0 ? pal.TierDanger : (pct >= 70.0 ? pal.TierWarn : pal.AccentBlue);
                var fillRect = new Rectangle(barX, y + 26, Math.Min(barW, fillW), 7);
                using (var fillPath = StripForm.CreateRoundedPath(fillRect, 3))
                using (var fillBrush = new SolidBrush(fillCol))
                    g.FillPath(fillBrush, fillPath);
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

                    string cacheStr = MetricFormatter.FormatCacheCompact(t.CacheHitPct);
                    Color cacheCol = t.CacheHitPct.HasValue ? pal.ForTier(MetricFormatter.ClassifyCache(t.CacheHitPct)) : pal.TextMuted;
                    using (var cacheBrush = new SolidBrush(cacheCol))
                        g.DrawString(cacheStr, monoBoldFont, cacheBrush, colRight, ty, rightFmt);
                }
            }
        }
    }
}
