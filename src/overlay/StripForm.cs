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
        internal string TpsText;
        internal MetricTier TpsTier;
        internal string TurnTokenText;
        internal string TotalTokenText;
        internal string TurnCacheText;
        internal MetricTier TurnCacheTier;
        internal string SessionCacheText;
        internal MetricTier SessionCacheTier;
        internal bool CompactSlash;
        internal string QuotaPillText;
        internal MetricTier QuotaPillTier;

        internal string Slash { get { return CompactSlash ? "/" : " / "; } }

        internal string Describe()
        {
            var parts = new List<string>();
            if (TpsText != null) parts.Add("\u26A1 " + TpsText);
            AddPillDescription(parts, TurnTokenText, TotalTokenText);
            AddPillDescription(parts, TurnCacheText, SessionCacheText);
            AddPillDescription(parts, QuotaPillText, null);
            return string.Join(" | ", parts.ToArray());
        }

        private void AddPillDescription(List<string> parts, string first, string second)
        {
            if (first != null && second != null) parts.Add("[" + first + Slash + second + "]");
            else if (first != null || second != null) parts.Add("[" + (first ?? second) + "]");
        }

        internal string RenderKey()
        {
            return Describe() + "|" + (int)TpsTier + "|" + (int)TurnCacheTier
                + "|" + (int)SessionCacheTier + "|" + (int)QuotaPillTier;
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

            var full = new StripRenderModel
            {
                TpsText = p.ShowTps ? tps + " tok/s" : null,
                TpsTier = !p.ShowTps || tps == "--" ? MetricTier.Muted : MetricFormatter.ClassifySpeed(snap.DisplayTps),
                TurnTokenText = p.ShowLatestTurn ? turn : null,
                TotalTokenText = p.ShowSessionTotal ? total : null,
                TurnCacheText = p.ShowCache ? turnCache : null,
                TurnCacheTier = p.ShowCache ? turnTier : MetricTier.Muted,
                SessionCacheText = p.ShowCache ? sessCache : null,
                SessionCacheTier = p.ShowCache ? sessTier : MetricTier.Muted,
                QuotaPillText = quota5hText,
                QuotaPillTier = quota5hTier
            };
            var compact = (StripRenderModel)full.MemberwiseClone();
            compact.TpsText = p.ShowTps ? tps + "/s" : null;
            compact.CompactSlash = true;

            var narrow = (StripRenderModel)compact.MemberwiseClone();
            if (p.ShowLatestTurn || p.ShowSessionTotal || p.ShowCache || p.ShowQuota5h)
            {
                narrow.TpsText = null;
                narrow.TpsTier = MetricTier.Muted;
            }

            var smallest = (StripRenderModel)narrow.MemberwiseClone();
            if (smallest.TotalTokenText != null) smallest.TurnTokenText = null;
            smallest.TurnCacheText = null;
            smallest.TurnCacheTier = MetricTier.Muted;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unique = new List<StripRenderModel>(4);
            foreach (var model in new[] { full, compact, narrow, smallest })
            {
                if (seen.Add(model.Describe())) unique.Add(model);
            }
            return unique.ToArray();
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
        private const int SeparatorGapPx = 7;

        private static readonly string StripFontFamily = FindStripFontFamily();
        private static readonly FontStyle StripFontStyle = StripFontFamily == "Segoe UI" ? FontStyle.Bold : FontStyle.Regular;

        private enum SlotKind { Speed, Tokens, Cache, Quota }

        private sealed class SlotLayout
        {
            internal SlotKind Kind;
            internal Rectangle Bounds;
            internal string Lead;
            internal string Tail;
            internal float LeadWidth;
            internal float SlashWidth;
        }
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
            string nextKey = selected.RenderKey();
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
                DrawStripContent(g, new Size(width, StripHeightPx), model, palette, hovered, scale);
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
                    DrawStripContent(g, new Size(_logicalWidth, StripHeightPx), _model, _palette, _hovered || _popoverOpen, _dpiScale);
                }
                BlitLayeredBitmap(bmp, _targetBounds.Location, _targetBounds.Size);
            }
        }

        private static void DrawStripContent(Graphics g, Size size, StripRenderModel model, ThemePalette palette, bool showOuterPill, float scale)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using (var font = OverlayDpi.FontAt96Dpi(StripFontFamily, 8.6f, StripFontStyle))
            using (var fmt = CreateSlotFormat())
            {
                var slots = LayoutSlots(g, model, font, fmt);
                float textY = (size.Height - g.MeasureString("Ag", font, 1000, fmt).Height) / 2f;
                g.ScaleTransform(scale, scale);

                using (var path = CreateRoundedPath(new Rectangle(0, 0, size.Width - 1, size.Height - 1), 6))
                using (var fill = new SolidBrush(showOuterPill ? palette.StripHoverFill : Color.FromArgb(1, 128, 128, 128)))
                {
                    g.FillPath(fill, path);
                    if (showOuterPill)
                    {
                        using (var border = new Pen(palette.StripHoverBorder))
                            g.DrawPath(border, path);
                    }
                }

                bool dark = palette.Kind == CodexThemeKind.Dark;
                Color separatorColor = dark ? Color.FromArgb(56, 255, 255, 255) : Color.FromArgb(52, 15, 23, 42);
                using (var separator = new Pen(separatorColor, 1f))
                using (var muted = new SolidBrush(palette.StripTextSecondary))
                {
                    for (int i = 0; i < slots.Count; i++)
                    {
                        var slot = slots[i];
                        if (i > 0)
                        {
                            float x = slot.Bounds.Left - SeparatorGapPx - 0.5f;
                            g.DrawLine(separator, x, 6, x, 17);
                        }

                        if (slot.Kind == SlotKind.Speed)
                        {
                            Color color = StripTierColor(palette, model.TpsTier);
                            DrawLightningBolt(g, slot.Bounds.Left, (size.Height - 13f) / 2f, color);
                            using (var text = new SolidBrush(color))
                                g.DrawString(slot.Lead, font, text, slot.Bounds.Left + BoltSlotPx, textY, fmt);
                        }
                        else
                        {
                            DrawSlotPill(g, slot, model, palette, font, fmt, muted, textY, dark);
                        }
                    }
                }
            }
        }

        private static void DrawSlotPill(Graphics g, SlotLayout slot, StripRenderModel model, ThemePalette palette,
            Font font, StringFormat fmt, SolidBrush muted, float textY, bool dark)
        {
            bool tokens = slot.Kind == SlotKind.Tokens;
            MetricTier leadTier = slot.Kind == SlotKind.Quota ? model.QuotaPillTier
                : (slot.Tail != null ? model.TurnCacheTier : model.SessionCacheTier);
            MetricTier frameTier = leadTier == MetricTier.Muted && slot.Tail != null ? model.SessionCacheTier : leadTier;
            Color tint = tokens ? (dark ? Color.FromArgb(96, 165, 250) : Color.FromArgb(37, 99, 235))
                : StripTierColor(palette, frameTier);
            Color tokenText = dark ? Color.FromArgb(147, 197, 253) : Color.FromArgb(37, 99, 235);
            int fillAlpha = tokens ? (dark ? 31 : 21) : (dark ? 34 : 24);
            int borderAlpha = tokens ? (dark ? 64 : 54) : (dark ? 78 : 62);

            using (var path = CreateRoundedPath(slot.Bounds, 5))
            using (var fill = new SolidBrush(Color.FromArgb(fillAlpha, tint)))
            using (var border = new Pen(Color.FromArgb(borderAlpha, tint)))
            using (var lead = new SolidBrush(tokens ? tokenText : StripTierColor(palette, leadTier)))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
                float x = slot.Bounds.Left + PillPadXPx;
                g.DrawString(slot.Lead, font, lead, x, textY, fmt);
                if (slot.Tail != null)
                {
                    x += slot.LeadWidth;
                    g.DrawString(model.Slash, font, muted, x, textY, fmt);
                    using (var tail = new SolidBrush(tokens ? tokenText : StripTierColor(palette, model.SessionCacheTier)))
                        g.DrawString(slot.Tail, font, tail, x + slot.SlashWidth, textY, fmt);
                }
            }
        }

        private static Color StripTierColor(ThemePalette palette, MetricTier tier)
        {
            return tier == MetricTier.Muted ? palette.StripTextSecondary : palette.ForTier(tier);
        }

        private static string FindStripFontFamily()
        {
            using (var fonts = new InstalledFontCollection())
            {
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FontFamily family in fonts.Families)
                {
                    using (family)
                        if (family.IsStyleAvailable(FontStyle.Regular)) installed.Add(family.Name);
                }
                foreach (string name in new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold" })
                    if (installed.Contains(name)) return name;
                return "Segoe UI";
            }
        }

        private static StringFormat CreateSlotFormat()
        {
            var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
            fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
            return fmt;
        }

        private static List<SlotLayout> LayoutSlots(Graphics g, StripRenderModel model, Font font, StringFormat fmt)
        {
            var slots = new List<SlotLayout>(4);
            AddSlot(slots, g, font, fmt, SlotKind.Speed, model.TpsText, null, model.Slash);
            AddSlot(slots, g, font, fmt, SlotKind.Tokens, model.TurnTokenText, model.TotalTokenText, model.Slash);
            AddSlot(slots, g, font, fmt, SlotKind.Cache, model.TurnCacheText, model.SessionCacheText, model.Slash);
            AddSlot(slots, g, font, fmt, SlotKind.Quota, model.QuotaPillText, null, model.Slash);
            return slots;
        }

        private static void AddSlot(List<SlotLayout> slots, Graphics g, Font font, StringFormat fmt,
            SlotKind kind, string first, string second, string slash)
        {
            string lead = first ?? second;
            if (lead == null) return;
            string tail = first != null ? second : null;
            float leadWidth = g.MeasureString(lead, font, 1000, fmt).Width;
            float slashWidth = tail == null ? 0 : g.MeasureString(slash, font, 1000, fmt).Width;
            float tailWidth = tail == null ? 0 : g.MeasureString(tail, font, 1000, fmt).Width;
            int left = slots.Count == 0 ? PaddingXPx : slots[slots.Count - 1].Bounds.Right + (2 * SeparatorGapPx) + 1;
            int inset = kind == SlotKind.Speed ? BoltSlotPx : 2 * PillPadXPx;
            int width = inset + (int)Math.Ceiling(leadWidth + slashWidth + tailWidth);
            slots.Add(new SlotLayout
            {
                Kind = kind,
                Lead = lead,
                Tail = tail,
                LeadWidth = leadWidth,
                SlashWidth = slashWidth,
                Bounds = new Rectangle(left, (StripHeightPx - PillHeightPx) / 2, width, PillHeightPx)
            });
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
            using (var bmp = new Bitmap(1, 1, PixelFormat.Format32bppPArgb))
            using (var g = Graphics.FromImage(bmp))
            using (var font = OverlayDpi.FontAt96Dpi(StripFontFamily, 8.6f, StripFontStyle))
            using (var fmt = CreateSlotFormat())
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var slots = LayoutSlots(g, model, font, fmt);
                return slots.Count == 0 ? 0 : slots[slots.Count - 1].Bounds.Right + PaddingXPx + 2;
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
