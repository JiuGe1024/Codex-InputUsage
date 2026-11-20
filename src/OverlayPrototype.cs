using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class OverlayPrototype
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, System.Text.StringBuilder id);

    [STAThread]
    private static void Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        if (args.Length == 2 && args[0] == "--probe")
        {
            var placement = Locate(false);
            File.WriteAllText(args[1], placement.Area.IsEmpty ? placement.Reason : placement.Area.ToString());
            return;
        }
        bool owns;
        using (var single = new System.Threading.Mutex(true, @"Local\CodexInputUsage.OverlayPrototype", out owns))
        {
            if (!owns) return;
            Application.EnableVisualStyles();
            Application.Run(new Overlay());
        }
    }

    private sealed class Placement
    {
        internal IntPtr Window;
        internal Rectangle Area;
        internal string Reason;
    }

    private static bool IsCodex(Process process)
    {
        string path = process.MainModule.FileName;
        string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + "\\";
        if (!path.StartsWith(windowsApps + "OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)) return false;
        uint length = 0;
        if (GetApplicationUserModelId(process.Handle, ref length, null) != 122 || length == 0 || length > 512) return false;
        var id = new System.Text.StringBuilder((int)length);
        return GetApplicationUserModelId(process.Handle, ref length, id) == 0
            && id.ToString().StartsWith("OpenAI.Codex_2p2nqsd0c76g0!", StringComparison.OrdinalIgnoreCase);
    }

    private static Placement Locate(bool requireForeground)
    {
        try
        {
            var foreground = GetForegroundWindow();
            foreach (var process in Process.GetProcessesByName("ChatGPT"))
            {
                using (process)
                {
                    var hwnd = process.MainWindowHandle;
                    if (hwnd == IntPtr.Zero || (requireForeground && hwnd != foreground) || IsIconic(hwnd) || !IsCodex(process)) continue;
                    return LocateComposer(hwnd);
                }
            }
            return new Placement { Reason = "codex-not-foreground" };
        }
        catch (ElementNotAvailableException) { return new Placement { Reason = "accessibility-element-disappeared" }; }
        catch (System.ComponentModel.Win32Exception) { return new Placement { Reason = "window-query-failed" }; }
    }

    private static Rectangle Bounds(System.Windows.Rect rect)
    {
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return Rectangle.Empty;
        return Rectangle.FromLTRB((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top),
            (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
    }

    private static Placement LocateComposer(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var types = new[] { ControlType.Document, ControlType.Edit, ControlType.Button, ControlType.SplitButton,
            ControlType.ComboBox, ControlType.CheckBox, ControlType.Hyperlink, ControlType.ScrollBar, ControlType.RadioButton, ControlType.MenuItem };
        var conditions = types.Select(t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t)).ToArray();
        var elements = root.FindAll(TreeScope.Descendants, new OrCondition(conditions))
            .Cast<AutomationElement>().Select(el => el.Current)
            .Where(info => !info.IsOffscreen && !info.BoundingRectangle.IsEmpty).ToArray();
        var editors = elements.Where(info => info.ControlType == ControlType.Edit).Select(info => Bounds(info.BoundingRectangle)).ToArray();
        var docs = elements.Where(info => info.ControlType == ControlType.Document).Select(info => Bounds(info.BoundingRectangle)).ToArray();
        var matches = (from box in editors from doc in docs
                       where doc.Contains(box) && box.Width >= 240 && box.Bottom >= doc.Bottom - 240
                       select new { box, doc }).ToArray();
        if (matches.Length != 1) return new Placement { Reason = "composer-ambiguous" };

        var match = matches[0];
        var screen = Screen.FromHandle(hwnd).WorkingArea;
        var obstacles = elements.Where(info => info.ControlType != ControlType.Document)
            .Select(info => Bounds(info.BoundingRectangle)).ToArray();
        var candidate = FindSpace(match.box, match.doc, screen, obstacles);
        NativeRect window;
        if (candidate.IsEmpty && GetWindowRect(hwnd, out window))
        {
            var frame = Rectangle.FromLTRB(window.Left, window.Top, window.Right, window.Bottom);
            candidate = FindBelowWindow(match.box, frame, screen);
        }
        return new Placement { Window = hwnd, Area = candidate, Reason = "no-clear-space-below-composer; editor=" + match.box + "; document=" + match.doc + "; work=" + screen + "; obstacles=" + string.Join("|", obstacles.Where(r => r.Bottom > match.box.Bottom && r.Left < match.box.Left + 380 && r.Right > match.box.Left).Select(r => r.ToString())) };
    }

    internal static Rectangle FindSpace(Rectangle editor, Rectangle document, Rectangle workArea, Rectangle[] obstacles)
    {
        const int height = 22;
        var visible = Rectangle.Intersect(document, workArea);
        int width = Math.Min(380, Math.Min(editor.Width, visible.Right - editor.Left));
        if (width < 240) return Rectangle.Empty;
        var area = new Rectangle(editor.Left, editor.Bottom + 5, width, height);
        for (; area.Bottom <= visible.Bottom - 2; area.Y++)
        {
            if (visible.Contains(area) && !obstacles.Any(r => Rectangle.Inflate(r, 2, 2).IntersectsWith(area))) return area;
        }
        return Rectangle.Empty;
    }

    internal static Rectangle FindBelowWindow(Rectangle editor, Rectangle window, Rectangle workArea)
    {
        int left = Math.Max(editor.Left, workArea.Left);
        int width = Math.Min(380, Math.Min(editor.Right, workArea.Right) - left);
        var area = new Rectangle(left, window.Bottom + 4, width, 22);
        return width >= 240 && workArea.Contains(area) ? area : Rectangle.Empty;
    }

    private sealed class Overlay : Form
    {
        private readonly Button pill;
        private readonly Timer timer;
        private Form details;

        internal Overlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            pill = new Button {
                Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(35, 38, 42),
                ForeColor = Color.WhiteSmoke, Font = new Font("Times New Roman", 10),
                Text = "Token 用量    本轮 --   ·   累计 --   ·   缓存 --"
            };
            pill.FlatAppearance.BorderSize = 0;
            pill.Click += ToggleDetails;
            pill.MouseUp += (sender, e) => { if (e.Button == MouseButtons.Right) Close(); };
            Controls.Add(pill);
            timer = new Timer { Interval = 400 };
            timer.Tick += Tick;
            timer.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return cp;
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            // Application.Run initially shows the form. The timer shows it after a valid anchor.
            base.SetVisibleCore(value && !Bounds.IsEmpty && Tag != null);
        }

        private async void Tick(object sender, EventArgs args)
        {
            timer.Stop();
            try
            {
                var placement = await Task.Run(() => Locate(true));
                if (placement.Area.IsEmpty || GetForegroundWindow() != placement.Window)
                {
                    if (details != null) details.Close();
                    Hide();
                    return;
                }
                if (Bounds != placement.Area && details != null)
                    PlaceDetails(placement.Area);
                Bounds = placement.Area;
                Tag = placement.Window;
                if (!Visible) Show();
            }
            finally { if (!IsDisposed) timer.Start(); }
        }

        private void PlaceDetails(Rectangle anchor)
        {
            var work = Screen.FromPoint(anchor.Location).WorkingArea;
            int height = Math.Min(122, anchor.Top - work.Top - 6);
            if (height < 55) { details.Close(); return; }
            details.Bounds = new Rectangle(anchor.Left, anchor.Top - height - 5, anchor.Width, height);
        }

        private void ToggleDetails(object sender, EventArgs args)
        {
            if (details != null) { details.Close(); return; }
            details = new DetailWindow();
            details.FormClosed += delegate { details = null; };
            PlaceDetails(Bounds);
            if (details != null) details.Show(this);
        }
    }

    private sealed class DetailWindow : Form
    {
        internal DetailWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(35, 38, 42);
            Controls.Add(new Label {
                Dock = DockStyle.Fill, ForeColor = Color.WhiteSmoke, Padding = new Padding(12),
                Font = new Font("Microsoft YaHei UI", 9),
                Text = "Token 用量\r\n\r\n最近一轮 --    会话累计 --    缓存率 --\r\n会话身份待确认。"
            });
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000080; return cp; }
        }
    }
}
