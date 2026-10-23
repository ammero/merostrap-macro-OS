using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;

namespace Merostrap.UI.Elements.Macros;

public sealed class MacroPointPicker : Window
{
    private MacroPointPicker(Screen screen, string label, Action<PixelPoint?> finish)
    {
        OverlayCapturePrivacy.Attach(this);
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Topmost = true; ShowInTaskbar = false; CanResize = false;
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling; Height = screen.Bounds.Height / screen.Scaling;
        Background = Brush.Parse("#20000000"); TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Cursor = new Cursor(StandardCursorType.Cross);
        Content = new Border { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(16), Padding = new Thickness(16, 10), CornerRadius = new CornerRadius(6), Background = Brush.Parse("#EE181A20"),
            IsHitTestVisible = false, Child = new TextBlock { Text = $"Click to pin {label.ToLowerInvariant()} · Esc cancels", Foreground = Brushes.White } };
        PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) finish(this.PointToScreen(e.GetPosition(this))); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; finish(null); } };
    }
    public static Task<PixelPoint?> PickAsync(Window owner, string label)
    {
        var result = new TaskCompletionSource<PixelPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = new List<MacroPointPicker>();
        void Finish(PixelPoint? point)
        {
            if (!result.TrySetResult(point)) return;
            foreach (var window in windows) window.Close();
        }
        try
        {
            foreach (var screen in owner.Screens.All) windows.Add(new MacroPointPicker(screen, label, Finish));
            if (windows.Count == 0) Finish(null);
            foreach (var window in windows) { window.Closed += (_, _) => Finish(null); window.Show(); }
        }
        catch { Finish(null); throw; }
        return result.Task;
    }
}
