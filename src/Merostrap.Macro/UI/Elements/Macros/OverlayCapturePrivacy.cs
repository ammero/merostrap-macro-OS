using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Merostrap.UI.Elements.Macros;

/// <summary>Keeps the ritual recognition outline out of its own OCR input. Regular overlays remain shareable.</summary>
public static class OverlayCapturePrivacy
{
    private static readonly HashSet<Window> Windows = [];

    public static void Attach(Window window)
    {
        if (window is not RitualPromptOverlay || !Windows.Add(window)) return;
        window.Opened += (_, _) => Apply(window);
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.IsVisibleProperty && window.IsVisible) Apply(window);
        };
        window.Closed += (_, _) => Windows.Remove(window);
        Apply(window);
    }

    private static bool Hidden(Window window) => window switch
    {
        // Recognition reads the desktop. Its own outline must never become
        // part of the next glyph, even when other overlays may be shared.
        RitualPromptOverlay => true,
        _ => false
    };

    private static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var platform = window.TryGetPlatformHandle();
        if (platform is not { Handle: var handle, HandleDescriptor: "HWND" } || handle == nint.Zero) return;
        var hidden = Hidden(window);
        uint requested = hidden ? 0x11u : 0u;
        if (SetWindowDisplayAffinity(handle, requested) &&
            GetWindowDisplayAffinity(handle, out var actual) && actual == requested) return;
        var error = Marshal.GetLastWin32Error();
        Trace.TraceError($"Capture protection failed for {window.GetType().Name} (Win32 {error}).");
        // Do not leave a visible, unprotected overlay when privacy was requested.
        if (hidden) window.Hide();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
}
