using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LandBar.Presentation;

/// <summary>The HWND rectangle in physical desktop pixels.</summary>
public readonly record struct WindowPixelBounds(int Left, int Top, int Width, int Height)
{
    public double CenterX => Left + Width / 2d;
}

/// <summary>
/// Applies position and size in one native transaction. Writing WPF Width/Height followed
/// by a move produces intermediate HWND rectangles whose center shifts during animation.
/// </summary>
public static class NativeWindowBounds
{
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint NoOwnerZOrder = 0x0200;

    public static WindowPixelBounds Calculate(int workLeft, int workTop, int workWidth,
        double topOffset, double width, double height, double dpiX, double dpiY)
    {
        if (workWidth <= 0) throw new ArgumentOutOfRangeException(nameof(workWidth));
        Validate(topOffset, nameof(topOffset), allowZero: true, allowNegative: true);
        Validate(width, nameof(width)); Validate(height, nameof(height));
        Validate(dpiX, nameof(dpiX)); Validate(dpiY, nameof(dpiY));

        // Match the work area's parity, keeping its exact integer/half-pixel center.
        // Otherwise alternate odd/even widths make the pill and text sway by half a pixel.
        int parity = workWidth & 1;
        double requestedPixels = width * dpiX;
        int pixelWidth = checked(parity + 2 * (int)Math.Round((requestedPixels - parity) / 2,
            MidpointRounding.AwayFromZero));
        pixelWidth = Math.Max(parity == 0 ? 2 : 1, pixelWidth);
        int pixelHeight = Math.Max(1, checked((int)Math.Round(height * dpiY, MidpointRounding.AwayFromZero)));
        int left = checked(workLeft + (workWidth - pixelWidth) / 2);
        int top = checked(workTop + (int)Math.Round(topOffset * dpiY, MidpointRounding.AwayFromZero));
        return new WindowPixelBounds(left, top, pixelWidth, pixelHeight);
    }

    public static WindowPixelBounds Apply(Window window, int workLeft, int workTop, int workWidth,
        double topOffset, double width, double height, double dpiX, double dpiY)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) throw new InvalidOperationException("The window handle is not ready.");
        var bounds = Calculate(workLeft, workTop, workWidth, topOffset, width, height, dpiX, dpiY);
        if (GetWindowRect(handle, out var current) && current.Left == bounds.Left && current.Top == bounds.Top &&
            current.Right - current.Left == bounds.Width && current.Bottom - current.Top == bounds.Height) return bounds;

        if (!SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                NoZOrder | NoActivate | NoOwnerZOrder))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return bounds;
    }

    private static void Validate(double value, string name, bool allowZero = false, bool allowNegative = false)
    {
        if (!double.IsFinite(value) || (!allowNegative && (allowZero ? value < 0 : value <= 0)))
            throw new ArgumentOutOfRangeException(name);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window,
        IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
