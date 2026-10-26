using System.Text;

namespace DamnWindows11;

internal sealed record TrackedWindow(
    IntPtr Hwnd, uint Pid, string ClassName, string Title,
    Native.WINDOWPLACEMENT Placement, Native.RECT Rect, Native.RECT Monitor);

internal readonly record struct RestoreResult(int Moved, int AlreadyOk, int Gone, int Failed);

internal static class WindowTracker
{
    private static readonly HashSet<string> ShellClasses =
        ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    /// <summary>Top-level app windows, top of the Z-order first.</summary>
    public static List<TrackedWindow> Capture()
    {
        var result = new List<TrackedWindow>();
        Native.EnumWindows((h, _) =>
        {
            if (Inspect(h) is { } w) result.Add(w);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static TrackedWindow? Inspect(IntPtr h)
    {
        if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return null;
        if ((Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOOLWINDOW) != 0) return null;
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0 && cloaked != Native.DWM_CLOAKED_SHELL) return null;

        Native.GetWindowThreadProcessId(h, out uint pid);
        if (pid == OwnPid) return null;

        string cls = ClassName(h);
        if (ShellClasses.Contains(cls)) return null;
        string title = Title(h);
        if (title.Length == 0) return null;

        var wp = Native.NewPlacement();
        if (!Native.GetWindowPlacement(h, ref wp) || !Native.GetWindowRect(h, out var rect)) return null;
        if (Native.MonitorBoundsOf(h) is not { } monitor) return null;

        return new TrackedWindow(h, pid, cls, title, wp, rect, monitor);
    }

    /// <summary>Puts saved windows back. Bottom of the Z-order first, so the top window is handled last.</summary>
    public static RestoreResult Restore(IReadOnlyList<TrackedWindow> saved)
    {
        int moved = 0, ok = 0, gone = 0, failed = 0;
        var foreground = Native.GetForegroundWindow();
        bool activatedSomething = false;

        for (int i = saved.Count - 1; i >= 0; i--)
        {
            var s = saved[i];
            if (!IsSameWindow(s)) { gone++; continue; }
            if (Native.IsHungAppWindow(s.Hwnd)) { failed++; Log.Write($"  hung, skipped: {Describe(s)}"); continue; }

            var cur = Native.NewPlacement();
            if (!Native.GetWindowPlacement(s.Hwnd, ref cur) || !Native.GetWindowRect(s.Hwnd, out var curRect))
            {
                failed++;
                continue;
            }

            bool inPlace = s.Placement.showCmd switch
            {
                Native.SW_SHOWMAXIMIZED => cur.showCmd == Native.SW_SHOWMAXIMIZED
                                           && Native.MonitorBoundsOf(s.Hwnd) is { } m && m.Equals(s.Monitor),
                Native.SW_SHOWMINIMIZED => cur.showCmd == Native.SW_SHOWMINIMIZED
                                           && cur.rcNormalPosition.Equals(s.Placement.rcNormalPosition),
                _ => cur.showCmd == Native.SW_SHOWNORMAL && curRect.Equals(s.Rect),
            };
            if (inPlace) { ok++; continue; }

            bool success;
            switch (s.Placement.showCmd)
            {
                case Native.SW_SHOWMAXIMIZED:
                    // Un-maximize onto the target monitor first, then maximize there.
                    success = Place(s, Native.SW_SHOWNOACTIVATE) && Place(s, Native.SW_SHOWMAXIMIZED);
                    activatedSomething = true;
                    break;

                case Native.SW_SHOWMINIMIZED:
                    // Stays minimized, but un-minimizing will bring it back on the right screen.
                    success = Place(s, Native.SW_SHOWMINNOACTIVE);
                    break;

                default:
                    if (cur.showCmd != Native.SW_SHOWNORMAL) Place(s, Native.SW_SHOWNOACTIVATE);
                    // Twice: the first move crosses monitors and the app rescales for the new DPI,
                    // the second one sets the exact size again.
                    success = Move(s) && Move(s);
                    break;
            }

            if (success) { moved++; Log.Write($"  moved: {Describe(s)}"); }
            else { failed++; Log.Write($"  failed (elevated app?): {Describe(s)}"); }
        }

        if (activatedSomething && foreground != IntPtr.Zero) Native.SetForegroundWindow(foreground);
        return new RestoreResult(moved, ok, gone, failed);
    }

    private static bool Place(TrackedWindow s, int showCmd)
    {
        var wp = s.Placement;
        wp.showCmd = showCmd;
        wp.flags |= Native.WPF_ASYNCWINDOWPLACEMENT;
        return Native.SetWindowPlacement(s.Hwnd, ref wp);
    }

    private static bool Move(TrackedWindow s) =>
        Native.SetWindowPos(s.Hwnd, IntPtr.Zero, s.Rect.Left, s.Rect.Top, s.Rect.Width, s.Rect.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_ASYNCWINDOWPOS);

    private static bool IsSameWindow(TrackedWindow s)
    {
        if (!Native.IsWindow(s.Hwnd)) return false;
        Native.GetWindowThreadProcessId(s.Hwnd, out uint pid);
        return pid == s.Pid && ClassName(s.Hwnd) == s.ClassName;
    }

    private static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        return Native.GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string Title(IntPtr h)
    {
        int len = Native.GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        Native.GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string Describe(TrackedWindow s)
    {
        string title = s.Title.Length > 60 ? s.Title[..57] + "..." : s.Title;
        string state = s.Placement.showCmd switch
        {
            Native.SW_SHOWMAXIMIZED => "max",
            Native.SW_SHOWMINIMIZED => "min",
            _ => s.Rect.ToString(),
        };
        return $"\"{title}\" [{s.ClassName}] -> {state}";
    }
}
