using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace CodexInputUsage
{
    internal sealed class StripRenderModel
    {
        internal string PrefixText;      // e.g. "⚡ 47 tok/s · 本轮 811.8K · 累计 68.6M · 缓存"
        internal string TurnCacheText;   // e.g. "99%" (null if single-cache fallback)
        internal MetricTier TurnCacheTier;
        internal string SessionCacheText;// e.g. "97%"
        internal MetricTier SessionCacheTier;
        internal bool CompactSlash;      // true => "99%/97%", false => "99% / 97%"

        internal string Describe()
        {
            if (string.IsNullOrEmpty(TurnCacheText))
                return PrefixText + " [" + SessionCacheText + "]";
            string sep = CompactSlash ? "/" : " / ";
            return PrefixText + " [" + TurnCacheText + sep + SessionCacheText + "]";
        }

        internal static StripRenderModel[] BuildVariants(SessionMetricsSnapshot snap)
        {
            string tps = snap != null ? MetricFormatter.FormatTps(snap.DisplayTps, snap.TpsIsHeld) : "--";
            bool hasTurn = snap != null && snap.LatestTurn != null && snap.LatestTurn.TotalTokens > 0;
            string turn = hasTurn ? MetricFormatter.CompactTokens(snap.LatestTurn.TotalTokens) : "--";
            double? turnCacheVal = hasTurn ? snap.LatestTurn.CacheHitPct : null;
            string turnCache = hasTurn ? MetricFormatter.FormatCacheCompact(turnCacheVal) : "--";
            MetricTier turnTier = MetricFormatter.ClassifyCache(turnCacheVal);

            bool hasSess = snap != null && snap.HasUsage;
            string total = hasSess ? MetricFormatter.CompactTokens(snap.TotalTokens) : "--";
            double? sessCacheVal = hasSess ? snap.SessionCacheHitPct : null;
            string sessCache = hasSess ? MetricFormatter.FormatCacheCompact(sessCacheVal) : "--";
            MetricTier sessTier = MetricFormatter.ClassifyCache(sessCacheVal);

            return new StripRenderModel[]
            {
                // Level 0: Full sleek strip — proper "缓存" label + semantic dual pill [99% / 97%]
                new StripRenderModel
                {
                    PrefixText = string.Format("\u26A1 {0} tok/s \u00B7 \u672C\u8F6E {1} \u00B7 \u7D2F\u8BA1 {2} \u00B7 \u7F13\u5B58", tps, turn, total),
                    TurnCacheText = turnCache, TurnCacheTier = turnTier,
                    SessionCacheText = sessCache, SessionCacheTier = sessTier,
                    CompactSlash = false
                },
                // Level 1: Compact speed unit + tighter slash inside pill
                new StripRenderModel
                {
                    PrefixText = string.Format("\u26A1 {0}/s \u00B7 \u672C\u8F6E {1} \u00B7 \u7D2F\u8BA1 {2} \u00B7 \u7F13\u5B58", tps, turn, total),
                    TurnCacheText = turnCache, TurnCacheTier = turnTier,
                    SessionCacheText = sessCache, SessionCacheTier = sessTier,
                    CompactSlash = true
                },
                // Level 2: Drop "缓存" word, keep pill directly after dot
                new StripRenderModel
                {
                    PrefixText = string.Format("\u26A1 {0}/s \u00B7 \u672C\u8F6E {1} \u00B7 \u7D2F\u8BA1 {2} \u00B7", tps, turn, total),
                    TurnCacheText = turnCache, TurnCacheTier = turnTier,
                    SessionCacheText = sessCache, SessionCacheTier = sessTier,
                    CompactSlash = true
                },
                // Level 3: Drop TPS, preserve 本轮 + 累计 + 缓存 pill
                new StripRenderModel
                {
                    PrefixText = string.Format("\u672C\u8F6E {0} \u00B7 \u7D2F\u8BA1 {1} \u00B7 \u7F13\u5B58", turn, total),
                    TurnCacheText = turnCache, TurnCacheTier = turnTier,
                    SessionCacheText = sessCache, SessionCacheTier = sessTier,
                    CompactSlash = true
                },
                // Level 4: Very narrow
                new StripRenderModel
                {
                    PrefixText = string.Format("{0} / {1} \u00B7", turn, total),
                    TurnCacheText = turnCache, TurnCacheTier = turnTier,
                    SessionCacheText = sessCache, SessionCacheTier = sessTier,
                    CompactSlash = true
                },
                // Level 5: Extreme narrow fallback
                new StripRenderModel
                {
                    PrefixText = total + " \u00B7",
                    TurnCacheText = null, TurnCacheTier = MetricTier.Muted,
                    SessionCacheText = sessCache, SessionCacheTier = sessTier,
                    CompactSlash = true
                }
            };
        }
    }

    internal sealed class StripForm : Form
    {
        private const int WsExToolWindow = 0x00000080;
        private const int WsExLayered = 0x00080000;
        private const int WsExNoActivate = 0x08000000;
        private const int WmMouseActivate = 0x0021;
        private const int MaNoActivate = 3;

        private const int StripHeightPx = 24;
        private const int PaddingXPx = 7;
        private const int BoltSlotPx = 12;
        private const int PillGapPx = 5;
        private const int PillPadXPx = 6;
        private const int PillHeightPx = 18;

        private ThemePalette _palette = ThemePalette.Light;
        private StripRenderModel _model;
        private string _modelKey = string.Empty;
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
            _autoStartItem = new ToolStripMenuItem("跟随原生 Codex 自动启动") { CheckOnClick = false };
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

            StripRenderModel[] variants = StripRenderModel.BuildVariants(metrics);
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

            StripRenderModel selected = null;
            int selectedWidth = 0;
            for (int i = 0; i < variants.Length; i++)
            {
                int w = MeasureModelWidth(variants[i]);
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
            string nextKey = selected.Describe() + "|" + (int)selected.TurnCacheTier + "|" + (int)selected.SessionCacheTier;
            bool needRepaint = !string.Equals(_modelKey, nextKey, StringComparison.Ordinal)
                || _palette != palette
                || _targetBounds.Size != nextBounds.Size
                || !Visible;
            bool moved = _targetBounds != nextBounds;

            _palette = palette;
            _model = selected;
            _modelKey = nextKey;
            _targetBounds = nextBounds;
            _allowShow = true;
            placedBounds = nextBounds;
            chosenVariant = selected.Describe();

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

        internal static Bitmap RenderPreviewBitmap(StripRenderModel model, ThemePalette palette, bool hovered, bool includeBackdrop)
        {
            int width = MeasureModelWidth(model);
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
                DrawStripContent(g, new Size(width, StripHeightPx), model, palette, hovered);
            }
            return bmp;
        }

        private void BlitCurrentSurface()
        {
            if (!IsHandleCreated || IsDisposed || _targetBounds.Width <= 0 || _targetBounds.Height <= 0 || _model == null)
                return;

            using (var bmp = new Bitmap(_targetBounds.Width, _targetBounds.Height, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    DrawStripContent(g, _targetBounds.Size, _model, _palette, _hovered || _popoverOpen);
                }
                BlitLayeredBitmap(bmp, _targetBounds.Location, _targetBounds.Size);
            }
        }

        private static void DrawStripContent(Graphics g, Size size, StripRenderModel model, ThemePalette palette, bool showOuterPill)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var rect = new Rectangle(0, 0, size.Width - 1, size.Height - 1);
            using (var path = CreateRoundedPath(rect, 6))
            {
                if (showOuterPill)
                {
                    using (var fill = new SolidBrush(palette.StripHoverFill))
                        g.FillPath(fill, path);
                    using (var border = new Pen(palette.StripHoverBorder, 1f))
                        g.DrawPath(border, path);
                }
                else
                {
                    // Alpha=1 makes the tight strip bounds reliably clickable while visually 100% transparent
                    using (var hitFill = new SolidBrush(Color.FromArgb(1, 128, 128, 128)))
                        g.FillPath(hitFill, path);
                }
            }

            if (model == null) return;

            using (var textFont = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Regular, GraphicsUnit.Point))
            using (var pillFont = new Font("Segoe UI Semibold", 8.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (var mainBrush = new SolidBrush(palette.StripText))
            using (var mutedBrush = new SolidBrush(palette.StripTextSecondary))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                float x = PaddingXPx;

                string body = model.PrefixText ?? string.Empty;
                if (body.StartsWith("\u26A1 ", StringComparison.Ordinal))
                {
                    // Crisp vector lightning bolt in warm amber-gold so it stands out cleanly
                    Color boltColor = palette.Kind == CodexThemeKind.Dark
                        ? Color.FromArgb(250, 251, 184, 52)
                        : Color.FromArgb(250, 232, 126, 20);
                    DrawLightningBolt(g, x, (size.Height - 13f) / 2f, boltColor);
                    x += BoltSlotPx;
                    body = body.Substring(2);
                }

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

                // Sleek semantic-tinted cache badge pill: [99% / 97%]
                x += PillGapPx;
                string slash = model.CompactSlash ? "/" : " / ";
                float innerW = 0f;
                float turnW = 0f;
                float slashW = 0f;
                float sessW = g.MeasureString(model.SessionCacheText ?? "--", pillFont, 1000, fmt).Width;
                if (!string.IsNullOrEmpty(model.TurnCacheText))
                {
                    turnW = g.MeasureString(model.TurnCacheText, pillFont, 1000, fmt).Width;
                    slashW = g.MeasureString(slash, textFont, 1000, fmt).Width;
                    innerW = turnW + slashW + sessW;
                }
                else
                {
                    innerW = sessW;
                }

                int pillW = (int)Math.Ceiling(innerW) + (PillPadXPx * 2);
                int pillY = (size.Height - PillHeightPx) / 2;
                var pillRect = new Rectangle((int)Math.Round(x), pillY, pillW, PillHeightPx);

                // Tint badge by primary displayed cache tier (turn tier if present, else session tier)
                MetricTier badgeTier = !string.IsNullOrEmpty(model.TurnCacheText) && model.TurnCacheTier != MetricTier.Muted
                    ? model.TurnCacheTier
                    : model.SessionCacheTier;
                Color badgeAccent = badgeTier == MetricTier.Muted ? palette.StripTextSecondary : palette.ForTier(badgeTier);
                int fillAlpha = palette.Kind == CodexThemeKind.Dark ? 34 : 24;
                int borderAlpha = palette.Kind == CodexThemeKind.Dark ? 78 : 62;

                using (var pillPath = CreateRoundedPath(pillRect, 5))
                using (var pillFill = new SolidBrush(Color.FromArgb(fillAlpha, badgeAccent)))
                using (var pillBorder = new Pen(Color.FromArgb(borderAlpha, badgeAccent), 1f))
                {
                    g.FillPath(pillFill, pillPath);
                    g.DrawPath(pillBorder, pillPath);
                }

                SizeF pillSample = g.MeasureString("99%", pillFont, 1000, fmt);
                float px = pillRect.X + PillPadXPx;
                float py = pillRect.Y + (PillHeightPx - pillSample.Height) / 2f;

                if (!string.IsNullOrEmpty(model.TurnCacheText))
                {
                    Color tCol = model.TurnCacheTier == MetricTier.Muted ? palette.StripTextSecondary : palette.ForTier(model.TurnCacheTier);
                    using (var tb = new SolidBrush(tCol))
                        g.DrawString(model.TurnCacheText, pillFont, tb, px, py, fmt);
                    px += turnW;
                    g.DrawString(slash, textFont, mutedBrush, px, py - 0.5f, fmt);
                    px += slashW;
                }

                Color sCol = model.SessionCacheTier == MetricTier.Muted ? palette.StripTextSecondary : palette.ForTier(model.SessionCacheTier);
                using (var sb = new SolidBrush(sCol))
                    g.DrawString(model.SessionCacheText ?? "--", pillFont, sb, px, py, fmt);
            }
        }

        private static void DrawLightningBolt(Graphics g, float x, float y, Color color)
        {
            var pts = new PointF[]
            {
                new PointF(x + 5.6f, y + 0.2f),
                new PointF(x + 0.6f, y + 7.0f),
                new PointF(x + 4.4f, y + 7.0f),
                new PointF(x + 3.2f, y + 12.8f),
                new PointF(x + 8.6f, y + 5.6f),
                new PointF(x + 4.8f, y + 5.6f)
            };
            using (var brush = new SolidBrush(color))
                g.FillPolygon(brush, pts);
        }

        private static int MeasureModelWidth(StripRenderModel model)
        {
            if (model == null) return 0;
            using (var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
            using (var g = Graphics.FromImage(bmp))
            using (var textFont = new Font("Microsoft YaHei UI", 8.8f, FontStyle.Regular, GraphicsUnit.Point))
            using (var pillFont = new Font("Segoe UI Semibold", 8.5f, FontStyle.Regular, GraphicsUnit.Point))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;

                float width = PaddingXPx;
                string body = model.PrefixText ?? string.Empty;
                if (body.StartsWith("\u26A1 ", StringComparison.Ordinal))
                {
                    width += BoltSlotPx;
                    body = body.Substring(2);
                }
                width += g.MeasureString(body, textFont, 1000, fmt).Width;
                width += PillGapPx;

                string slash = model.CompactSlash ? "/" : " / ";
                float innerW = g.MeasureString(model.SessionCacheText ?? "--", pillFont, 1000, fmt).Width;
                if (!string.IsNullOrEmpty(model.TurnCacheText))
                {
                    innerW += g.MeasureString(model.TurnCacheText, pillFont, 1000, fmt).Width;
                    innerW += g.MeasureString(slash, textFont, 1000, fmt).Width;
                }
                width += (float)Math.Ceiling(innerW) + (PillPadXPx * 2) + PaddingXPx + 2f;
                return (int)Math.Ceiling(width);
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
