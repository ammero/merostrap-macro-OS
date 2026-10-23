using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Merostrap.UI.Elements.Macros;

public sealed class HudDetectionDialog : Window
{
    public HudDetectionDialog()
    {
        OverlayCapturePrivacy.Attach(this);
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Win32Properties.AddWindowStylesCallback(this, (style, extended) =>
            (style, (extended | 0x08000080u) & ~0x00040000u));
        Win32Properties.AddWndProcHookCallback(this, (nint h, uint message, nint w, nint l, ref bool handled) =>
        {
            if (message == 0x0021) { handled = true; return new nint(3); }
            return nint.Zero;
        });
        Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
            if (screen is null) return;
            var area = screen.WorkingArea;
            int width = (int)Math.Ceiling(Bounds.Width * screen.Scaling);
            int height = (int)Math.Ceiling(Bounds.Height * screen.Scaling) + 40;
            Position = new PixelPoint(
                Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - width)),
                Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - height)));
        });
    }
}
