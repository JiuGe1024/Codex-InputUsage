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
    internal sealed class StripRenderModel
    {
        internal string PrefixText;      // e.g. "⚡ 47 tok/s · 本轮 811.8K · 累计 68.6M · 缓存"
        internal bool HasCachePill;
        internal string TurnCacheText;   // e.g. "99%" (null if single-cache fallback)
        internal MetricTier TurnCacheTier;
        internal string SessionCacheText;// e.g. "97%"
        internal MetricTier SessionCacheTier;
        internal bool CompactSlash;      // true => "99%/97%", false => "99% / 97%"
        internal string QuotaPillText;   // e.g. "5h 87%" (null if hidden)
        internal MetricTier QuotaPillTier;

        internal string Describe()
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(PrefixText))
                parts.Add(PrefixText);
            if (HasCachePill)
            {
                if (string.IsNullOrEmpty(TurnCacheText))
                    parts.Add("[" + (SessionCacheText ?? "--") + "]");
                else
                    parts.Add("[" + TurnCacheText + (CompactSlash ? "/" : " / ") + (SessionCacheText ?? "--") + "]");
            }
            if (!string.IsNullOrEmpty(QuotaPillText))
                parts.Add("[" + QuotaPillText + "]");
            return string.Join(" ", parts.ToArray());
        }

        internal static StripRenderModel[] BuildVariants(SessionMetricsSnapshot snap)
        {
            return BuildVariants(snap, null);
        }

        internal static StripRenderModel[] BuildVariants(SessionMetricsSnapshot snap, StripSlotPreferences prefs, DateTime? utcNow = null)
        {
            var p = prefs ?? new StripSlotPreferences();
            if (!p.AnyEnabled()) p = new StripSlotPreferences();

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

            string quota5hText = null;
            MetricTier quota5hTier = MetricTier.Muted;
            if (p.ShowQuota5h)
            {
                double? remaining = snap == null ? null : MetricFormatter.ResolveQuotaRemainingPct(
                    snap.PrimaryUsedPct, snap.PrimaryResetsAt, utcNow ?? DateTime.UtcNow);
                if (remaining.HasValue)
                {
                    double rem = remaining.Value;
                    quota5hText = string.Format(CultureInfo.InvariantCulture, "5h {0}%", (int)Math.Round(rem));
                    quota5hTier = MetricFormatter.ClassifyQuotaRemaining(rem);
                }
                else
                {
                    quota5hText = "5h --";
                    quota5hTier = MetricTier.Muted;
                }
            }

            var raw = new List<StripRenderModel>
            {
                // Level 0: Full sleek strip respecting user slot toggles
                BuildOne(p.ShowTps, false, p.ShowLatestTurn, p.ShowSessionTotal, false, p.ShowCache, true, true, false, p.ShowQuota5h,
                    tps, turn, total, turnCache, turnTier, sessCache, sessTier, quota5hText, quota5hTier),
                // Level 1: Compact TPS unit + tighter slash inside cache pill
                BuildOne(p.ShowTps, true, p.ShowLatestTurn, p.ShowSessionTotal, false, p.ShowCache, true, true, true, p.ShowQuota5h,
                    tps, turn, total, turnCache, turnTier, sessCache, sessTier, quota5hText, quota5hTier),
                // Level 2: Drop "缓存" word, keep pill directly after dot
                BuildOne(p.ShowTps, true, p.ShowLatestTurn, p.ShowSessionTotal, false, p.ShowCache, false, true, true, p.ShowQuota5h,
                    tps, turn, total, turnCache, turnTier, sessCache, sessTier, quota5hText, quota5hTier),
                // Level 3: Drop TPS if other slots exist, keep 本轮 + 累计 + 缓存 + 5h
                BuildOne(p.ShowTps && !p.ShowLatestTurn && !p.ShowSessionTotal && !p.ShowCache && !p.ShowQuota5h, true,
                    p.ShowLatestTurn, p.ShowSessionTotal, false, p.ShowCache, !p.ShowQuota5h, true, true, p.ShowQuota5h,
                    tps, turn, total, turnCache, turnTier, sessCache, sessTier, quota5hText, quota5hTier),
                // Level 4: Very narrow — compact token labels "3.3M / 9.9M"
                BuildOne(p.ShowTps && !p.ShowLatestTurn && !p.ShowSessionTotal && !p.ShowCache && !p.ShowQuota5h, true,
                    p.ShowLatestTurn, p.ShowSessionTotal, true, p.ShowCache, false, true, true, p.ShowQuota5h,
                    tps, turn, total, turnCache, turnTier, sessCache, sessTier, quota5hText, quota5hTier),
                // Level 5: Extreme narrow fallback
                BuildOne(p.ShowTps && !p.ShowLatestTurn && !p.ShowSessionTotal && !p.ShowCache && !p.ShowQuota5h, true,
                    p.ShowLatestTurn && !p.ShowSessionTotal, p.ShowSessionTotal, true, p.ShowCache, false, false, true, p.ShowQuota5h,
                    tps, turn, total, turnCache, turnTier, sessCache, sessTier, quota5hText, quota5hTier)
            };

            var unique = new List<StripRenderModel>(raw.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < raw.Count; i++)
            {
                string key = raw[i].Describe();
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (seen.Add(key)) unique.Add(raw[i]);
            }
            return unique.ToArray();
        }

        private static StripRenderModel BuildOne(
            bool showTps, bool compactTps,
            bool showTurn, bool showTotal, bool compactTokenLabels,
            bool showCache, bool showCacheLabel, bool dualCache, bool compactSlash,
            bool showQuota5h,
            string tps, string turn, string total,
            string turnCache, MetricTier turnTier,
            string sessCache, MetricTier sessTier,
            string quota5hText, MetricTier quota5hTier)
        {
            var segments = new List<string>();
            if (showTps)
                segments.Add(compactTps ? ("\u26A1 " + tps + "/s") : ("\u26A1 " + tps + " tok/s"));

            if (compactTokenLabels)
            {
                if (showTurn && showTotal) segments.Add(turn + " / " + total);
                else if (showTurn) segments.Add(turn);
                else if (showTotal) segments.Add(total);
            }
            else
            {
                if (showTurn) segments.Add("\u672C\u8F6E " + turn);
                if (showTotal) segments.Add("\u7D2F\u8BA1 " + total);
            }

            string prefix = string.Join(" \u00B7 ", segments.ToArray());
            if (showCache)
            {
                if (showCacheLabel)
                    prefix = prefix.Length > 0 ? (prefix + " \u00B7 \u7F13\u5B58") : "\u7F13\u5B58";
                else if (prefix.Length > 0)
                    prefix = prefix + " \u00B7";
            }

            return new StripRenderModel
            {
                PrefixText = prefix,
                HasCachePill = showCache,
                TurnCacheText = (showCache && dualCache) ? turnCache : null,
                TurnCacheTier = (showCache && dualCache) ? turnTier : MetricTier.Muted,
                SessionCacheText = showCache ? sessCache : null,
                SessionCacheTier = showCache ? sessTier : MetricTier.Muted,
                CompactSlash = compactSlash,
                QuotaPillText = showQuota5h ? quota5hText : null,
                QuotaPillTier = showQuota5h ? quota5hTier : MetricTier.Muted
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
        private float _dpiScale = 1f;
        private int _logicalWidth;
        private Rectangle _targetBounds = Rectangle.Empty;
        private ToolStripMenuItem _autoStartItem;
        private ToolStripMenuItem _slotTpsItem;
        private ToolStripMenuItem _slotTurnItem;
        private ToolStripMenuItem _slotTotalItem;
        private ToolStripMenuItem _slotCacheItem;
        private ToolStripMenuItem _slotQuota5hItem;

        internal event EventHandler StripClicked;
        internal event Action<string> SlotToggleRequested;
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

            var slotsMenu = new ToolStripMenuItem("状态条显示项");
            _slotTpsItem = CreateSlotMenuItem("输出速度 (TPS)", "tps");
            _slotTurnItem = CreateSlotMenuItem("本轮 Token", "turn");
            _slotTotalItem = CreateSlotMenuItem("会话累计", "total");
            _slotCacheItem = CreateSlotMenuItem("缓存命中率", "cache");
            _slotQuota5hItem = CreateSlotMenuItem("5h 配额余量", "quota5h");
            slotsMenu.DropDownItems.Add(_slotTpsItem);
            slotsMenu.DropDownItems.Add(_slotTurnItem);
            slotsMenu.DropDownItems.Add(_slotTotalItem);
            slotsMenu.DropDownItems.Add(_slotCacheItem);
            slotsMenu.DropDownItems.Add(_slotQuota5hItem);
            slotsMenu.DropDownItems.Add(new ToolStripSeparator());
            slotsMenu.DropDownItems.Add("恢复默认显示", null, delegate
            {
                if (SlotToggleRequested != null) SlotToggleRequested("reset");
            });
            menu.Items.Add(slotsMenu);

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

        private ToolStripMenuItem CreateSlotMenuItem(string label, string key)
        {
            var item = new ToolStripMenuItem(label) { CheckOnClick = false };
            item.Click += delegate
            {
                if (SlotToggleRequested != null) SlotToggleRequested(key);
            };
            return item;
        }

        internal void SetSlotPreferences(StripSlotPreferences prefs)
        {
            if (prefs == null) return;
            if (_slotTpsItem != null) _slotTpsItem.Checked = prefs.ShowTps;
            if (_slotTurnItem != null) _slotTurnItem.Checked = prefs.ShowLatestTurn;
            if (_slotTotalItem != null) _slotTotalItem.Checked = prefs.ShowSessionTotal;
            if (_slotCacheItem != null) _slotCacheItem.Checked = prefs.ShowCache;
            if (_slotQuota5hItem != null) _slotQuota5hItem.Checked = prefs.ShowQuota5h;
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
            // ApplyDockedState sizes the layered surface from the host's DPI.
            if (m.Msg == OverlayDpi.WmDpiChanged) return;
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
            StripSlotPreferences slotPrefs,
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

            StripRenderModel[] variants = StripRenderModel.BuildVariants(metrics, slotPrefs);
            float scale = dock.DpiScale;
            int height = OverlayDpi.Pixels(StripHeightPx, scale);
            int gap = OverlayDpi.Pixels(10, scale);
            var work = Screen.FromHandle(dock.WindowHandle).WorkingArea;
            int rightEdge;
            if (dock.Source == DockSourceKind.UiaContext)
                rightEdge = dock.ReferenceRect.Left - gap;
            else if (dock.Source == DockSourceKind.UiaModel)
                rightEdge = dock.ReferenceRect.Left - OverlayDpi.Pixels(34, scale);
            else
                rightEdge = dock.ComposerRect.Right - OverlayDpi.Pixels(220, scale);

            rightEdge = Math.Min(rightEdge, work.Right);
            int minLeft = Math.Max(work.Left, Math.Max(dock.ComposerRect.Left, dock.LeftBarrierX) + gap);
            int maxWidth = rightEdge - minLeft;
            if (maxWidth < OverlayDpi.Pixels(50, scale) || work.Height < height)
            {
                HideStrip();
                return false;
            }

            StripRenderModel selected = null;
            int logicalWidth = 0;
            for (int i = 0; i < variants.Length; i++)
            {
                int w = MeasureModelWidth(variants[i]);
                if (OverlayDpi.Pixels(w, scale) <= maxWidth)
                {
                    selected = variants[i];
                    logicalWidth = w;
                    break;
                }
            }

            if (selected == null)
            {
                HideStrip();
                return false;
            }

            int width = OverlayDpi.Pixels(logicalWidth, scale);
            int x = rightEdge - width;
            int y = Math.Max(work.Top, Math.Min(dock.RowCenterY - height / 2, work.Bottom - height));

            Rectangle nextBounds = new Rectangle(x, y, width, height);
            string nextKey = selected.Describe() + "|" + (int)selected.TurnCacheTier + "|" + (int)selected.SessionCacheTier + "|" + (int)selected.QuotaPillTier;
            bool needRepaint = !string.Equals(_modelKey, nextKey, StringComparison.Ordinal)
                || _palette != palette
                || _dpiScale != scale
                || _targetBounds.Size != nextBounds.Size
                || !Visible;
            bool moved = _targetBounds != nextBounds;

            _palette = palette;
            _dpiScale = scale;
            _logicalWidth = logicalWidth;
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
                BlitCurrentSurface();
                if (IsHandleCreated)
                {
                    NativeWin32.SetWindowPos(
                        Handle, NativeWin32.HWND_TOPMOST,
                        nextBounds.X, nextBounds.Y, nextBounds.Width, nextBounds.Height,
                        NativeWin32.SWP_NOACTIVATE | NativeWin32.SWP_SHOWWINDOW);
                }
            }
            else if (needRepaint)
            {
                if (moved) Bounds = nextBounds;
                BlitCurrentSurface();
                if (IsHandleCreated)
                {
                    NativeWin32.SetWindowPos(
                        Handle, NativeWin32.HWND_TOPMOST,
                        nextBounds.X, nextBounds.Y, nextBounds.Width, nextBounds.Height,
                        NativeWin32.SWP_NOACTIVATE | NativeWin32.SWP_SHOWWINDOW);
                }
            }
            else if (moved && IsHandleCreated)
            {
                // Position-only change: zero-allocation fast move without re-blitting the layered surface!
                NativeWin32.SetWindowPos(
                    Handle, NativeWin32.HWND_TOPMOST,
                    nextBounds.X, nextBounds.Y, 0, 0,
                    NativeWin32.SWP_NOSIZE | NativeWin32.SWP_NOACTIVATE | NativeWin32.SWP_NOREDRAW);
            }

            return true;
        }

        /// <summary>
        /// Ultra-fast (<0.1ms) geometric translation during window drag: moves the layered window
        /// without repainting a single pixel so it stays glued to Codex at 60+ FPS.
        /// </summary>
        internal Rectangle MoveFast(int newX, int newY)
        {
            if (!Visible || !IsHandleCreated || _targetBounds.IsEmpty) return _targetBounds;
            if (_targetBounds.X == newX && _targetBounds.Y == newY) return _targetBounds;
            _targetBounds = new Rectangle(newX, newY, _targetBounds.Width, _targetBounds.Height);
            NativeWin32.SetWindowPos(
                Handle, NativeWin32.HWND_TOPMOST,
                newX, newY, 0, 0,
                NativeWin32.SWP_NOSIZE | NativeWin32.SWP_NOACTIVATE | NativeWin32.SWP_NOREDRAW);
            return _targetBounds;
        }

        internal void HideStrip()
        {
            _allowShow = false;
            _hovered = false;
            if (Visible) Hide();
        }

        internal static Bitmap RenderPreviewBitmap(StripRenderModel model, ThemePalette palette, bool hovered, bool includeBackdrop, float scale = 1f)
        {
            int width = MeasureModelWidth(model);
            var bmp = new Bitmap(OverlayDpi.Pixels(width, scale), OverlayDpi.Pixels(StripHeightPx, scale), PixelFormat.Format32bppPArgb);
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
                g.ScaleTransform(scale, scale);
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
                    g.ScaleTransform(_dpiScale, _dpiScale);
                    DrawStripContent(g, new Size(_logicalWidth, StripHeightPx), _model, _palette, _hovered || _popoverOpen);
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

            using (var textFont = OverlayDpi.FontAt96Dpi("Microsoft YaHei UI", 8.8f, FontStyle.Regular))
            using (var pillFont = OverlayDpi.FontAt96Dpi("Segoe UI Semibold", 8.5f, FontStyle.Regular))
            using (var mainBrush = new SolidBrush(palette.StripText))
            using (var mutedBrush = new SolidBrush(palette.StripTextSecondary))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                float x = PaddingXPx;

                string body = model.PrefixText ?? string.Empty;
                bool hasPrefix = body.Length > 0;
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

                if (body.Length > 0)
                {
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

                int pillY = (size.Height - PillHeightPx) / 2;
                int fillAlpha = palette.Kind == CodexThemeKind.Dark ? 34 : 24;
                int borderAlpha = palette.Kind == CodexThemeKind.Dark ? 78 : 62;
                SizeF pillSample = g.MeasureString("99%", pillFont, 1000, fmt);
                bool drewPrev = hasPrefix;

                // Optional semantic-tinted cache badge pill: [99% / 97%]
                if (model.HasCachePill)
                {
                    if (drewPrev) x += PillGapPx;
                    string slash = model.CompactSlash ? "/" : " / ";
                    float turnW = 0f;
                    float slashW = 0f;
                    float sessW = g.MeasureString(model.SessionCacheText ?? "--", pillFont, 1000, fmt).Width;
                    float innerW;
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
                    var pillRect = new Rectangle((int)Math.Round(x), pillY, pillW, PillHeightPx);

                    MetricTier badgeTier = !string.IsNullOrEmpty(model.TurnCacheText) && model.TurnCacheTier != MetricTier.Muted
                        ? model.TurnCacheTier
                        : model.SessionCacheTier;
                    Color badgeAccent = badgeTier == MetricTier.Muted ? palette.StripTextSecondary : palette.ForTier(badgeTier);

                    using (var pillPath = CreateRoundedPath(pillRect, 5))
                    using (var pillFill = new SolidBrush(Color.FromArgb(fillAlpha, badgeAccent)))
                    using (var pillBorder = new Pen(Color.FromArgb(borderAlpha, badgeAccent), 1f))
                    {
                        g.FillPath(pillFill, pillPath);
                        g.DrawPath(pillBorder, pillPath);
                    }

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

                    x = pillRect.Right;
                    drewPrev = true;
                }

                // Optional semantic-tinted 5h quota pill: [5h 87%]
                if (!string.IsNullOrEmpty(model.QuotaPillText))
                {
                    if (drewPrev) x += PillGapPx;
                    float qW = g.MeasureString(model.QuotaPillText, pillFont, 1000, fmt).Width;
                    int pillW = (int)Math.Ceiling(qW) + (PillPadXPx * 2);
                    var pillRect = new Rectangle((int)Math.Round(x), pillY, pillW, PillHeightPx);
                    Color qAccent = model.QuotaPillTier == MetricTier.Muted ? palette.StripTextSecondary : palette.ForTier(model.QuotaPillTier);

                    using (var pillPath = CreateRoundedPath(pillRect, 5))
                    using (var pillFill = new SolidBrush(Color.FromArgb(fillAlpha, qAccent)))
                    using (var pillBorder = new Pen(Color.FromArgb(borderAlpha, qAccent), 1f))
                    {
                        g.FillPath(pillFill, pillPath);
                        g.DrawPath(pillBorder, pillPath);
                    }

                    float px = pillRect.X + PillPadXPx;
                    float py = pillRect.Y + (PillHeightPx - pillSample.Height) / 2f;
                    using (var qb = new SolidBrush(qAccent))
                        g.DrawString(model.QuotaPillText, pillFont, qb, px, py, fmt);
                }
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
            using (var textFont = OverlayDpi.FontAt96Dpi("Microsoft YaHei UI", 8.8f, FontStyle.Regular))
            using (var pillFont = OverlayDpi.FontAt96Dpi("Segoe UI Semibold", 8.5f, FontStyle.Regular))
            using (var fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;

                float width = PaddingXPx;
                string body = model.PrefixText ?? string.Empty;
                bool hasPrev = body.Length > 0;
                if (body.StartsWith("\u26A1 ", StringComparison.Ordinal))
                {
                    width += BoltSlotPx;
                    body = body.Substring(2);
                }
                if (body.Length > 0)
                    width += g.MeasureString(body, textFont, 1000, fmt).Width;

                if (model.HasCachePill)
                {
                    if (hasPrev) width += PillGapPx;
                    string slash = model.CompactSlash ? "/" : " / ";
                    float innerW = g.MeasureString(model.SessionCacheText ?? "--", pillFont, 1000, fmt).Width;
                    if (!string.IsNullOrEmpty(model.TurnCacheText))
                    {
                        innerW += g.MeasureString(model.TurnCacheText, pillFont, 1000, fmt).Width;
                        innerW += g.MeasureString(slash, textFont, 1000, fmt).Width;
                    }
                    width += (float)Math.Ceiling(innerW) + (PillPadXPx * 2);
                    hasPrev = true;
                }

                if (!string.IsNullOrEmpty(model.QuotaPillText))
                {
                    if (hasPrev) width += PillGapPx;
                    float qW = g.MeasureString(model.QuotaPillText, pillFont, 1000, fmt).Width;
                    width += (float)Math.Ceiling(qW) + (PillPadXPx * 2);
                }

                width += PaddingXPx + 2f;
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
