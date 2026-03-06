using System.Drawing.Drawing2D;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Automation;

ApplicationConfiguration.Initialize();
Application.Run(new OverlayForm());

internal sealed class OverlayForm : Form
{
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly List<LabelInfo> labels = [];
    private readonly OverlaySettings settings;
    private readonly Font labelFont;
    private readonly SolidBrush badgeBrush;
    private readonly SolidBrush textBrush;
    private readonly NotifyIcon trayIcon;
    private int unchangedPolls;
    private int consecutiveEmptyScans;

    public OverlayForm()
    {
        settings = OverlaySettings.LoadOrCreateDefault();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        DoubleBuffered = true;

        var virtualScreen = SystemInformation.VirtualScreen;
        Bounds = virtualScreen;

        refreshTimer = new System.Windows.Forms.Timer { Interval = settings.RefreshIntervalMs };
        refreshTimer.Tick += (_, _) => RefreshLabels();
        labelFont = new Font("Segoe UI", settings.FontSize, FontStyle.Bold, GraphicsUnit.Point);
        badgeBrush = new SolidBrush(ParseColorOrDefault(settings.BadgeColorRgba, Color.FromArgb(210, 20, 20, 20)));
        textBrush = new SolidBrush(ParseColorOrDefault(settings.TextColorRgba, Color.White));

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

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;

            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyClickThroughStyles();
        RefreshLabels();
        refreshTimer.Start();
    }

    private void RefreshLabels()
    {
        var includeExpensiveScan = labels.Count == 0 || unchangedPolls % 8 == 0;
        var virtualScreen = SystemInformation.VirtualScreen;
        var boundsChanged = false;
        if (Bounds != virtualScreen)
        {
            Bounds = virtualScreen;
            boundsChanged = true;
        }

        var next = TaskbarLocator.GetNumberedButtonLocations(includeExpensiveScan)
            .Select((rect, index) => new LabelInfo(rect, LabelForIndex(index)))
            .ToList();

        var hadLabels = labels.Count > 0;
        var nextIsEmpty = next.Count == 0;

        if (nextIsEmpty && hadLabels)
        {
            consecutiveEmptyScans++;
        }
        else
        {
            consecutiveEmptyScans = 0;
        }

        // UIA can momentarily return no taskbar apps during focus/task switches.
        // Keep last known labels until we see several empty scans in a row.
        var shouldKeepLastKnown = nextIsEmpty && hadLabels && consecutiveEmptyScans < settings.EmptyScanHoldCount;
        var effective = shouldKeepLastKnown ? labels.ToList() : next;

        var labelsChanged = !labels.SequenceEqual(effective);
        if (labelsChanged)
        {
            labels.Clear();
            labels.AddRange(effective);
            unchangedPolls = 0;
        }
        else
        {
            unchangedPolls++;
        }

        if (shouldKeepLastKnown)
        {
            // Recover quickly after transient misses.
            refreshTimer.Interval = settings.TransientRetryIntervalMs;
        }
        else
        {
        refreshTimer.Interval = labelsChanged
            ? Math.Max(250, settings.RefreshIntervalMs)
            : unchangedPolls switch
            {
                > 20 => 2500,
                > 6 => 1200,
                _ => Math.Max(500, settings.RefreshIntervalMs)
            };
        }

        if (labelsChanged || boundsChanged)
        {
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        foreach (var label in labels)
        {
            var badgeRect = new Rectangle(
                label.ButtonBounds.Left - Left + Math.Max((label.ButtonBounds.Width - settings.BadgeWidth) / 2, 0),
                label.ButtonBounds.Top - Top + settings.VerticalOffsetPx,
                settings.BadgeWidth,
                settings.BadgeHeight);

            using var path = RoundedRect(badgeRect, settings.CornerRadius);

            e.Graphics.FillPath(badgeBrush, path);
            var textSize = e.Graphics.MeasureString(label.Text, labelFont);
            var textX = badgeRect.Left + (badgeRect.Width - textSize.Width) / 2f;
            var textY = badgeRect.Top + (badgeRect.Height - textSize.Height) / 2f - 1;
            e.Graphics.DrawString(label.Text, labelFont, textBrush, textX, textY);
        }

    }

    private Icon CreateTrayIcon()
    {
        var badgeColor = ParseColorOrDefault(settings.BadgeColorRgba, Color.FromArgb(255, 20, 20, 20));
        var opaqueColor = Color.FromArgb(255, badgeColor.R, badgeColor.G, badgeColor.B);

        using var bmp = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(opaqueColor);

        using var fg = new SolidBrush(Color.White);
        using var font = new Font("Segoe UI", 7.5f, FontStyle.Bold, GraphicsUnit.Point);
        var sz = g.MeasureString("12", font);
        g.DrawString("12", font, fg, (16 - sz.Width) / 2f, (16 - sz.Height) / 2f - 0.5f);

        // GetHicon can mis-handle alpha; copy to a 24bpp bitmap first to force opaque conversion.
        using var opaqueBmp = new Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var og = Graphics.FromImage(opaqueBmp);
        og.DrawImage(bmp, 0, 0);
        return Icon.FromHandle(opaqueBmp.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refreshTimer.Dispose();
            labelFont.Dispose();
            badgeBrush.Dispose();
            textBrush.Dispose();
            trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Color ParseColorOrDefault(string rgba, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(rgba))
        {
            return fallback;
        }

        var parts = rgba.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return fallback;
        }

        if (!byte.TryParse(parts[0], out var r) ||
            !byte.TryParse(parts[1], out var g) ||
            !byte.TryParse(parts[2], out var b) ||
            !byte.TryParse(parts[3], out var a))
        {
            return fallback;
        }

        return Color.FromArgb(a, r, g, b);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();

        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }

    private void ApplyClickThroughStyles()
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_LAYERED = 0x00080000;

        var exStyle = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        var updated = new IntPtr(exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);
        SetWindowLongPtr(Handle, GWL_EXSTYLE, updated);
    }

    private static string LabelForIndex(int index) => index switch
    {
        <= 8 => (index + 1).ToString(),
        9 => "0",
        _ => (index + 1).ToString()
    };

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private sealed record LabelInfo(Rectangle ButtonBounds, string Text);
}

internal sealed class OverlaySettings
{
    public int RefreshIntervalMs { get; init; } = 700;
    public int BadgeWidth { get; init; } = 15;
    public int BadgeHeight { get; init; } = 11;
    public int VerticalOffsetPx { get; init; } = 1;
    public int CornerRadius { get; init; } = 3;
    public float FontSize { get; init; } = 8.5f;
    public int EmptyScanHoldCount { get; init; } = 8;
    public int TransientRetryIntervalMs { get; init; } = 250;
    public string BadgeColorRgba { get; init; } = "100,60,180,210";
    public string TextColorRgba { get; init; } = "255,255,255,255";
    public bool ShowDiagnosticWhenNoButtons { get; init; } = true;
    public string DiagnosticBadgeColorRgba { get; init; } = "180,30,30,170";
    public string DiagnosticTextColorRgba { get; init; } = "255,255,255,255";

    public static OverlaySettings LoadOrCreateDefault()
    {
        var path = GetSettingsPath();
        if (!File.Exists(path))
        {
            var defaults = new OverlaySettings();
            Write(path, defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<OverlaySettings>(json);
            if (loaded is null)
            {
                var defaults = new OverlaySettings();
                Write(path, defaults);
                return defaults;
            }

            return Sanitize(loaded);
        }
        catch
        {
            var defaults = new OverlaySettings();
            Write(path, defaults);
            return defaults;
        }
    }

    private static string GetSettingsPath() =>
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    private static void Write(string path, OverlaySettings settings)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(settings, options);
        File.WriteAllText(path, json);
    }

    private static OverlaySettings Sanitize(OverlaySettings loaded) =>
        new()
        {
            RefreshIntervalMs = Math.Clamp(loaded.RefreshIntervalMs, 100, 5000),
            BadgeWidth = Math.Clamp(loaded.BadgeWidth, 14, 100),
            BadgeHeight = Math.Clamp(loaded.BadgeHeight, 10, 60),
            VerticalOffsetPx = Math.Clamp(loaded.VerticalOffsetPx, -20, 30),
            CornerRadius = Math.Clamp(loaded.CornerRadius, 0, 20),
            FontSize = Math.Clamp(loaded.FontSize, 6, 24),
            EmptyScanHoldCount = Math.Clamp(loaded.EmptyScanHoldCount, 1, 60),
            TransientRetryIntervalMs = Math.Clamp(loaded.TransientRetryIntervalMs, 100, 2000),
            BadgeColorRgba = loaded.BadgeColorRgba,
            TextColorRgba = loaded.TextColorRgba,
            ShowDiagnosticWhenNoButtons = loaded.ShowDiagnosticWhenNoButtons,
            DiagnosticBadgeColorRgba = loaded.DiagnosticBadgeColorRgba,
            DiagnosticTextColorRgba = loaded.DiagnosticTextColorRgba
        };
}

internal static class TaskbarLocator
{
    public static IReadOnlyList<Rectangle> GetNumberedButtonLocations(bool includeExpensiveCandidates)
    {
        var allButtons = new List<Rectangle>();
        foreach (var trayHandle in EnumerateTrayWindows())
        {
            allButtons.AddRange(GetButtonsForTray(trayHandle, includeExpensiveCandidates));
        }

        if (allButtons.Count == 0)
        {
            return [];
        }

        allButtons = allButtons
            .Distinct()
            .ToList();

        var spanX = allButtons.Max(x => x.Right) - allButtons.Min(x => x.Left);
        var spanY = allButtons.Max(x => x.Bottom) - allButtons.Min(x => x.Top);
        var horizontal = spanX >= spanY;

        var ordered = horizontal
            ? allButtons.OrderBy(r => r.Left).ThenBy(r => r.Top).ToList()
            : allButtons.OrderBy(r => r.Top).ThenBy(r => r.Left).ToList();

        return ordered.Take(10).ToList();
    }

    public static Rectangle? GetPrimaryTaskbarBounds()
    {
        var main = FindWindow("Shell_TrayWnd", null);
        if (main == IntPtr.Zero || !GetWindowRect(main, out var rect))
        {
            return null;
        }

        return rect;
    }

    private static IEnumerable<Rectangle> GetButtonsForTray(IntPtr trayHandle, bool includeExpensiveCandidates)
    {
        var results = new List<Rectangle>();
        try
        {
            var root = AutomationElement.FromHandle(trayHandle);
            if (root is null)
            {
                return results;
            }

            if (!GetWindowRect(trayHandle, out var taskbarBounds))
            {
                return results;
            }

            var buttons = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            var tabs = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            foreach (AutomationElement button in buttons)
            {
                TryAddCandidate(button, taskbarBounds, results);
            }

            foreach (AutomationElement tab in tabs)
            {
                TryAddCandidate(tab, taskbarBounds, results);
            }

            if (includeExpensiveCandidates && results.Count == 0)
            {
                var listItems = root.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));

                foreach (AutomationElement item in listItems)
                {
                    TryAddCandidate(item, taskbarBounds, results);
                }
            }
        }
        catch
        {
            // Explorer can reload taskbar during polling; skip this cycle.
        }

        return results;
    }

    private static void TryAddCandidate(AutomationElement element, Rectangle taskbarBounds, List<Rectangle> results)
    {
        var current = element.Current;
        var rect = current.BoundingRectangle;
        if (current.IsOffscreen || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var rounded = Rectangle.Round(new RectangleF(
            (float)rect.Left,
            (float)rect.Top,
            (float)rect.Width,
            (float)rect.Height));

        if (!LooksLikeTaskbarAppButton(rounded, taskbarBounds, current.Name, current.AutomationId, current.ClassName))
        {
            return;
        }

        results.Add(rounded);
    }

    private static bool LooksLikeTaskbarAppButton(
        Rectangle candidate,
        Rectangle taskbarBounds,
        string name,
        string automationId,
        string className)
    {
        if (candidate.Width < 20 || candidate.Height < 20)
        {
            return false;
        }

        // Keep only buttons physically inside the taskbar bounds.
        var inflatedTaskbar = Rectangle.Inflate(taskbarBounds, 2, 2);
        if (!inflatedTaskbar.IntersectsWith(candidate))
        {
            return false;
        }

        var taskbarIsHorizontal = taskbarBounds.Width >= taskbarBounds.Height;
        if (taskbarIsHorizontal)
        {
            var minHeight = Math.Max((int)(taskbarBounds.Height * 0.55), 24);
            if (candidate.Height < minHeight)
            {
                return false;
            }
        }
        else
        {
            var minWidth = Math.Max((int)(taskbarBounds.Width * 0.55), 24);
            if (candidate.Width < minWidth)
            {
                return false;
            }
        }

        var marker = $"{name} {automationId} {className}".ToLowerInvariant();
        var excluded = new[]
        {
            "start",
            "search",
            "widgets",
            "task view",
            "copilot",
            "chat",
            "system tray",
            "notification",
            "quick settings",
            "show desktop",
            "hidden icon",
            "overflow",
            "chevron"
        };

        if (excluded.Any(marker.Contains))
        {
            return false;
        }

        return true;
    }

    private static IEnumerable<IntPtr> EnumerateTrayWindows()
    {
        var main = FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero && IsWindowVisible(main))
        {
            yield return main;
        }

        var current = IntPtr.Zero;
        while (true)
        {
            current = FindWindowEx(IntPtr.Zero, current, "Shell_SecondaryTrayWnd", null);
            if (current == IntPtr.Zero)
            {
                yield break;
            }

            if (IsWindowVisible(current))
            {
                yield return current;
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public static implicit operator Rectangle(RECT rect) =>
            Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }
}
