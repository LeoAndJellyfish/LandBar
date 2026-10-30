using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LandBar.Presentation;

/// <summary>Repairs desktop Z-order after window events without moving, resizing or activating the island.</summary>
public sealed class TopmostWindowController : IDisposable
{
    private const uint ForegroundEvent = 0x0003, WindowShownEvent = 0x8002, WindowReorderedEvent = 0x8004, WindowMovedEvent = 0x800B;
    private const uint SkipOwnProcess = 0x0002;
    private const int MaximumBurstRepairs = 3;
    private static readonly TimeSpan QuietInterval = TimeSpan.FromSeconds(2);
    private readonly Window _window;
    private readonly Func<Rect> _visibleBounds;
    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private readonly WinEventProc _callback;
    private readonly List<IntPtr> _hooks = [];
    private readonly DispatcherTimer _pending;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly uint _processId = (uint)Environment.ProcessId;
    private IntPtr _foreground;
    private TimeSpan? _lastObstruction;
    private int _burstRepairs;
    private bool _restoring, _disposed;

    public TopmostWindowController(Window window, Func<Rect> visibleBounds)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(visibleBounds);
        window.Dispatcher.VerifyAccess();
        _window = window; _visibleBounds = visibleBounds;
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("The window handle is not ready.");
        _foreground = GetForegroundWindow();
        _pending = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _pending.Tick += CheckPending;
        _callback = WindowEvent;
        foreach (uint eventId in new[] { ForegroundEvent, WindowShownEvent, WindowReorderedEvent, WindowMovedEvent })
        {
            var hook = SetWinEventHook(eventId, eventId, IntPtr.Zero, _callback, 0, 0, SkipOwnProcess);
            if (hook != IntPtr.Zero) _hooks.Add(hook);
        }
        _source.AddHook(WindowMessage);
        _window.Closed += WindowClosed;
        ScheduleCheck();
    }

    public int ActiveHookCount => _hooks.Count;
    public int RepairCount { get; private set; }
    public int SuppressedRepairCount { get; private set; }

    /// <summary>Requests one coalesced check after the visible island has changed shape.</summary>
    public void RequestCheck()
    {
        _window.Dispatcher.VerifyAccess();
        ScheduleCheck();
    }

    private void WindowEvent(IntPtr hook, uint eventId, IntPtr hwnd, int objectId, int childId, uint threadId, uint time)
    {
        if (_disposed || hwnd == _handle) return;
        if (eventId == ForegroundEvent)
        {
            if (_foreground != hwnd)
            {
                _foreground = hwnd;
                _burstRepairs = 0;
                _lastObstruction = null;
            }
        }
        else
        {
            bool desktopOrder = eventId == WindowReorderedEvent && (hwnd == IntPtr.Zero || hwnd == GetDesktopWindow());
            if (!desktopOrder && (hwnd == IntPtr.Zero || objectId != 0 || childId != 0 || GetAncestor(hwnd, 2) != hwnd)) return;
        }
        ScheduleCheck();
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // No per-frame work: internal shell animation never changes the native HWND rectangle.
        if (!_disposed && !_restoring && message is 0x0006 or 0x0018 or 0x0047 or 0x007E)
            ScheduleCheck();
        return IntPtr.Zero;
    }

    private void ScheduleCheck()
    {
        if (!_disposed && !_pending.IsEnabled) _pending.Start();
    }

    private void CheckPending(object? sender, EventArgs args)
    {
        _pending.Stop();
        if (_disposed || !_window.IsVisible || IsIconic(_handle) || !NeedsRepair()) return;
        var now = _clock.Elapsed;
        // A persistent competing overlay receives a bounded burst, then no more Z-order writes.
        // A separate obstruction after a quiet interval can recover even in the same application.
        if (_lastObstruction is null || now - _lastObstruction.Value >= QuietInterval) _burstRepairs = 0;
        _lastObstruction = now;
        if (_burstRepairs >= MaximumBurstRepairs) { SuppressedRepairCount++; return; }
        _burstRepairs++;
        _restoring = true;
        try
        {
            if (SetWindowPos(_handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200))
                RepairCount++;
        }
        finally { _restoring = false; }
    }

    private bool NeedsRepair()
    {
        if ((GetWindowLongPtr(_handle, -20).ToInt64() & 0x0008) == 0) return true;
        Rect local = _visibleBounds();
        if (local.IsEmpty || local.Width <= 0 || local.Height <= 0) return false;
        Point topLeft = _window.PointToScreen(local.TopLeft), bottomRight = _window.PointToScreen(local.BottomRight);
        var visible = new Rect(topLeft, bottomRight);
        var visited = new HashSet<IntPtr>();
        for (var hwnd = GetWindow(_handle, 3); hwnd != IntPtr.Zero && visited.Add(hwnd); hwnd = GetWindow(hwnd, 3))
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == _processId || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd)) continue;
            if (GetWindowRect(hwnd, out var bounds) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top &&
                visible.IntersectsWith(new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)))
                return true;
        }
        return false;
    }

    private static bool IsCloaked(IntPtr hwnd) => DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    private void WindowClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        _window.Dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _pending.Stop(); _pending.Tick -= CheckPending;
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        if (!_source.IsDisposed) _source.RemoveHook(WindowMessage);
        _window.Closed -= WindowClosed;
        GC.KeepAlive(_callback);
    }

    private delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr hwnd, int objectId, int childId, uint threadId, uint time);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
