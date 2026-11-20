using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace CodexInputUsage
{
    internal sealed class StripForm : Form
    {
        private const int WsExToolWindow = 0x00000080;
        private const int WsExLayered = 0x00080000;
        private const int WsExNoActivate = 0x08000000;
        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;

        private const int StripHeightPx = 24;
        private const int PaddingXPx = 8;

        private ThemePalette _palette = ThemePalette.Light;
        private string _displayText = string.Empty;
        private bool _hovered;
        private bool _popoverOpen;
        private bool _allowShow;
        private Rectangle _targetBounds = Rectangle.Empty;

        private ToolStripMenuItem _autoStartItem;

        internal event EventHandler StripClicked;
        internal event EventHandler AutoStartToggled;
        internal event EventHandler ExitRequested;

        internal StripForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            Cursor = Cursors.Hand;

            var menu = new ContextMenuStrip();
            menu.Items.Add("展开 / 收起用量详情", null, delegate
            {
                if (StripClicked != null) StripClicked(this, EventArgs.Empty);
            });
            _autoStartItem = new ToolStripMenuItem("开机自启（跟随原生 Codex）") { CheckOnClick = false };
            _autoStartItem.Click += delegate
            {
                if (AutoStartToggled != null) AutoStartToggled(this, EventArgs.Empty);
            };
            menu.Items.Add(_autoStartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出 Codex InputUsage", null, delegate
            {
                if (ExitRequested != null) ExitRequested(this, EventArgs.Empty);
            });
            ContextMenuStrip = menu;
        }

        internal void SetAutoStartChecked(bool enabled)
        {
            if (_autoStartItem != null) _autoStartItem.Checked = enabled;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExToolWindow | WsExLayered | WsExNoActivate;
                return cp;
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(value && _allowShow && !_targetBounds.IsEmpty);
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

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!_hovered)
            {
                _hovered = true;
                BlitCurrentSurface();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hovered)
            {
                _hovered = false;
                BlitCurrentSurface();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                if (StripClicked != null) StripClicked(this, EventArgs.Empty);
            }
            else if (e.Button == MouseButtons.Right && ContextMenuStrip != null)
            {
                ContextMenuStrip.Show(Cursor.Position);
            }
        }

        internal void SetPopoverOpen(bool open)
        {
            if (_popoverOpen == open) return;
            _popoverOpen = open;
            if (Visible) BlitCurrentSurface();
        }

        internal bool ApplyDockedState(
            ComposerDockSnapshot dock,
            SessionMetricsSnapshot metrics,
            ThemePalette palette,
            out Rectangle placedBounds,
            out string chosenVariant)
        {
            placedBounds = Rectangle.Empty;
            chosenVariant = string.Empty;

            if (dock == null || dock.Source == DockSourceKind.None || dock.WindowHandle == IntPtr.Zero)
            {
                HideStrip();
                return false;
            }

            string[] variants = MetricFormatter.BuildStripVariants(metrics);
            int rightEdge;
            if (dock.Source == DockSourceKind.UiaContext)
                rightEdge = dock.ReferenceRect.Left - 10;
            else if (dock.Source == DockSourceKind.UiaModel)
                rightEdge = dock.ReferenceRect.Left - 34;
            else
                rightEdge = dock.ComposerRect.Right - 220;

            int minLeft = Math.Max(dock.ComposerRect.Left + 10, dock.LeftBarrierX + 10);
            int maxWidth = rightEdge - minLeft;
            if (maxWidth < 70)
            {
                HideStrip();
                return false;
            }

            string selected = null;
            int selectedWidth = 0;
            for (int i = 0; i < variants.Length; i++)
            {
                int w = MeasureVariantWidth(variants[i]) + (PaddingXPx * 2);
                if (w <= maxWidth)
                {
                    selected = variants[i];
                    selectedWidth = w;
                    break;
                }
            }

            if (selected == null)
            {
                HideStrip();
                return false;
            }

            int x = rightEdge - selectedWidth;
            int y = dock.RowCenterY - (StripHeightPx / 2);
            var work = Screen.FromHandle(dock.WindowHandle).WorkingArea;
            if (y < work.Top) y = work.Top;
            if (y + StripHeightPx > work.Bottom) y = work.Bottom - StripHeightPx;
            if (x < work.Left) x = work.Left;

            Rectangle nextBounds = new Rectangle(x, y, selectedWidth, StripHeightPx);
            bool needRepaint = !string.Equals(_displayText, selected, StringComparison.Ordinal)
                || _palette != palette
                || _targetBounds.Size != nextBounds.Size
                || !Visible;
            bool moved = _targetBounds != nextBounds;

            _palette = palette;
            _displayText = selected;
            _targetBounds = nextBounds;
            _allowShow = true;
            placedBounds = nextBounds;
            chosenVariant = selected;

            if (!Visible)
            {
                Bounds = nextBounds;
                Show();
            }
            else if (moved)
            {
                Bounds = nextBounds;
            }

            if (needRepaint || moved)
            {
                BlitCurrentSurface();
            }

            if (IsHandleCreated)
            {
                NativeWin32.SetWindowPos(
                    Handle, NativeWin32.HWND_TOPMOST,
                    nextBounds.X, nextBounds.Y, nextBounds.Width, nextBounds.Height,
                    NativeWin32.SWP_NOACTIVATE | NativeWin32.SWP_SHOWWINDOW);
            }

            return true;
        }

        internal void HideStrip()
        {
            _allowShow = false;
            _hovered = false;
            if (Visible) Hide();
        }

        internal static Bitmap RenderPreviewBitmap(string text, ThemePalette palette, bool hovered, bool includeBackdrop)
        {
            int width = MeasureVariantWidth(text) + (PaddingXPx * 2);
            var bmp = new Bitmap(width, StripHeightPx, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                if (includeBackdrop)
                {
                    g.Clear(palette.Kind == CodexThemeKind.Dark
                        ? Color.FromArgb(255, 33, 36, 42)
                        : Color.FromArgb(255, 255, 255, 255));
                }
                else
                {
                    g.Clear(Color.Transparent);
                }
                DrawStripContent(g, new Size(width, StripHeightPx), text, palette, hovered);
            }
            return bmp;
        }

        private void BlitCurrentSurface()
        {
            if (!IsHandleCreated || IsDisposed || _targetBounds.Width <= 0 || _targetBounds.Height <= 0)
                return;

            using (var bmp = new Bitmap(_targetBounds.Width, _targetBounds.Height, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    DrawStripContent(g, _targetBounds.Size, _displayText, _palette, _hovered || _popoverOpen);
                }
                BlitLayeredBitmap(bmp, _targetBounds.Location, _targetBounds.Size);
            }
        }

        private static void DrawStripContent(Graphics g, Size size, string text, ThemePalette palette, bool showPill)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var rect = new Rectangle(0, 0, size.Width - 1, size.Height - 1);
            using (var path = CreateRoundedPath(rect, 6))
            {
                if (showPill)
                {
                    using (var fill = new SolidBrush(palette.StripHoverFill))
                        g.FillPath(fill, path);
                    using (var border = new Pen(palette.StripHoverBorder, 1f))
                        g.DrawPath(border, path);
                }
                else
                {
                    // Alpha=1 makes the entire tight strip area reliably clickable while visually 100% transparent
                    using (var hitFill = new SolidBrush(Color.FromArgb(1, 128, 128, 128)))
                        g.FillPath(hitFill, path);
                }
            }

            if (string.IsNullOrEmpty(text)) return;

            using (var textFont = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point))
            using (var iconFont = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (var mainBrush = new SolidBrush(palette.StripText))
            using (var mutedBrush = new SolidBrush(palette.StripTextSecondary))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                float x = PaddingXPx;

                // Split lightning prefix so it renders slightly smaller and optically centered
                string body = text;
                if (body.StartsWith("\u26A1 ", StringComparison.Ordinal))
                {
                    string glyph = "\u26A1";
                    SizeF iconSz = g.MeasureString(glyph, iconFont, 1000, fmt);
                    float iconY = (size.Height - iconSz.Height) / 2f;
                    g.DrawString(glyph, iconFont, mutedBrush, x, iconY, fmt);
                    x += iconSz.Width + 3f;
                    body = body.Substring(2);
                }

                // Draw segments split by " · " so dots are subtly muted while numbers/labels stay crisp
                string sep = " \u00B7 ";
                string[] parts = body.Split(new string[] { sep }, StringSplitOptions.None);
                SizeF sampleSz = g.MeasureString("Ag", textFont, 1000, fmt);
                float textY = (size.Height - sampleSz.Height) / 2f;

                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0)
                    {
                        g.DrawString(sep, textFont, mutedBrush, x, textY, fmt);
                        x += g.MeasureString(sep, textFont, 1000, fmt).Width;
                    }
                    g.DrawString(parts[i], textFont, mainBrush, x, textY, fmt);
                    x += g.MeasureString(parts[i], textFont, 1000, fmt).Width;
                }
            }
        }

        private static int MeasureVariantWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            using (var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
            using (var g = Graphics.FromImage(bmp))
            using (var textFont = new Font("Microsoft YaHei UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point))
            using (var iconFont = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                float width = 0f;
                string body = text;
                if (body.StartsWith("\u26A1 ", StringComparison.Ordinal))
                {
                    width += g.MeasureString("\u26A1", iconFont, 1000, fmt).Width + 3f;
                    body = body.Substring(2);
                }
                width += g.MeasureString(body, textFont, 1000, fmt).Width;
                return (int)Math.Ceiling(width) + 2;
            }
        }

        private void BlitLayeredBitmap(Bitmap bitmap, Point location, Size size)
        {
            IntPtr screenDc = NativeWin32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return;
            IntPtr memDc = NativeWin32.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr prev = NativeWin32.SelectObject(memDc, hBitmap);
            try
            {
                var dst = new NativeWin32.NativePoint { X = location.X, Y = location.Y };
                var src = new NativeWin32.NativePoint { X = 0, Y = 0 };
                var sz = new NativeWin32.NativeSize { CX = size.Width, CY = size.Height };
                var blend = new NativeWin32.BlendFunction
                {
                    BlendOp = NativeWin32.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = NativeWin32.AC_SRC_ALPHA
                };
                NativeWin32.UpdateLayeredWindow(Handle, screenDc, ref dst, ref sz, memDc, ref src, 0, ref blend, NativeWin32.ULW_ALPHA);
            }
            finally
            {
                NativeWin32.SelectObject(memDc, prev);
                NativeWin32.DeleteObject(hBitmap);
                NativeWin32.DeleteDC(memDc);
                NativeWin32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        internal static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;
            if (d <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
