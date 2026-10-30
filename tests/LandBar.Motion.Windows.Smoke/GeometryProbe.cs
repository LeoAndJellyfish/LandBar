using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

internal sealed class GeometryProbe(Window window)
{
    private const int WmWindowPosChanged = 0x0047;
    private HwndSource? _source;
    public bool Recording { get; set; }
    public double CenterX { get; set; }
    public int Top { get; set; }
    public int NativeChanges { get; private set; }
    public double MaximumCenterError { get; private set; }
    public double MaximumTopError { get; private set; }
    public void Attach()
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        _source.AddHook(Hook);
    }
    public void Detach() => _source?.RemoveHook(Hook);
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmWindowPosChanged && Recording && GetWindowRect(hwnd, out var rect))
        {
            NativeChanges++;
            MaximumCenterError = Math.Max(MaximumCenterError, Math.Abs((rect.Left + rect.Right) / 2d - CenterX));
            MaximumTopError = Math.Max(MaximumTopError, Math.Abs(rect.Top - Top));
        }
        return IntPtr.Zero;
    }
    public static Rect PhysicalBounds(Window window)
    {
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out var rect)) throw new InvalidOperationException("GetWindowRect failed");
        return new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    public static void MoveOnly(Window window, int x, int y)
    {
        if (!SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010))
            throw new InvalidOperationException("Move-only SetWindowPos failed");
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
