using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace CodexInputUsage
{
    internal static class OverlayCharts
    {
        internal static GraphicsPath BuildSmoothCurvePath(PointF[] points, float tension = 0.42f)
        {
            var path = new GraphicsPath();
            AppendCurve(path, points, tension);
            return path;
        }

        internal static GraphicsPath BuildSmoothAreaPath(PointF[] points, float baselineY, float tension = 0.42f)
        {
            var path = BuildSmoothCurvePath(points, tension);
            if (points.Length < 2) return path;
            var first = points[0];
            var last = points[points.Length - 1];
            path.AddLine(last, new PointF(last.X, baselineY));
            path.AddLine(new PointF(last.X, baselineY), new PointF(first.X, baselineY));
            path.CloseFigure();
            return path;
        }

        internal static GraphicsPath BuildSmoothBandPath(PointF[] upper, PointF[] lower, float tension = 0.42f)
        {
            var path = BuildSmoothCurvePath(upper, tension);
            if (upper.Length < 2 || lower.Length < 2) return path;
            var reverse = (PointF[])lower.Clone();
            Array.Reverse(reverse);
            path.AddLine(upper[upper.Length - 1], reverse[0]);
            AppendCurve(path, reverse, tension);
            path.CloseFigure();
            return path;
        }

        private static void AppendCurve(GraphicsPath path, PointF[] points, float tension)
        {
            tension = Math.Max(0, Math.Min(0.5f, tension));
            for (int i = 1; i < points.Length; i++)
            {
                PointF a = points[i - 1], b = points[i];
                float dx = (b.X - a.X) * tension;
                path.AddBezier(a, new PointF(a.X + dx, a.Y), new PointF(b.X - dx, b.Y), b);
            }
        }

        internal static void DrawArea(Graphics g, PointF[] points, Rectangle plot, Color color)
        {
            if (points.Length < 2) return;
            using (var area = BuildSmoothAreaPath(points, plot.Bottom))
            using (var fill = new LinearGradientBrush(plot, Color.FromArgb(78, color), Color.FromArgb(0, color), LinearGradientMode.Vertical))
                g.FillPath(fill, area);
        }
    }
}
