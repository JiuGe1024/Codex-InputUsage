using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace CodexInputUsage
{
    internal sealed partial class DetailPopoverForm
    {
        private const int UsageOverviewHeight = 170;
        private const int UsageCustomHeight = 34;
        private const int UsageChartHeight = 186;
        private const int UsageCardGap = 8;
        private static readonly string[] PeriodLabels = { "今天", "1天", "7天", "30天", "全部", "自定义" };
        private readonly UsageView _usage = new UsageView();

        private sealed class UsageView
        {
            internal UsagePeriod Period = UsagePeriod.Week;
            internal Point Mouse = new Point(-1, -1);
            internal Rectangle Viewport;
            internal DateTime DraftStart, DraftEnd, Start, End;
            internal int StartField = 2, EndField = 2;
            internal List<UsageHit> Hits = new List<UsageHit>();
            private UsageHistoryData _data;
            private DateTime _now;

            internal HistoryStatsSnapshot Snapshot = new HistoryStatsSnapshot { Period = UsagePeriod.Week };

            internal void Update(UsageHistoryData data, DateTime now)
            {
                _data = data;
                _now = now;
                Recalculate();
            }

            internal bool Select(UsagePeriod period)
            {
                if (period == Period) return false;
                Period = period;
                if (period == UsagePeriod.Custom)
                {
                    if (Start == default(DateTime))
                    {
                        var hour = UsageHistoryData.FloorHour(_now == default(DateTime) ? DateTime.Now : _now);
                        Start = hour.AddHours(-24);
                        End = hour;
                    }
                    DraftStart = Start;
                    DraftEnd = End;
                }
                Recalculate();
                return true;
            }

            internal bool Apply()
            {
                Start = DraftStart < DraftEnd ? DraftStart : DraftEnd;
                End = DraftStart > DraftEnd ? DraftStart : DraftEnd;
                DraftStart = Start;
                DraftEnd = End;
                Recalculate();
                return false;
            }

            private void Recalculate()
            {
                if (_data == null)
                {
                    Snapshot = new HistoryStatsSnapshot { Period = Period };
                    return;
                }
                Snapshot = _data.Build(Period, _now, Start, End);
            }

            internal void Step(int endpoint, int field, int delta)
            {
                if (endpoint == 0) StartField = field;
                else EndField = field;
                DateTime value = endpoint == 0 ? DraftStart : DraftEnd;
                DateTime next;
                try
                {
                    switch (field)
                    {
                        case 0: next = value.AddYears(delta); break;
                        case 1: next = value.AddMonths(delta); break;
                        case 2: next = value.AddDays(delta); break;
                        default: next = value.AddHours(delta); break;
                    }
                }
                catch (ArgumentOutOfRangeException) { return; }
                if (next.Year < 1 || next.Year >= 9999) return;
                if (endpoint == 0) DraftStart = next;
                else DraftEnd = next;
            }

            internal UsageHit Hit(Point point)
            {
                return Viewport.Contains(point) ? Hits.Find(hit => hit.Bounds.Contains(point)) : null;
            }
        }

        private sealed class UsageHit
        {
            internal Rectangle Bounds;
            internal Func<bool> Click;
            internal int Endpoint = -1, Field;
        }

        internal static bool CheckUsageInteraction(UsageHistoryData data, DateTime now, float scale)
        {
            using (var form = new DetailPopoverForm { _logicalHeight = PopoverHeight, _dpiScale = scale, _activeTab = PopoverTab.History })
            using (var bitmap = new Bitmap(OverlayDpi.Pixels(PopoverWidth, scale), OverlayDpi.Pixels(PopoverHeight, scale)))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var view = form._usage;
                view.Update(data, now);
                var paint = new PaintEventArgs(graphics, new Rectangle(Point.Empty, bitmap.Size));
                Action<int, int> click = (x, y) =>
                {
                    form.OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, OverlayDpi.Pixels(x, scale), OverlayDpi.Pixels(y, scale), 0));
                    form.OnPaint(paint);
                };
                Action<int, int, int> wheel = (x, y, delta) =>
                {
                    form.OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, OverlayDpi.Pixels(x, scale), OverlayDpi.Pixels(y, scale), delta));
                    form.OnPaint(paint);
                };
                form.OnPaint(paint);
                click(304, 95);
                if (view.Period != UsagePeriod.Day || view.Snapshot.Points.Count != 25) return false;
                click(397, 95);
                if (view.Period != UsagePeriod.Month || view.Snapshot.Points.Count != 30) return false;
                click(444, 95);
                if (view.Period != UsagePeriod.All || view.Snapshot.Total.Tokens != 890) return false;
                click(490, 95);
                if (view.Period != UsagePeriod.Custom || view.Snapshot.Points.Count != 25) return false;
                DateTime start = view.DraftStart;
                DateTime end = view.DraftEnd;
                click(158, 124);
                click(224, 124);
                click(204, 124);
                if (view.StartField != 3 || view.DraftStart != start) return false;
                wheel(155, 124, 120);
                if (form._bodyScroll != 0 || view.DraftStart != start.AddHours(1) || view.Start != start) return false;
                wheel(224, 124, -120);
                wheel(224, 124, 120);
                if (form._bodyScroll != 0 || view.DraftStart != start.AddHours(1)) return false;
                wheel(355, 124, -120);
                if (form._bodyScroll != 0 || view.DraftEnd != end.AddDays(-1)) return false;
                click(481, 123);
                if (view.Start != end.AddDays(-1) || view.End != start.AddHours(1) || view.Snapshot.Points.Count != 2) return false;
                click(351, 95);
                click(490, 95);
                if (view.DraftStart != end.AddDays(-1) || view.DraftEnd != start.AddHours(1)) return false;
                wheel(480, 300, -120);
                return form._bodyScroll > 0;
            }
        }

        private static int HistoryHeight(UsageView view)
        {
            return HeaderHeight + TabBarHeight + 5 + UsageOverviewHeight +
                (view.Period == UsagePeriod.Custom ? UsageCustomHeight : 0) + UsageChartHeight +
                UsageModelsHeight(view.Snapshot) + HistTopThreadsHeight + UsageCardGap * 3 + 8;
        }

        private static int UsageModelsHeight(HistoryStatsSnapshot snapshot)
        {
            return 34 + 59 * Math.Max(1, snapshot.Models.Length);
        }

        private bool HandleUsageWheel(Point location, int delta)
        {
            var hit = _usage.Hit(location);
            if (hit == null || hit.Endpoint < 0) return false;
            _usage.Step(hit.Endpoint, hit.Field, (delta > 0 ? 1 : -1) * Math.Max(1, Math.Abs(delta) / 120));
            Invalidate();
            return true;
        }

        private void ClickUsage(Point location)
        {
            var hit = _usage.Hit(location);
            if (hit == null || hit.Click == null) return;
            if (hit.Click()) _bodyScroll = 0;
            _maxBodyScroll = Math.Max(0, HistoryHeight(_usage) - _logicalHeight);
            _bodyScroll = Math.Min(_bodyScroll, _maxBodyScroll);
            Invalidate();
        }

        private static void RenderUsageBody(Graphics g, Size size, int top, UsageView view, int scroll, ThemePalette pal,
            Font head, Font label, Font sub, Font hero, Font mono, Font monoBold)
        {
            var snap = view.Snapshot;
            view.Hits.Clear();
            view.Viewport = new Rectangle(0, top, size.Width, Math.Max(0, size.Height - top - 2));
            var mouse = view.Viewport.Contains(view.Mouse) ? new Point(view.Mouse.X, view.Mouse.Y + scroll) : new Point(-1, -1);
            int width = size.Width - Pad * 2;
            int y = top + 5;
            var overview = new Rectangle(Pad, y, width, UsageOverviewHeight + (view.Period == UsagePeriod.Custom ? UsageCustomHeight : 0));
            DrawUsageOverview(g, overview, view, scroll, mouse, pal, head, label, sub, hero);
            y = overview.Bottom + UsageCardGap;
            var visibleBody = view.Viewport;
            visibleBody.Offset(0, scroll);
            DrawUsageTrend(g, new Rectangle(Pad, y, width, UsageChartHeight), visibleBody, snap, mouse, pal, head, label, sub, hero);
            y += UsageChartHeight + UsageCardGap;
            int modelHeight = UsageModelsHeight(snap);
            DrawUsageModels(g, new Rectangle(Pad, y, width, modelHeight), snap, pal, head, label, sub);
            y += modelHeight + UsageCardGap;
            DrawTopThreadsCard(g, size.Width, y, snap, pal, label, head, sub, mono, monoBold);
        }

        private static void DrawUsageOverview(Graphics g, Rectangle area, UsageView view, int scroll, Point mouse,
            ThemePalette pal, Font head, Font label, Font sub, Font hero)
        {
            var snap = view.Snapshot;
            var total = snap.Total;
            var colors = UsageColors(pal);
            DrawSummaryCard(g, area, pal, pal.Border);
            DrawSegments(g, new Rectangle(area.Right - 290, area.Top + 7, 280, 24), PeriodLabels, (int)view.Period,
                mouse, pal, label, (i, rect) => RegisterUsageHit(view, rect, scroll, () => view.Select((UsagePeriod)i)));
            using (var primary = new SolidBrush(pal.TextPrimary))
            using (var secondary = new SolidBrush(pal.TextSecondary))
            using (var sep = new Pen(pal.Border))
            {
                g.DrawString("用量总览", head, primary, area.X + 11, area.Y + 11);
                string range = view.Period == UsagePeriod.All ? "全部时间" : snap.Points.Count == 0 ? "" :
                    snap.Start.ToString("MM/dd") + "–" + snap.End.AddTicks(-1).ToString("MM/dd");
                g.DrawString(range, sub, secondary, area.X + 68, area.Y + 11);
                int gridTop = area.Y + 34;
                if (view.Period == UsagePeriod.Custom)
                {
                    DrawCustomRange(g, new Rectangle(area.X + 12, gridTop, area.Width - 24, 29), view, scroll, mouse, pal, label);
                    gridTop += UsageCustomHeight;
                }

                long average = total.Tokens / Math.Max(1, snap.ElapsedBuckets);
                string count = UsageCount(total);
                string coverage = Coverage(total);
                string cacheSub = coverage.Length > 0 ? coverage : total.CacheHitPct.HasValue ? "命中 " + Tokens(total.CachedInputTokens) : "暂无缓存明细";
                string grain = new[] { "时", "日", "周", "月" }[(int)snap.Grain];
                var titles = new[] { "总 Token", grain + "均消耗", "缓存命中率", "新输入", "缓存读取", "输出用量" };
                var values = new[] { Tokens(total.Tokens), Tokens(average), Percent(total.CacheHitPct),
                    Detailed(total, total.UncachedInputTokens), Detailed(total, total.CachedInputTokens),
                    Detailed(total, total.OutputTokens + total.ReasoningTokens) };
                var hints = new[] { count, "峰值 " + Tokens(snap.Peak), cacheSub, "未命中缓存输入", "缓存复用读取",
                    total.DetailedThreads == 0 ? "暂无输出明细" : "回复 " + Tokens(total.OutputTokens) + " · 思考 " + Tokens(total.ReasoningTokens) };
                var shares = new[] { total.UncachedInputTokens, total.CachedInputTokens, total.OutputTokens + total.ReasoningTokens };
                var accents = new[] { pal.AccentBlue, pal.TextPrimary, pal.ForTier(MetricFormatter.ClassifyCache(total.CacheHitPct)),
                    colors[0], colors[1], colors[3] };
                int gridLeft = area.X + 11, cellWidth = (area.Width - 22) / 3;
                for (int col = 1; col < 3; col++)
                    g.DrawLine(sep, gridLeft + col * cellWidth, gridTop + 4, gridLeft + col * cellWidth, gridTop + 117);
                g.DrawLine(sep, gridLeft, gridTop + 61, area.Right - 11, gridTop + 61);
                for (int i = 0; i < titles.Length; i++)
                {
                    int x = gridLeft + (i % 3) * cellWidth + 6;
                    int y = gridTop + (i / 3) * 62;
                    using (var accent = new SolidBrush(accents[i]))
                    using (var right = new StringFormat { Alignment = StringAlignment.Far })
                    {
                        if (i >= 2) g.FillEllipse(accent, x, y + 10, 6, 6);
                        g.DrawString(titles[i], label, secondary, x + (i >= 2 ? 9 : 0), y + 4);
                        g.DrawString(values[i], hero, accent, x - 1, y + 18);
                        g.DrawString(hints[i], sub, secondary, x, y + 40);
                        if (i >= 3 && total.DetailedTokens > 0)
                            g.DrawString(Percent(shares[i - 3] * 100.0 / total.DetailedTokens), sub, secondary, x + cellWidth - 12, y + 4, right);
                    }
                }
            }
            DrawUsageComposition(g, new Rectangle(area.X + 12, area.Bottom - 13, area.Width - 24, 6), Breakdown(total), colors, pal);
        }

        private static void RegisterUsageHit(UsageView view, Rectangle rect, int scroll, Func<bool> click, int endpoint = -1, int field = 0)
        {
            rect.Offset(0, -scroll);
            view.Hits.Add(new UsageHit { Bounds = rect, Click = click, Endpoint = endpoint, Field = field });
        }

        private static void DrawCustomRange(Graphics g, Rectangle area, UsageView view, int scroll, Point mouse, ThemePalette pal, Font font)
        {
            DrawSummaryCard(g, area, pal, pal.Border);
            using (var text = new SolidBrush(pal.TextSecondary))
                g.DrawString("至", font, text, area.Left + 213, area.Top + 6);
            DrawDateEndpoint(g, new Rectangle(area.Left + 4, area.Top + 3, 205, 23), view, 0, scroll, mouse, pal, font);
            DrawDateEndpoint(g, new Rectangle(area.Left + 231, area.Top + 3, 205, 23), view, 1, scroll, mouse, pal, font);
            var apply = new Rectangle(area.Right - 48, area.Top + 3, 44, 23);
            DrawUsageButton(g, apply, "筛选", true, mouse, pal, font);
            RegisterUsageHit(view, apply, scroll, view.Apply);
        }

        private static void DrawDateEndpoint(Graphics g, Rectangle area, UsageView view, int endpoint, int scroll, Point mouse, ThemePalette pal, Font font)
        {
            DateTime date = endpoint == 0 ? view.DraftStart : view.DraftEnd;
            int field = endpoint == 0 ? view.StartField : view.EndField;
            DrawSummaryCard(g, area, pal, pal.Border);
            using (var text = new SolidBrush(pal.TextSecondary))
            {
                g.DrawString(endpoint == 0 ? "起始" : "结束", font, text, area.X + 4, area.Y + 4);
                g.DrawString(endpoint == 0 ? ":00" : ":59", font, text, area.X + 143, area.Y + 4);
            }
            var parts = new[] { date.ToString("yyyy"), date.ToString("MM"), date.ToString("dd"), date.ToString("HH") };
            int[] offsets = { 31, 64, 91, 119 };
            for (int i = 0; i < parts.Length; i++)
            {
                int part = i;
                var rect = new Rectangle(area.X + offsets[i], area.Y + 1, i == 0 ? 30 : 22, area.Height - 2);
                DrawUsageButton(g, rect, parts[i], field == i, mouse, pal, font);
                RegisterUsageHit(view, rect, scroll, () =>
                {
                    if (endpoint == 0) view.StartField = part;
                    else view.EndField = part;
                    return false;
                }, endpoint, i);
            }
            using (var text = new SolidBrush(pal.TextSecondary))
                foreach (int offset in new[] { 59, 86 })
                    g.DrawString("–", font, text, area.X + offset, area.Y + 4);
            for (int i = 0; i < 2; i++)
            {
                int delta = i == 0 ? -1 : 1;
                var rect = new Rectangle(area.X + 165 + i * 19, area.Y + 1, 18, area.Height - 2);
                DrawUsageButton(g, rect, i == 0 ? "−" : "+", false, mouse, pal, font);
                RegisterUsageHit(view, rect, scroll, () =>
                {
                    view.Step(endpoint, endpoint == 0 ? view.StartField : view.EndField, delta);
                    return false;
                }, endpoint, field);
            }
            RegisterUsageHit(view, area, scroll, null, endpoint, field);
        }

        private static void DrawUsageButton(Graphics g, Rectangle rect, string value, bool selected, Point mouse, ThemePalette pal, Font font)
        {
            if (selected || rect.Contains(mouse))
                using (var path = StripForm.CreateRoundedPath(rect, 4))
                using (var fill = new SolidBrush(Color.FromArgb(selected ? 29 : 14, pal.AccentBlue)))
                    g.FillPath(fill, path);
            using (var text = new SolidBrush(selected ? pal.AccentBlue : pal.TextPrimary))
            using (var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(value, font, text, rect, centered);
        }

        private static void DrawUsageTrend(Graphics g, Rectangle card, Rectangle visibleBody, HistoryStatsSnapshot snap, Point mouse,
            ThemePalette pal, Font head, Font label, Font sub, Font hero)
        {
            DrawSummaryCard(g, card, pal, pal.Border);
            using (var primary = new SolidBrush(pal.TextPrimary))
            using (var secondary = new SolidBrush(pal.TextSecondary))
            using (var right = new StringFormat { Alignment = StringAlignment.Far })
            {
                g.DrawString("用量趋势", head, primary, card.X + 11, card.Y + 10);
                DrawSmallPill(g, card.X + 67, card.Y + 8, snap.PeriodLabel, sub, pal.AccentBlue);
                string grain = new[] { "小时", "日", "周", "月" }[(int)snap.Grain];
                g.DrawString("按" + grain + " (" + snap.Points.Count + "点) · 悬停查看", sub, secondary, card.Right - 10, card.Y + 10, right);
                var plot = new Rectangle(card.X + 55, card.Y + 45, card.Width - 73, 111);
                if (snap.Points.Count == 0)
                {
                    g.DrawString("正在读取用量", label, secondary, plot.X, plot.Y + 30);
                    return;
                }
                long peak = snap.Peak;
                double ceiling = Math.Max(1, peak * 1.17);
                int count = snap.Points.Count;
                float step = count > 1 ? plot.Width / (float)(count - 1) : 0;
                var points = snap.Points.Select((p, i) => new PointF(count == 1 ? plot.Left + plot.Width / 2 : plot.Left + i * step,
                    plot.Bottom - (float)(p.Totals.Tokens / ceiling * plot.Height))).ToArray();
                using (var grid = new Pen(pal.Border))
                using (var center = new StringFormat { Alignment = StringAlignment.Center })
                {
                    for (int i = 0; i < 4; i++)
                    {
                        float y = plot.Bottom - i * plot.Height / 3f;
                        g.DrawLine(grid, plot.Left, y, plot.Right, y);
                        g.DrawString(MetricFormatter.CompactTokensLower((long)Math.Round(i * ceiling / 3)), sub, secondary, plot.Left - 6, y - 7, right);
                    }
                    int ticks = count <= 8 ? count : 6;
                    for (int i = 0; i < ticks; i++)
                    {
                        int index = ticks == 1 ? 0 : i * (count - 1) / (ticks - 1);
                        g.DrawString(snap.Points[index].AxisLabel, sub, secondary, points[index].X, plot.Bottom + 6, center);
                    }
                }
                OverlayCharts.DrawArea(g, points, plot, pal.AccentBlue);
                using (var line = new Pen(pal.AccentBlue, 1.7f))
                using (var curve = OverlayCharts.BuildSmoothCurvePath(points))
                    if (points.Length > 1) g.DrawPath(line, curve);

                bool hover = Rectangle.Inflate(plot, 5, 5).Contains(mouse);
                int selected = hover ? (step > 0 ? Math.Max(0, Math.Min(count - 1, (int)Math.Round((mouse.X - plot.Left) / step))) : 0)
                    : snap.Points.FindIndex(p => p.Totals.Tokens == peak);
                if (hover || peak > 0)
                {
                    var point = points[selected];
                    var bin = snap.Points[selected];
                    using (var dot = new SolidBrush(pal.AccentBlue))
                    using (var ring = new Pen(pal.AccentBlue, 1.5f))
                    using (var guide = new Pen(pal.TextMuted) { DashStyle = DashStyle.Dash })
                    {
                        if (hover)
                        {
                            g.DrawLine(guide, point.X, plot.Top, point.X, plot.Bottom);
                            g.DrawEllipse(ring, point.X - 5, point.Y - 5, 10, 10);
                        }
                        g.FillEllipse(dot, point.X - 3, point.Y - 3, 6, 6);
                    }
                    if (hover)
                    {
                        DrawUsageHover(g, Rectangle.Intersect(card, visibleBody), point, bin, snap.Grain, pal, head, sub, hero);
                        return;
                    }
                    string tip = "峰值 " + bin.TooltipLabel + " · " + MetricFormatter.CompactTokensLower(peak);
                    int w = (int)Math.Ceiling(g.MeasureString(tip, sub).Width) + 14;
                    int left = Math.Max(plot.Left, Math.Min(card.Right - w - 9, (int)point.X - w / 2));
                    int top = Math.Max(card.Top + 29, (int)point.Y - 25);
                    var bubble = new Rectangle(left, top, w, 20);
                    using (var surface = new SolidBrush(pal.SurfaceBg))
                    using (var path = StripForm.CreateRoundedPath(bubble, 5))
                    using (var pen = new Pen(pal.BorderStrong))
                    {
                        g.FillPath(surface, path);
                        g.DrawPath(pen, path);
                    }
                    g.DrawString(tip, sub, primary, left + 7, top + 3);
                }
            }
        }

        private static void DrawUsageHover(Graphics g, Rectangle bounds, PointF point, UsageTrendPoint bin, UsageGrain grain,
            ThemePalette pal, Font head, Font small, Font hero)
        {
            var totals = bin.Totals;
            var models = bin.ModelTokens.Where(model => model.Value > 0)
                .OrderByDescending(model => model.Value).ThenBy(model => model.Key, StringComparer.OrdinalIgnoreCase).ToList();
            int width = 230;
            int baseHeight = totals.UnbrokenTokens > 0 ? 97 : 81;
            int rowCount = Math.Min(Math.Min(3, models.Count), Math.Max(0, (bounds.Height - baseHeight - 18) / 17));
            if (rowCount > 0 && models.Count > rowCount)
            {
                long other = models.Skip(rowCount - 1).Sum(model => model.Value);
                models.RemoveRange(rowCount - 1, models.Count - rowCount + 1);
                models.Add(new KeyValuePair<string, long>("其他模型", other));
            }

            int height = baseHeight + rowCount * 17 + (rowCount > 0 ? 6 : 0);
            if (bounds.Width < width + 12 || bounds.Height < height + 12) return;
            int x = (int)point.X + 13;
            if (x + width > bounds.Right - 6) x = (int)point.X - width - 13;
            x = Math.Max(bounds.Left + 6, Math.Min(bounds.Right - width - 6, x));
            int y = Math.Max(bounds.Top + 6, Math.Min(bounds.Bottom - height - 6, (int)point.Y + 9));
            var rect = new Rectangle(x, y, width, height);
            using (var path = StripForm.CreateRoundedPath(rect, 10))
            using (var surface = new SolidBrush(pal.SurfaceBg))
            using (var border = new Pen(pal.BorderStrong))
            using (var primary = new SolidBrush(pal.TextPrimary))
            using (var secondary = new SolidBrush(pal.TextSecondary))
            using (var accent = new SolidBrush(pal.AccentBlue))
            using (var right = new StringFormat { Alignment = StringAlignment.Far })
            {
                g.FillPath(surface, path);
                g.DrawPath(border, path);
                int left = x + 11, edge = rect.Right - 10;
                string date = grain == UsageGrain.Day
                    ? bin.StartLocal.ToString("M月d日（ddd）", CultureInfo.GetCultureInfo("zh-CN"))
                    : bin.TooltipLabel;
                g.DrawString(date, head, primary, left, y + 8);
                string calls = totals.DetailedThreads == 0 && totals.Sessions > 0 ? "调用 —" : totals.Calls + " 次调用";
                g.DrawString(calls, small, secondary, edge, y + 9, right);
                g.DrawString(MetricFormatter.CompactTokensLower(totals.Tokens) + " tok", hero, accent, left - 1, y + 24);

                var names = new[] { "新输入", "缓存", "输出" };
                var tokens = new[] { totals.UncachedInputTokens, totals.CachedInputTokens, totals.OutputTokens + totals.ReasoningTokens };
                for (int i = 0; i < names.Length; i++)
                {
                    int col = left + i * 70;
                    g.DrawString(names[i], small, secondary, col, y + 48);
                    string value = totals.DetailedThreads > 0 ? MetricFormatter.CompactTokensLower(tokens[i]) : "—";
                    g.DrawString(value, head, primary, col, y + 62);
                }

                int line = y + 80;
                if (totals.UnbrokenTokens > 0)
                {
                    g.DrawString("未拆解 " + MetricFormatter.CompactTokensLower(totals.UnbrokenTokens), small, secondary, left, line);
                    line += 16;
                }
                if (rowCount > 0)
                {
                    g.DrawLine(border, left, line, edge, line);
                    line += 6;
                }
                var colors = new[] { pal.CtxToolResults, pal.AccentBlue, pal.CtxUserInput };
                for (int i = 0; i < rowCount; i++)
                {
                    var model = models[i];
                    using (var dot = new SolidBrush(colors[i])) g.FillEllipse(dot, left, line + 5, 5, 5);
                    DrawSummaryText(g, model.Key, small, primary, new Rectangle(left + 9, line - 1, 91, 16));
                    g.DrawString(MetricFormatter.CompactTokensLower(model.Value), small, primary, edge - 55, line, right);
                    g.DrawString(Percent(model.Value * 100.0 / totals.Tokens), small, secondary, edge, line, right);
                    line += 17;
                }
            }
        }

        private static void DrawUsageModels(Graphics g, Rectangle card, HistoryStatsSnapshot snap, ThemePalette pal, Font head, Font label, Font sub)
        {
            DrawSummaryCard(g, card, pal, pal.Border);
            var colors = UsageColors(pal);
            using (var primary = new SolidBrush(pal.TextPrimary))
            using (var secondary = new SolidBrush(pal.TextSecondary))
            using (var right = new StringFormat { Alignment = StringAlignment.Far })
            using (var sep = new Pen(pal.Border))
            {
                int left = card.X + 12, edge = card.Right - 12;
                g.DrawString("模型排行", head, primary, left, card.Y + 10);
                g.DrawString("历史总计 " + Tokens(snap.AllTimeTokens) + " · " + snap.AllTimeThreads + " 会话", sub, secondary, edge, card.Y + 10, right);
                int y = card.Y + 31;
                if (snap.Models.Length == 0) g.DrawString("所选时段暂无模型用量", label, secondary, left, y + 12);
                foreach (var pair in snap.Models)
                {
                    var model = pair.Value;
                    var detail = Breakdown(model);
                    if (y > card.Y + 31) g.DrawLine(sep, left, y - 4, edge, y - 4);
                    DrawSummaryText(g, pair.Key, head, primary, new Rectangle(left, y, 210, 20));
                    if (model.DetailedThreads > 0 && model.DetailedThreads < model.Sessions)
                        DrawSummaryText(g, Coverage(model), sub, secondary, new Rectangle(left + 213, y, 141, 20));
                    string share = Percent(snap.Total.Tokens > 0 ? model.Tokens * 100.0 / snap.Total.Tokens : (double?)null);
                    int shareW = (int)Math.Ceiling(g.MeasureString(share, sub).Width);
                    DrawSmallPill(g, edge - 77 - shareW, y, share, sub, pal.AccentBlue);
                    g.DrawString(Tokens(model.Tokens), head, primary, edge, y + 2, right);
                    var bar = new Rectangle(left, y + 25, edge - left, 6);
                    if (model.DetailedThreads == 0)
                    {
                        using (var path = StripForm.CreateRoundedPath(bar, 3))
                        using (var fill = new SolidBrush(Color.FromArgb(63, pal.AccentBlue)))
                            g.FillPath(fill, path);
                        g.DrawString("基于历史会话总量", sub, secondary, left, y + 36);
                    }
                    else
                    {
                        DrawUsageComposition(g, bar, detail, colors, pal);
                        string[] names = { "输入", "缓存", "思考", "输出" };
                        int x = left;
                        for (int i = 0; i < names.Length; i++)
                        {
                            using (var dot = new SolidBrush(colors[i]))
                                g.FillEllipse(dot, x, y + 42, 5, 5);
                            string value = names[i] + " " + Tokens(detail[i]);
                            g.DrawString(value, sub, secondary, x + 7, y + 36);
                            x += (int)Math.Ceiling(g.MeasureString(value, sub).Width) + 12;
                        }
                    }
                    g.DrawString(UsageCount(model), sub, secondary, edge, y + 36, right);
                    y += 59;
                }
            }
        }

        private static Color[] UsageColors(ThemePalette pal)
        {
            return new[] { pal.AccentBlue, pal.CtxToolResults, pal.CtxUserInput, pal.CtxCheckpoint };
        }

        private static long[] Breakdown(UsageTotals total)
        {
            return new[] { total.UncachedInputTokens, total.CachedInputTokens, total.ReasoningTokens, total.OutputTokens };
        }

        private static void DrawUsageComposition(Graphics g, Rectangle bar, long[] tokens, Color[] colors, ThemePalette pal)
        {
            using (var path = StripForm.CreateRoundedPath(bar, 3))
            using (var rail = new SolidBrush(pal.Border))
            {
                g.FillPath(rail, path);
                var state = g.Save();
                g.SetClip(path, CombineMode.Intersect);
                double total = tokens.Sum(value => (double)value);
                int remaining = bar.Width - 3 * tokens.Count(value => value > 0);
                float x = bar.X;
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (tokens[i] <= 0) continue;
                    float segment = 3 + (float)(remaining * tokens[i] / total);
                    using (var fill = new SolidBrush(colors[i]))
                        g.FillRectangle(fill, x, bar.Y, segment + 0.15f, bar.Height);
                    x += segment;
                }
                g.Restore(state);
            }
        }

        private static string Tokens(long value) { return MetricFormatter.CompactTokens(value); }
        private static string Detailed(UsageTotals total, long value) { return total.DetailedThreads > 0 ? Tokens(value) : "—"; }
        private static string Percent(double? value) { return value.HasValue ? value.Value.ToString("0.#", CultureInfo.CurrentCulture) + "%" : "—"; }
        private static string UsageCount(UsageTotals total) { return total.Sessions + " 会话" + (total.Calls > 0 ? " · " + total.Calls + " 调用" : ""); }
        private static string Coverage(UsageTotals total)
        {
            return total.DetailedThreads < total.Sessions ? "明细覆盖 " + total.DetailedThreads + "/" + total.Sessions + " 会话" : "";
        }
    }
}
