using System.Drawing.Drawing2D;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Automation;

ApplicationConfiguration.Initialize();
Application.Run(new BadgeManager());

// ── BadgeManager ─────────────────────────────────────────────────────────────
// Hidden controller form — owns the tray icon and refresh timer.
// Creates/destroys/repositions a pool of tiny BadgeWindow instances.

internal sealed class BadgeManager : Form
{
    private readonly OverlaySettings settings;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly NotifyIcon trayIcon;
    private readonly List<BadgeWindow> pool = [];
    private readonly List<LabelInfo> lastLabels = [];
    private int unchangedPolls;
    private int consecutiveEmptyScans;
    private bool isFullscreenSuppressed;

    public BadgeManager()
    {
        settings = OverlaySettings.LoadOrCreateDefault();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Opacity = 0;
        Size = new Size(1, 1);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);

        refreshTimer = new System.Windows.Forms.Timer { Interval = settings.RefreshIntervalMs };
        refreshTimer.Tick += (_, _) => Refresh();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Taskbar Number Overlay").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { trayIcon!.Visible = false; Application.Exit(); });

        trayIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "Taskbar Number Overlay",
            ContextMenuStrip = menu,
            Visible = true
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Refresh();
        refreshTimer.Start();
    }

    private new void Refresh()
    {
        // Hide badges while a fullscreen window is active.
        var fullscreen = IsFullscreenWindowActive();
        if (fullscreen != isFullscreenSuppressed)
        {
            isFullscreenSuppressed = fullscreen;
            if (fullscreen)
            {
                HideAllBadges();
                return;
            }
        }
        if (fullscreen) return;

        var includeExpensive = lastLabels.Count == 0 || unchangedPolls % 8 == 0;

        var next = TaskbarLocator.GetNumberedButtonLocations(includeExpensive)
            .Select((rect, i) => new LabelInfo(rect, LabelForIndex(i)))
            .ToList();

        var hadLabels = lastLabels.Count > 0;
        var nextIsEmpty = next.Count == 0;

        if (nextIsEmpty && hadLabels)
            consecutiveEmptyScans++;
        else
            consecutiveEmptyScans = 0;

        var keepLast = nextIsEmpty && hadLabels && consecutiveEmptyScans < settings.EmptyScanHoldCount;
        var effective = keepLast ? lastLabels.ToList() : next;
        var changed = !lastLabels.SequenceEqual(effective);

        if (changed)
        {
            lastLabels.Clear();
            lastLabels.AddRange(effective);
            unchangedPolls = 0;
            SyncBadgeWindows(effective);
        }
        else
        {
            unchangedPolls++;
            // Re-assert topmost every tick even when labels are unchanged.
            // The OS can hide badge windows (e.g. on taskbar click) at the Win32 level
            // while WinForms' internal Visible flag stays true, making Visible=true a no-op.
            // SetWindowPos(SWP_SHOWWINDOW) bypasses WinForms state and forces them back.
            for (var i = 0; i < lastLabels.Count && i < pool.Count; i++)
                pool[i].AssertTopmost();
        }

        refreshTimer.Interval = keepLast
            ? settings.TransientRetryIntervalMs
            : changed
                ? Math.Max(250, settings.RefreshIntervalMs)
                : unchangedPolls switch
                {
                    > 20 => 2500,
                    > 6  => 1200,
                    _    => Math.Max(500, settings.RefreshIntervalMs)
                };
    }

    private void SyncBadgeWindows(IReadOnlyList<LabelInfo> labels)
    {
        // Grow pool if needed.
        while (pool.Count < labels.Count)
        {
            var w = new BadgeWindow(settings);
            pool.Add(w);
            w.Show();
        }

        // Update visible badges.
        for (var i = 0; i < labels.Count; i++)
        {
            pool[i].Update(labels[i]);
            pool[i].Visible = true;
            pool[i].AssertTopmost();
        }

        // Hide excess.
        for (var i = labels.Count; i < pool.Count; i++)
        {
            pool[i].AllowHide = true;
            pool[i].Visible = false;
            pool[i].AllowHide = false;
        }
    }

    private void HideAllBadges()
    {
        foreach (var w in pool)
        {
            w.AllowHide = true;
            w.Visible = false;
            w.AllowHide = false;
        }
    }

    private static bool IsFullscreenWindowActive()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        if (!GetWindowRect(hwnd, out var wr)) return false;
        var winRect = new Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);

        foreach (var screen in Screen.AllScreens)
        {
            if (!winRect.Contains(screen.Bounds)) continue;

            // Exclude the desktop and taskbar shell windows.
            var cls = new System.Text.StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            var name = cls.ToString();
            if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                continue;

            return true;
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private Icon CreateTrayIcon()
    {
        var badgeColor = OverlaySettings.ParseColor(settings.BadgeColorRgba, Color.FromArgb(255, 20, 20, 20));
        var opaqueColor = Color.FromArgb(255, badgeColor.R, badgeColor.G, badgeColor.B);

        using var bmp = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(opaqueColor);
        using var fg = new SolidBrush(Color.White);
        using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold, GraphicsUnit.Point);
        var sz = g.MeasureString("12", font);
        g.DrawString("12", font, fg, (16 - sz.Width) / 2f, (16 - sz.Height) / 2f - 0.5f);
        using var opaque = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var og = Graphics.FromImage(opaque);
        og.DrawImage(bmp, 0, 0);
        return Icon.FromHandle(opaque.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refreshTimer.Dispose();
            trayIcon.Dispose();
            foreach (var w in pool) w.Dispose();
        }
        base.Dispose(disposing);
    }

    private static string LabelForIndex(int i) => i switch { <= 8 => (i + 1).ToString(), 9 => "0", _ => (i + 1).ToString() };
}

internal sealed record LabelInfo(Rectangle ButtonBounds, string Text);

// ── BadgeWindow ───────────────────────────────────────────────────────────────
// One tiny borderless topmost window per badge, sized exactly to the badge.
// No transparent background hack needed — the window IS the badge.

internal sealed class BadgeWindow : Form
{
    private readonly OverlaySettings settings;
    private readonly Font font;
    private readonly SolidBrush badgeBrush;
    private readonly SolidBrush textBrush;
    private string labelText = "";

    // Set to true before calling Visible=false so the WndProc intercept
    // doesn't block our own intentional hides (e.g. for excess pool windows).
    internal bool AllowHide;

    public BadgeWindow(OverlaySettings settings)
    {
        this.settings = settings;
        font = new Font("Segoe UI", settings.FontSize, FontStyle.Bold, GraphicsUnit.Point);
        badgeBrush = new SolidBrush(OverlaySettings.ParseColor(settings.BadgeColorRgba, Color.FromArgb(210, 20, 20, 20)));
        textBrush = new SolidBrush(OverlaySettings.ParseColor(settings.TextColorRgba, Color.White));

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Size = new Size(settings.BadgeWidth, settings.BadgeHeight);
        BackColor = Color.Black;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE  = 0x08000000;
            const int WS_EX_TRANSPARENT = 0x00000020;
            const int WS_EX_LAYERED     = 0x00080000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_LAYERED;
            return cp;
        }
    }

    public void Update(LabelInfo label)
    {
        labelText = label.Text;
        var buttonRect = label.ButtonBounds;

        var x = buttonRect.Left + Math.Max((buttonRect.Width - settings.BadgeWidth) / 2, 0);
        var y = buttonRect.Top + settings.VerticalOffsetPx;

        SetBounds(x, y, settings.BadgeWidth, settings.BadgeHeight);
        Invalidate();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        DisableDwmShadow();
        SetClickThrough();
        AssertTopmost();
    }

    public void AssertTopmost()
    {
        // Re-assert topmost position — call on every refresh to survive taskbar focus changes.
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_SHOWWINDOW = 0x0040;
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_SYSCOMMAND          = 0x0112;
        const int SC_MINIMIZE            = 0xF020;
        const int WM_SIZE                = 0x0005;
        const int SIZE_MINIMIZED         = 1;
        const int WM_WINDOWPOSCHANGING   = 0x0046;
        const uint SWP_HIDEWINDOW        = 0x0080;

        // Block minimize commands entirely.
        if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt32() & 0xFFF0) == SC_MINIMIZE)
            return;

        // If somehow minimized, restore immediately.
        if (m.Msg == WM_SIZE && m.WParam.ToInt32() == SIZE_MINIMIZED)
        {
            base.WndProc(ref m);
            AssertTopmost();
            return;
        }

        // Strip SWP_HIDEWINDOW from external callers (e.g. taskbar clicking Show Desktop).
        // AllowHide is set by BadgeManager when it intentionally hides excess pool windows.
        if (m.Msg == WM_WINDOWPOSCHANGING && !AllowHide)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
            if ((pos.flags & SWP_HIDEWINDOW) != 0)
            {
                pos.flags &= ~SWP_HIDEWINDOW;
                Marshal.StructureToPtr(pos, m.LParam, false);
            }
        }

        base.WndProc(ref m);
    }

    private void DisableDwmShadow()
    {
        const int DWMWA_NCRENDERING_POLICY = 2;
        const int DWMNCRP_DISABLED = 1;
        var policy = DWMNCRP_DISABLED;
        DwmSetWindowAttribute(Handle, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
    }

    private void SetClickThrough()
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_LAYERED = 0x00080000;
        var ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_LAYERED));

        // Full opacity — no transparency colour key needed.
        SetLayeredWindowAttributes(Handle, 0, 255, 0x2 /* LWA_ALPHA */);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width, Height);
        using var path = RoundedRect(rect, settings.CornerRadius);
        g.FillPath(badgeBrush, path);

        var sz = g.MeasureString(labelText, font);
        g.DrawString(labelText, font, textBrush,
            (Width  - sz.Width)  / 2f,
            (Height - sz.Height) / 2f - 1f);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { font.Dispose(); badgeBrush.Dispose(); textBrush.Dispose(); }
        base.Dispose(disposing);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left,       r.Top,        d, d, 180, 90);
        p.AddArc(r.Right - d,  r.Top,        d, d, 270, 90);
        p.AddArc(r.Right - d,  r.Bottom - d, d, d, 0,   90);
        p.AddArc(r.Left,       r.Bottom - d, d, d, 90,  90);
        p.CloseFigure();
        return p;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }
}

// ── OverlaySettings ───────────────────────────────────────────────────────────

internal sealed class OverlaySettings
{
    public int RefreshIntervalMs { get; init; } = 700;
    public int BadgeWidth { get; init; } = 15;
    public int BadgeHeight { get; init; } = 11;
    public int VerticalOffsetPx { get; init; } = 3;
    public int CornerRadius { get; init; } = 3;
    public float FontSize { get; init; } = 8.5f;
    public int EmptyScanHoldCount { get; init; } = 8;
    public int TransientRetryIntervalMs { get; init; } = 250;
    public string BadgeColorRgba { get; init; } = "100,60,180,210";
    public string TextColorRgba { get; init; } = "255,255,255,255";

    public static Color ParseColor(string rgba, Color fallback)
    {
        var parts = rgba?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts?.Length != 4) return fallback;
        if (!byte.TryParse(parts[0], out var r) || !byte.TryParse(parts[1], out var g) ||
            !byte.TryParse(parts[2], out var b) || !byte.TryParse(parts[3], out var a))
            return fallback;
        return Color.FromArgb(a, r, g, b);
    }

    public static OverlaySettings LoadOrCreateDefault()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "settings.json");
        if (!File.Exists(path)) { var d = new OverlaySettings(); Write(path, d); return d; }
        try
        {
            var loaded = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(path));
            if (loaded is null) { var d = new OverlaySettings(); Write(path, d); return d; }
            return Sanitize(loaded);
        }
        catch { var d = new OverlaySettings(); Write(path, d); return d; }
    }

    private static void Write(string path, OverlaySettings s) =>
        File.WriteAllText(path, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));

    private static OverlaySettings Sanitize(OverlaySettings s) => new()
    {
        RefreshIntervalMs      = Math.Clamp(s.RefreshIntervalMs, 100, 5000),
        BadgeWidth             = Math.Clamp(s.BadgeWidth, 14, 100),
        BadgeHeight            = Math.Clamp(s.BadgeHeight, 10, 60),
        VerticalOffsetPx       = Math.Clamp(s.VerticalOffsetPx, -20, 30),
        CornerRadius           = Math.Clamp(s.CornerRadius, 0, 20),
        FontSize               = Math.Clamp(s.FontSize, 6, 24),
        EmptyScanHoldCount     = Math.Clamp(s.EmptyScanHoldCount, 1, 60),
        TransientRetryIntervalMs = Math.Clamp(s.TransientRetryIntervalMs, 100, 2000),
        BadgeColorRgba         = s.BadgeColorRgba,
        TextColorRgba          = s.TextColorRgba
    };
}

// ── TaskbarLocator ────────────────────────────────────────────────────────────

internal static class TaskbarLocator
{
    public static IReadOnlyList<Rectangle> GetNumberedButtonLocations(bool includeExpensiveCandidates)
    {
        var all = new List<Rectangle>();
        foreach (var handle in EnumerateTrayWindows())
            all.AddRange(GetButtonsForTray(handle, includeExpensiveCandidates));

        if (all.Count == 0) return [];

        all = all.Distinct().ToList();
        var horizontal = (all.Max(r => r.Right) - all.Min(r => r.Left)) >=
                         (all.Max(r => r.Bottom) - all.Min(r => r.Top));

        return (horizontal
            ? all.OrderBy(r => r.Left).ThenBy(r => r.Top)
            : all.OrderBy(r => r.Top).ThenBy(r => r.Left))
            .Take(10).ToList();
    }

    private static IEnumerable<Rectangle> GetButtonsForTray(IntPtr trayHandle, bool includeExpensive)
    {
        var results = new List<Rectangle>();
        try
        {
            var root = AutomationElement.FromHandle(trayHandle);
            if (root is null || !GetWindowRect(trayHandle, out var bounds)) return results;

            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                TryAdd(e, bounds, results);

            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)))
                TryAdd(e, bounds, results);

            if (includeExpensive && results.Count == 0)
                foreach (AutomationElement e in root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))
                    TryAdd(e, bounds, results);
        }
        catch { }
        return results;
    }

    private static void TryAdd(AutomationElement el, Rectangle taskbar, List<Rectangle> results)
    {
        var c = el.Current;
        var r = c.BoundingRectangle;
        if (c.IsOffscreen || r.Width <= 0 || r.Height <= 0) return;
        var rect = Rectangle.Round(new RectangleF((float)r.Left, (float)r.Top, (float)r.Width, (float)r.Height));
        if (IsTaskbarAppButton(rect, taskbar, c.Name, c.AutomationId, c.ClassName))
            results.Add(rect);
    }

    private static bool IsTaskbarAppButton(Rectangle c, Rectangle bar, string name, string aid, string cls)
    {
        if (c.Width < 20 || c.Height < 20) return false;
        if (!Rectangle.Inflate(bar, 2, 2).IntersectsWith(c)) return false;
        var horiz = bar.Width >= bar.Height;
        if (horiz  && c.Height < Math.Max((int)(bar.Height * 0.55), 24)) return false;
        if (!horiz && c.Width  < Math.Max((int)(bar.Width  * 0.55), 24)) return false;
        var marker = $"{name} {aid} {cls}".ToLowerInvariant();
        return !new[] { "start","search","widgets","task view","copilot","chat",
            "system tray","notification","quick settings","show desktop",
            "hidden icon","overflow","chevron" }.Any(marker.Contains);
    }

    private static IEnumerable<IntPtr> EnumerateTrayWindows()
    {
        var main = FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero && IsWindowVisible(main)) yield return main;
        var cur = IntPtr.Zero;
        while (true)
        {
            cur = FindWindowEx(IntPtr.Zero, cur, "Shell_SecondaryTrayWnd", null);
            if (cur == IntPtr.Zero) yield break;
            if (IsWindowVisible(cur)) yield return cur;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? c, string? w);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string? c, string? w);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public static implicit operator Rectangle(RECT r) => Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }
}
