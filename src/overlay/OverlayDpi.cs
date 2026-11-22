using System;
using System.Drawing;

namespace CodexInputUsage
{
    internal static class OverlayDpi
    {
        internal const int WmDpiChanged = 0x02E0;

        internal static int Pixels(int logical, float scale)
        {
            return (int)Math.Round(logical * scale, MidpointRounding.AwayFromZero);
        }

        internal static Point ToLogical(Point physical, float scale)
        {
            return new Point((int)Math.Floor(physical.X / scale), (int)Math.Floor(physical.Y / scale));
        }

        internal static Font FontAt96Dpi(string family, float points, FontStyle style)
        {
            return new Font(family, points * 96f / 72f, style, GraphicsUnit.Pixel);
        }
    }
}
