using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CodexInputUsage
{
    internal sealed partial class DetailPopoverForm
    {
        private const int TurnsToolbarHeight = 32;
        private const int TrendCardHeight = 162;
        private const int ModelCardStride = 92;
        private const int TurnsBodyTop = HeaderHeight + TabBarHeight + TurnsToolbarHeight;
        private readonly TurnsView _turnsView = new TurnsView();
        private bool ShowingSummary { get { return _activeTab == PopoverTab.Turns && _turnsView.Summary; } }

        private enum ChartMetric { Total, Context, Tps, Ttft, Cache }

        private sealed class TurnsView
        {
            internal bool Summary;
            internal ChartMetric Timeline = ChartMetric.Total, FitMetric = ChartMetric.Tps;
            internal string SelectedModel;
            internal Point Mouse = new Point(-1, -1);
            internal readonly List<Rectangle> Buttons = new List<Rectangle>();
            internal readonly List<Action> Actions = new List<Action>();
            private SessionMetricsSnapshot _snapshot;
            private SessionModelSummary[] _models;
            private readonly Dictionary<ChartMetric, TrendSample[]> _samples = new Dictionary<ChartMetric, TrendSample[]>();
            private TrendEstimate[] _fit;
            private ChartMetric _fitMetric;
            private string _fitModel;

            internal SessionModelSummary[] Models(SessionMetricsSnapshot snapshot)
            {
                if (_snapshot != snapshot)
                {
                    _snapshot = snapshot;
                    _models = SessionAnalytics.BuildSessionModelSummaries(snapshot);
                    _fit = null;
                    _samples.Clear();
                    if (!_models.Any(model => model.Name == SelectedModel)) SelectedModel = null;
                }
                return _models;
            }

            internal TrendSample[] Samples(ChartMetric metric)
            {
                TrendSample[] points;
                if (_samples.TryGetValue(metric, out points)) return points;
                var samples = new List<TrendSample>();
                foreach (var turn in _snapshot.Turns)
                {
                    double? value = TrendValue(turn, metric);
                    if (value >= 0 && !double.IsInfinity(value.Value))
                        samples.Add(new TrendSample(turn.Index, metric == ChartMetric.Cache ? Math.Min(100, value.Value) : value.Value,
                            SessionAnalytics.ModelName(turn, _snapshot)));
                }
                points = samples.ToArray();
                _samples.Add(metric, points);
                return points;
            }

            internal TrendEstimate[] Fit(TrendSample[] points, double maximum)
            {
                if (_fit == null || _fitMetric != FitMetric || _fitModel != SelectedModel)
                {
                    _fitMetric = FitMetric;
                    _fitModel = SelectedModel;
                    _fit = SessionAnalytics.ComputeTrendFit(points, maximum);
                }
                return _fit;
            }

            internal void AddButton(Rectangle bounds, Action action)
            {
                if (bounds.Width <= 0 || bounds.Height <= 0) return;
                Buttons.Add(bounds);
                Actions.Add(action);
            }
        }

        internal static bool CheckTurnsInteraction(SessionMetricsSnapshot snapshot, float scale)
        {
            using (var form = new DetailPopoverForm { _snapshot = snapshot, _logicalHeight = PopoverHeight,
                _dpiScale = scale, _activeTab = PopoverTab.Turns, _visibleRows = 5, _scrollOffset = 2 })
            using (var bitmap = new Bitmap(OverlayDpi.Pixels(PopoverWidth, scale), OverlayDpi.Pixels(PopoverHeight, scale)))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var bounds = new Rectangle(Point.Empty, bitmap.Size);
                var paint = new PaintEventArgs(graphics, bounds);
                Action<int, int> click = (x, y) => form.OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1,
                    OverlayDpi.Pixels(x, scale), OverlayDpi.Pixels(y, scale), 0));

                form.OnPaint(paint);
                click(495, 85);
                if (!form.ShowingSummary || form._scrollOffset != 0) return false;
                form._maxBodyScroll = TurnsSummaryHeight(form._turnsView.Models(snapshot).Length) - PopoverHeight;
                form.OnPaint(paint);
                click(454, 126);
                click(496, 295);
                if (form._turnsView.Timeline != ChartMetric.Tps || form._turnsView.FitMetric != ChartMetric.Cache) return false;

                form.OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, OverlayDpi.Pixels(100, scale),
                    OverlayDpi.Pixels(410, scale), -120));
                form.OnPaint(paint);
                if (form._bodyScroll != 40) return false;
                click(140, 580);
                if (form._turnsView.SelectedModel != "6Astra") return false;
                click(140, 580);
                if (form._turnsView.SelectedModel != null) return false;
                click(414, 85);
                return !form.ShowingSummary && form._bodyScroll == 0;
            }
        }

        private static int TurnsSummaryHeight(int models)
        {
            return TurnsBodyTop + 2 * (TrendCardHeight + 8) + 22 + Math.Max(1, models) * ModelCardStride + 8;
        }

        private static void DrawTurnsToolbar(Graphics g, Size size, SessionMetricsSnapshot snap, ThemePalette pal,
            TurnsView view, int scroll, int visible, Font bold, Font regular)
        {
            int top = TurnsBodyTop - TurnsToolbarHeight;
            int count = snap.Turns.Count;
            var control = new Rectangle(size.Width - Pad - 160, top + 3, 160, 25);
            DrawSegments(g, control, new[] { "按轮查看 (" + count + ")", "总汇总" }, view.Summary ? 1 : 0,
                view.Mouse, pal, regular, (index, rect) => view.AddButton(rect, () => view.Summary = index == 1), 98);
            string range = view.Summary || count == 0 ? count + " 轮"
                : string.Format("{0}–{1} / {2} 轮", scroll + 1, Math.Min(count, scroll + visible), count);
            if (view.Summary && view.SelectedModel != null) range += " · " + view.SelectedModel;
            using (var primary = new SolidBrush(pal.TextPrimary))
            using (var muted = new SolidBrush(pal.TextSecondary))
            {
                g.DrawString("调用记录", bold, primary, Pad, top + 7);
                DrawSummaryText(g, range, regular, muted, new Rectangle(Pad + 54, top + 6, control.Left - Pad - 64, 19));
            }
        }

        private static void DrawSegments(Graphics g, Rectangle area, string[] labels, int selected, Point mouse,
            ThemePalette pal, Font font, Action<int, Rectangle> register, int firstWidth = 0)
        {
            using (var path = StripForm.CreateRoundedPath(area, 7))
            using (var surface = new SolidBrush(pal.SurfaceBg))
            using (var border = new Pen(pal.Border))
            using (var accent = new SolidBrush(pal.AccentBlue))
            using (var tint = new SolidBrush(Color.FromArgb(24, pal.AccentBlue)))
            using (var text = new SolidBrush(pal.TextSecondary))
            using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.FillPath(surface, path);
                g.DrawPath(border, path);
                for (int i = 0; i < labels.Length; i++)
                {
                    int width = firstWidth > 0 ? (i == 0 ? firstWidth : area.Width - firstWidth) : area.Width / labels.Length;
                    int x = firstWidth > 0 ? area.X + i * firstWidth : area.X + i * width;
                    var rect = new Rectangle(x + 2, area.Y + 2, width - 4, area.Height - 4);
                    if (i == selected || rect.Contains(mouse))
                        using (var highlight = StripForm.CreateRoundedPath(rect, 5))
                            g.FillPath(tint, highlight);
                    g.DrawString(labels[i], font, i == selected ? accent : text, rect, center);
                    register(i, rect);
                }
            }
        }

        private static void RenderTurnsSummary(Graphics g, Size size, SessionMetricsSnapshot snap, ThemePalette pal,
            TurnsView view, int scroll, Font bold, Font regular, Font small)
        {
            var models = view.Models(snap);
            var shades = new[] { pal.AccentBlue, pal.CtxUserInput, pal.CtxToolResults, pal.CtxSystemTools, pal.CtxMcpTools, pal.CtxSystemPrompt };
            var modelColors = models.Select((model, i) => new { model.Name, Color = shades[i % shades.Length] })
                .ToDictionary(model => model.Name, model => model.Color, StringComparer.OrdinalIgnoreCase);
            int y = TurnsBodyTop + 3;
            int width = size.Width - 2 * Pad;
            DrawTrendCard(g, new Rectangle(Pad, y, width, TrendCardHeight), snap, pal, view, scroll,
                modelColors, false, bold, regular, small);
            y += TrendCardHeight + 8;
            DrawTrendCard(g, new Rectangle(Pad, y, width, TrendCardHeight), snap, pal, view, scroll,
                modelColors, true, bold, regular, small);
            y += TrendCardHeight + 8;

            using (var primary = new SolidBrush(pal.TextPrimary))
            using (var secondary = new SolidBrush(pal.TextSecondary))
            using (var right = new StringFormat { Alignment = StringAlignment.Far })
            {
                g.DrawString("模型总览", bold, primary, Pad + 1, y);
                string count = models.Length > 1 ? models.Length + " 模型 · " : "";
                g.DrawString(count + snap.Turns.Count + " 轮 · " + MetricFormatter.CompactTokens(models.Sum(model => model.TotalTokens)),
                    small, secondary, size.Width - Pad, y, right);
                y += 22;
                if (models.Length == 0) g.DrawString("当前会话暂无模型用量记录", regular, secondary, Pad + 10, y + 13);
                foreach (var model in models)
                {
                    var card = new Rectangle(Pad, y, width, 86);
                    var hit = card;
                    hit.Offset(0, -scroll);
                    if (models.Length > 1)
                        view.AddButton(Rectangle.Intersect(hit, new Rectangle(0, TurnsBodyTop, size.Width, size.Height - TurnsBodyTop)), () => view.SelectedModel = view.SelectedModel == model.Name ? null : model.Name);
                    DrawSessionModel(g, card, model, modelColors[model.Name], pal, models.Length > 1,
                        view.SelectedModel == model.Name, models.Length > 1 && view.Mouse.Y >= TurnsBodyTop && hit.Contains(view.Mouse), bold, regular, small);
                    y += ModelCardStride;
                }
            }
        }

        private static double? TrendValue(TurnSummary turn, ChartMetric metric)
        {
            switch (metric)
            {
                case ChartMetric.Total: return turn.TotalTokens;
                case ChartMetric.Context: return turn.ContextInputTokens;
                case ChartMetric.Tps: return turn.Tps;
                case ChartMetric.Ttft: return turn.TtftMs;
                default: return turn.CacheHitPct;
            }
        }

        private static string TrendLabel(double value, ChartMetric metric, bool compact = false)
        {
            switch (metric)
            {
                case ChartMetric.Total:
                case ChartMetric.Context: return MetricFormatter.CompactTokens((long)value);
                case ChartMetric.Tps: return value.ToString("0.#") + (compact ? "" : " tok/s");
                case ChartMetric.Ttft: return (value / 1000).ToString("0.#") + "s";
                default: return value.ToString("0.#") + "%";
            }
        }

        private static void DrawTrendCard(Graphics g, Rectangle card, SessionMetricsSnapshot snap, ThemePalette pal,
            TurnsView view, int scroll, Dictionary<string, Color> modelColors, bool fit, Font bold, Font regular, Font small)
        {
            DrawSummaryCard(g, card, pal, pal.Border);
            ChartMetric[] metrics = fit ? new[] { ChartMetric.Tps, ChartMetric.Ttft, ChartMetric.Cache }
                : new[] { ChartMetric.Total, ChartMetric.Context, ChartMetric.Tps, ChartMetric.Ttft };
            string[] labels = fit ? new[] { "TPS", "首字", "缓存" } : new[] { "总Token", "上下文", "TPS", "首字" };
            ChartMetric metric = fit ? view.FitMetric : view.Timeline;
            int controlWidth = fit ? 126 : 192;
            var controls = new Rectangle(card.Right - controlWidth - 8, card.Y + 7, controlWidth, 24);
            var mouse = view.Mouse;
            mouse.Offset(0, scroll);
            DrawSegments(g, controls, labels, Array.IndexOf(metrics, metric), view.Mouse.Y >= TurnsBodyTop ? mouse : new Point(-1, -1), pal, small,
                (index, rect) =>
                {
                    rect.Offset(0, -scroll);
                    view.AddButton(Rectangle.Intersect(rect, new Rectangle(0, TurnsBodyTop, PopoverWidth, PopoverHeight - TurnsBodyTop)), () =>
                    {
                        if (fit) view.FitMetric = metrics[index];
                        else view.Timeline = metrics[index];
                    });
                });
            var samples = view.Samples(metric);
            if (fit && view.SelectedModel != null)
                samples = samples.Where(point => string.Equals(point.Model, view.SelectedModel, StringComparison.OrdinalIgnoreCase)).ToArray();
            var plot = new Rectangle(card.X + 50, card.Y + 46, card.Width - 69, 92);
            string note = fit ? "" : snap.Turns.Count + " 轮";
            if (samples.Length > 0)
            {
                double maximum = metric == ChartMetric.Cache ? 100 : double.MaxValue;
                var estimates = fit ? view.Fit(samples, maximum) : new TrendEstimate[0];
                double ceiling = Math.Max(samples.Max(point => point.Value), estimates.Length == 0 ? 0 : estimates.Max(point => point.High));
                double top = metric == ChartMetric.Cache ? 100 : Math.Max(1, ceiling * 1.08);
                double first = snap.Turns[0].Index;
                double last = snap.Turns[snap.Turns.Count - 1].Index;
                Func<double, double, PointF> map = (x, y) => new PointF(
                    plot.Left + (float)(last == first ? plot.Width / 2.0 : (x - first) / (last - first) * plot.Width),
                    plot.Bottom - (float)(y / top * plot.Height));
                var screenPoints = samples.Select(point => map(point.Turn, point.Value)).ToArray();
                double mean = samples.Average(point => point.Value);
                TrendSample? hovered = null;
                if (view.Mouse.Y >= TurnsBodyTop && Rectangle.Inflate(plot, 6, 6).Contains(mouse))
                {
                    int nearest = -1;
                    double best = 100;
                    for (int i = 0; i < screenPoints.Length; i++)
                    {
                        double dx = screenPoints[i].X - mouse.X, dy = screenPoints[i].Y - mouse.Y;
                        double distance = dx * dx + dy * dy;
                        if (distance > best) continue;
                        best = distance;
                        nearest = i;
                    }
                    if (nearest >= 0) hovered = samples[nearest];
                }

                using (var axis = new SolidBrush(pal.TextSecondary))
                using (var right = new StringFormat { Alignment = StringAlignment.Far })
                using (var center = new StringFormat { Alignment = StringAlignment.Center })
                using (var grid = new Pen(pal.Border))
                {
                    for (int i = 0; i <= 2; i++)
                    {
                        double level = top * i / 2;
                        float y = map(first, level).Y;
                        g.DrawLine(grid, plot.Left, y, plot.Right, y);
                        g.DrawString(TrendLabel(level, metric, true), small, axis, plot.Left - 5, y - 7, right);
                    }
                    int ticks = Math.Min(5, snap.Turns.Count);
                    for (int i = 0; i < ticks; i++)
                    {
                        int turn = snap.Turns[ticks <= 1 ? 0 : i * (snap.Turns.Count - 1) / (ticks - 1)].Index;
                        g.DrawString("#" + turn, small, axis, map(turn, 0).X, plot.Bottom + 4, center);
                    }
                }

                Color line = fit && view.SelectedModel != null ? modelColors[view.SelectedModel] : pal.AccentBlue;
                var gState = g.Save();
                g.SetClip(Rectangle.Inflate(plot, 4, 4), CombineMode.Intersect);
                using (var fill = new SolidBrush(Color.FromArgb(32, line)))
                using (var pen = new Pen(line, 1.7f))
                using (var guide = new Pen(pal.TextMuted) { DashStyle = DashStyle.Dash })
                {
                    if (!fit && screenPoints.Length > 1)
                    {
                        OverlayCharts.DrawArea(g, screenPoints, plot, line);
                        using (var curve = OverlayCharts.BuildSmoothCurvePath(screenPoints)) g.DrawPath(pen, curve);
                    }
                    if (fit)
                    {
                        float meanY = map(first, mean).Y;
                        g.DrawLine(guide, plot.Left, meanY, plot.Right, meanY);
                        note = "均值 " + TrendLabel(mean, metric);
                        if (estimates.Length > 1)
                        {
                            var upper = estimates.Select(point => map(point.X, point.High)).ToArray();
                            var lower = estimates.Select(point => map(point.X, point.Low)).ToArray();
                            using (var band = OverlayCharts.BuildSmoothBandPath(upper, lower)) g.FillPath(fill, band);
                            using (var curve = OverlayCharts.BuildSmoothCurvePath(estimates.Select(point => map(point.X, point.Value)).ToArray()))
                                g.DrawPath(pen, curve);
                        }
                    }
                    for (int i = 0; i < screenPoints.Length; i++)
                    {
                        PointF point = screenPoints[i];
                        using (var dot = new SolidBrush(modelColors[samples[i].Model]))
                            g.FillEllipse(dot, point.X - 2.6f, point.Y - 2.6f, 5.2f, 5.2f);
                    }
                    if (hovered.HasValue)
                    {
                        var sample = hovered.Value;
                        PointF point = map(sample.Turn, sample.Value);
                        g.DrawLine(guide, point.X, plot.Top, point.X, plot.Bottom);
                        g.DrawEllipse(pen, point.X - 4.5f, point.Y - 4.5f, 9, 9);
                        note = "#" + sample.Turn + " · " + sample.Model + " · " + TrendLabel(sample.Value, metric);
                        if (fit && estimates.Length > 0)
                            note += " / 拟合 " + TrendLabel(EstimateAt(estimates, sample.Turn), metric);
                    }
                }
                g.Restore(gState);
            }
            else
            {
                using (var muted = new SolidBrush(pal.TextMuted))
                using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("当前指标暂无有效样本", regular, muted, plot, center);
            }
            using (var primary = new SolidBrush(pal.TextPrimary))
            using (var secondary = new SolidBrush(pal.TextSecondary))
            {
                g.DrawString(fit ? "趋势拟合" : "会话走势", bold, primary, card.X + 11, card.Y + 11);
                if (!fit && note == snap.Turns.Count + " 轮")
                    DrawSmallPill(g, card.X + 65, card.Y + 9, note, small, pal.AccentBlue);
                else
                    DrawSummaryText(g, note, small, secondary, new Rectangle(card.X + 65, card.Y + 9, controls.Left - card.X - 71, 21));
            }
        }

        private static double EstimateAt(TrendEstimate[] estimates, int turn)
        {
            for (int i = 1; i < estimates.Length; i++)
            {
                if (estimates[i].X < turn) continue;
                var low = estimates[i - 1];
                var high = estimates[i];
                return low.Value + (high.Value - low.Value) * (turn - low.X) / (high.X - low.X);
            }
            return estimates.Last().Value;
        }

        private static void DrawSummaryCard(Graphics g, Rectangle rect, ThemePalette pal, Color border)
        {
            using (var path = StripForm.CreateRoundedPath(rect, 9))
            using (var fill = new SolidBrush(pal.CardBg))
            using (var stroke = new Pen(border))
            {
                g.FillPath(fill, path);
                g.DrawPath(stroke, path);
            }
        }

        private static void DrawSummaryText(Graphics g, string value, Font font, Brush brush, Rectangle bounds)
        {
            using (var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(value, font, brush, bounds, format);
        }

        private static void DrawSessionModel(Graphics g, Rectangle card, SessionModelSummary model, Color color,
            ThemePalette pal, bool multiple, bool selected, bool hover, Font bold, Font regular, Font small)
        {
            DrawSummaryCard(g, card, pal, selected ? color : hover ? Color.FromArgb(135, color) : pal.Border);
            int x = card.X + 11;
            int width = card.Width - 22;
            var colors = UsageColors(pal);
            var tokens = new[] { model.UncachedInputTokens, model.CachedInputTokens, model.ReasoningTokens, model.ReplyOutputTokens };
            var labels = new[] { "新输入", "缓存", "思考", "输出" };
            using (var dot = new SolidBrush(color))
            using (var primary = new SolidBrush(pal.TextPrimary))
            using (var secondary = new SolidBrush(pal.TextSecondary))
            using (var right = new StringFormat { Alignment = StringAlignment.Far })
            {
                g.FillEllipse(dot, x, card.Y + 13, 7, 7);
                int nameWidth = Math.Min(190, (int)Math.Ceiling(g.MeasureString(model.Name, bold).Width));
                DrawSummaryText(g, model.Name, bold, primary, new Rectangle(x + 12, card.Y + 6, nameWidth + 2, 20));
                string share = model.Calls + " 轮" + (multiple ? " · " + model.CallSharePct.ToString("0.#") + "%" : "");
                DrawSmallPill(g, x + 17 + nameWidth, card.Y + 7, share, small, color);
                g.DrawString(MetricFormatter.CompactTokens(model.TotalTokens), bold, primary, card.Right - 11, card.Y + 9, right);

                var bar = new Rectangle(x, card.Y + 30, width, 7);
                DrawUsageComposition(g, bar, tokens, colors, pal);
                for (int i = 0; i < labels.Length; i++)
                {
                    int left = x + i * width / labels.Length;
                    using (var fill = new SolidBrush(colors[i]))
                        g.FillEllipse(fill, left, card.Y + 47, 6, 6);
                    g.DrawString(labels[i] + " " + MetricFormatter.CompactTokens(tokens[i]), regular, primary, left + 10, card.Y + 42);
                }
                string speed = model.AvgTps.HasValue ? TrendLabel(model.AvgTps.Value, ChartMetric.Tps) : "—";
                string first = model.AvgTtftMs.HasValue ? TrendLabel(model.AvgTtftMs.Value, ChartMetric.Ttft) : "—";
                string cache = model.CacheHitPct.HasValue ? TrendLabel(model.CacheHitPct.Value, ChartMetric.Cache) : "—";
                g.DrawString("均速 " + speed + " · 平均首字 " + first + " · 缓存命中 " + cache, small, secondary, x, card.Y + 63);
            }
        }
    }
}
