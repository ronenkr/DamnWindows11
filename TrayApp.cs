using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace DamnWindows11;

/// <summary>
/// Keeps one window snapshot per monitor topology. Snapshots are only taken while the topology has been
/// stable for a while; when a topology comes back (e.g. KVM switched back to the Dell) its snapshot is restored.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SnapshotHoldoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(3);
    // Repeat passes: apps re-layout after DPI changes and Windows' own restore may move things again.
    private static readonly TimeSpan[] RestorePassOffsets =
        [TimeSpan.Zero, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4)];

    private readonly Dictionary<string, List<TrackedWindow>> _snapshots = new();
    private readonly List<DateTime> _restorePasses = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly DisplayWatcher _watcher;
    private readonly Native.WinEventProc _moveSizeProc; // kept alive for the native hook
    private readonly IntPtr _moveSizeHook;

    private string _stableKey;
    private string _pendingKey;
    private DateTime _pendingSince = DateTime.MinValue;
    private DateTime _lastDisplayChange = DateTime.MinValue;
    private DateTime _lastSnapshot = DateTime.MinValue;
    private bool _restoreWanted;
    private int _restorePassNumber;
    private bool _paused;

    public TrayApp()
    {
        _stableKey = _pendingKey = Topology.CurrentKey();
        Log.Write($"Started. Topology: {_stableKey}");

        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        _autostartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart())
        {
            Checked = Autostart.IsEnabled,
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Restore now", null, (_, _) => ScheduleRestore("manual"));
        menu.Items.Add("Snapshot now", null, (_, _) => TakeSnapshot("manual"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { Icon = CreateIcon(), ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ScheduleRestore("manual");

        _watcher = new DisplayWatcher();
        _watcher.DisplayChanged += () => OnDisplayChange("WM_DISPLAYCHANGE");

        _moveSizeProc = (_, _, _, _, _, _, _) => OnUserMovedWindow();
        _moveSizeHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MOVESIZEEND, Native.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _moveSizeProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

        _timer = new System.Windows.Forms.Timer { Interval = (int)TickInterval.TotalMilliseconds };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        UpdateTooltip();
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        string key = Topology.CurrentKey();

        if (key != _pendingKey)
        {
            _pendingKey = key;
            OnDisplayChange("topology changed");
            return;
        }
        if (now - _pendingSince < SettleTime) return;

        if (key != _stableKey)
        {
            Log.Write($"Topology settled: {_stableKey}  ->  {key}");
            _stableKey = key;
            UpdateTooltip();
        }

        if (_restoreWanted)
        {
            _restoreWanted = false;
            ScheduleRestore("display change");
        }

        if (_restorePasses.Count > 0)
        {
            if (now >= _restorePasses[0])
            {
                _restorePasses.RemoveAt(0);
                RunRestorePass();
            }
            return; // no snapshots until all passes are done
        }

        if (!_paused && now - _lastDisplayChange >= SnapshotHoldoff && now - _lastSnapshot >= SnapshotInterval)
            TakeSnapshot(null);
    }

    private void OnDisplayChange(string reason)
    {
        var now = DateTime.UtcNow;
        if (now - _lastDisplayChange > SettleTime) Log.Write($"Display change detected ({reason}), freezing snapshots");
        _lastDisplayChange = now;
        _pendingSince = now;
        _restorePasses.Clear();
        _restoreWanted = true;
    }

    private void OnUserMovedWindow()
    {
        var now = DateTime.UtcNow;
        if (!_paused && _restorePasses.Count == 0 && now - _lastDisplayChange >= SnapshotHoldoff)
            TakeSnapshot(null);
    }

    private void TakeSnapshot(string? reason)
    {
        if (_pendingKey != _stableKey) return;
        var windows = WindowTracker.Capture();
        _lastSnapshot = DateTime.UtcNow;
        if (windows.Count == 0) return;

        bool isNew = !_snapshots.ContainsKey(_stableKey);
        _snapshots[_stableKey] = windows;
        if (isNew || reason != null)
            Log.Write($"Snapshot{(reason != null ? $" ({reason})" : "")}: {windows.Count} windows for {_stableKey}");
        UpdateTooltip();
    }

    private void ScheduleRestore(string reason)
    {
        if (_paused) return;
        if (!_snapshots.ContainsKey(_stableKey))
        {
            Log.Write($"Restore ({reason}): no snapshot for {_stableKey} yet");
            return;
        }
        Log.Write($"Restore ({reason}) for {_stableKey}");
        var now = DateTime.UtcNow;
        _restorePasses.Clear();
        _restorePasses.AddRange(RestorePassOffsets.Select(o => now + o));
        _restorePassNumber = 0;
        if (reason == "manual") Tick();
    }

    private void RunRestorePass()
    {
        if (!_snapshots.TryGetValue(_stableKey, out var saved)) return;
        _restorePassNumber++;
        var r = WindowTracker.Restore(saved);
        Log.Write($"Restore pass {_restorePassNumber}/{RestorePassOffsets.Length}: " +
                  $"moved {r.Moved}, already ok {r.AlreadyOk}, closed {r.Gone}, failed {r.Failed}");
        if (_restorePasses.Count == 0) _lastSnapshot = DateTime.UtcNow; // let things settle before the next snapshot
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _pauseItem.Checked = _paused;
        if (_paused) _restorePasses.Clear();
        Log.Write(_paused ? "Paused" : "Resumed");
        UpdateTooltip();
    }

    private void ToggleAutostart()
    {
        Autostart.Set(!Autostart.IsEnabled);
        _autostartItem.Checked = Autostart.IsEnabled;
        Log.Write($"Start with Windows: {_autostartItem.Checked}");
    }

    private static void OpenLog()
    {
        if (File.Exists(Log.FilePath)) Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
    }

    private void UpdateTooltip()
    {
        int monitors = _stableKey.Split(" | ").Length;
        int saved = _snapshots.TryGetValue(_stableKey, out var s) ? s.Count : 0;
        string text = $"DamnWindows11 - {monitors} monitor(s), {saved} windows saved{(_paused ? " (paused)" : "")}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    // Two little monitors, so it's recognisable in the tray.
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var frame = new Pen(Color.White, 2.5f);
            using var fillA = new SolidBrush(Color.FromArgb(0, 120, 212));
            using var fillB = new SolidBrush(Color.FromArgb(16, 185, 129));
            g.FillRectangle(fillA, 2, 6, 15, 12);
            g.DrawRectangle(frame, 2, 6, 15, 12);
            g.FillRectangle(fillB, 15, 12, 15, 12);
            g.DrawRectangle(frame, 15, 12, 15, 12);
            g.DrawLine(frame, 22, 24, 22, 28);
            g.DrawLine(frame, 18, 29, 26, 29);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            if (_moveSizeHook != IntPtr.Zero) Native.UnhookWinEvent(_moveSizeHook);
            _watcher.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            Log.Write("Exited");
        }
        base.Dispose(disposing);
    }

    /// <summary>Hidden top-level window; WM_DISPLAYCHANGE is only broadcast to top-level windows.</summary>
    private sealed class DisplayWatcher : NativeWindow, IDisposable
    {
        private const int WM_DISPLAYCHANGE = 0x007E;

        public event Action? DisplayChanged;

        public DisplayWatcher() => CreateHandle(new CreateParams { Caption = "DamnWindows11.DisplayWatcher" });

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DISPLAYCHANGE) DisplayChanged?.Invoke();
            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }
}
