// SPDX-License-Identifier: MPL-2.0
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Merostrap.Macros.Health;
/// <summary>Copies only the calibrated rectangle into memory. Nothing is saved to disk.</summary>
public sealed class WindowsHealthCapture
{
    public static void FocusRobloxForDetection()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (IsForegroundRoblox()) return;
        var players = System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta")
            .Concat(System.Diagnostics.Process.GetProcessesByName("RobloxPlayer")).ToArray();
        try
        {
            var windows = players.Select(p => p.MainWindowHandle).Where(h => h != IntPtr.Zero).ToArray();
            if (windows.Length == 1)
            {
                if (IsIconic(windows[0])) _ = ShowWindow(windows[0], 9);
                _ = SetForegroundWindow(windows[0]);
            }
        }
        finally { foreach (var player in players) player.Dispose(); }
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr window, int command);
    public static HealthRegion? ForegroundRobloxArea(bool top = false)
    {
        if (!IsForegroundRoblox()) return null;
        var window = GetForegroundWindow();
        if (!GetClientRect(window, out var rect)) return null;
        var origin = new NativePoint();
        if (!ClientToScreen(window, ref origin)) return null;
        int width = Math.Min(4096, rect.Right), height = Math.Min(600, rect.Bottom / 2);
        if (width < 160 || height < 40) return null;
        return new(origin.X + (rect.Right - width) / 2, top ? origin.Y : origin.Y + rect.Bottom - height, width, height);
    }
    public static HealthRegion? ForegroundRobloxClientArea()
    {
        if(!IsForegroundRoblox() || !GetClientRect(GetForegroundWindow(),out var rect))return null;
        var origin=new NativePoint();if(!ClientToScreen(GetForegroundWindow(),ref origin))return null;
        return rect.Right is >=300 and <=4096 && rect.Bottom is >=200 and <=2160 ? new(origin.X,origin.Y,rect.Right,rect.Bottom) : null;
    }
    public static nint ForegroundRobloxWindow() => IsForegroundRoblox() ? GetForegroundWindow() : nint.Zero;
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    public HealthPixels Capture(HealthRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        region.Validate();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Health capture requires Windows.");
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        if (region.X < left || region.Y < top || (long)region.X + region.Width > (long)left + width ||
            (long)region.Y + region.Height > (long)top + height)
            throw new ArgumentException("The selected health region is outside the current desktop.", nameof(region));
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot access the screen for health capture.");
        IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the health capture buffer.");
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = region.Width, Height = -region.Height,
                    Planes = 1, BitCount = 32, Compression = 0
                }
            };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot allocate the health capture pixels.");
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot select the health capture buffer.");
            if (!BitBlt(memory, 0, 0, region.Width, region.Height, screen, region.X, region.Y, 0x00CC0020 | 0x40000000))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Screen health capture failed.");
            // GDI can batch the copy. Finish drawing before reading the DIB's
            // memory directly, otherwise detection can receive stale/blank pixels.
            if (!GdiFlush())
                throw new Win32Exception("Cannot finish the screen health capture.");
            var pixels = new byte[checked(region.Width * region.Height * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return new(region.Width, region.Height, pixels);
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
    internal static bool IsForegroundRoblox()
    {
        return WindowsMacroInput.CheckForegroundRoblox();
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPixelsPerMeter, YPixelsPerMeter; public uint ColorsUsed, ImportantColors;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
}
