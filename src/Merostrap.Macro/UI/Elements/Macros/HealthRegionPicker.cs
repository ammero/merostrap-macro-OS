using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Merostrap.Macros.Health;

namespace Merostrap.UI.Elements.Macros;

public sealed class HealthRegionPicker : Window
{
    private readonly Canvas _canvas = new();
    private readonly Rectangle _selection = new() { Stroke = Brush.Parse("#D0BAFF"), StrokeThickness = 2, Fill = Brush.Parse("#30B8A8FF"), IsVisible = false };
    private readonly Action<HealthRegion?> _finish;
    private Point? _start;
    private HealthRegionPicker(Screen screen, Action<HealthRegion?> finish, string hintText, int maxHeight,int minWidth,int maxWidth)
    {
        OverlayCapturePrivacy.Attach(this);
        _finish = finish;
        Title = "Merostrap · Select area";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None; CanResize = false; Topmost = true; ShowInTaskbar = false;
        Position = screen.Bounds.Position; Width = screen.Bounds.Width / screen.Scaling; Height = screen.Bounds.Height / screen.Scaling;
        Background = Brush.Parse("#40000000"); TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Cursor = new Cursor(StandardCursorType.Cross);
        _canvas.Children.Add(_selection);
        var hint = new Border { Background = Brush.Parse("#EE181A20"), CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 12),
            Child = new TextBlock { Text = hintText, Foreground = Brushes.White, FontSize = 14 } };
        Canvas.SetLeft(hint, 24); Canvas.SetTop(hint, 24); _canvas.Children.Add(hint);
        Content = _canvas;
        PointerPressed += (_, e) => { if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; _start = e.GetPosition(this); e.Pointer.Capture(this); _selection.IsVisible = true; Update(_start.Value); };
        PointerMoved += (_, e) => { if (_start.HasValue) Update(e.GetPosition(this)); };
        PointerReleased += (_, e) =>
        {
            if (!_start.HasValue) return;
            var a = this.PointToScreen(_start.Value); var b = this.PointToScreen(e.GetPosition(this));
            e.Pointer.Capture(null); _start = null;
            var region = new HealthRegion(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
            if (region.Width >= minWidth && region.Height >= 4 && region.Width <= maxWidth && region.Height <= maxHeight) _finish(region);
            else _selection.IsVisible = false;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; _finish(null); } };
    }
    private void Update(Point point)
    {
        var start = _start!.Value;
        Canvas.SetLeft(_selection, Math.Min(start.X, point.X)); Canvas.SetTop(_selection, Math.Min(start.Y, point.Y));
        _selection.Width = Math.Abs(start.X - point.X); _selection.Height = Math.Abs(start.Y - point.Y);
    }
    public static Task<HealthRegion?> PickAsync(Window owner, string hintText = "Select the whole health bar, including the percentage and HEALTH text. Esc cancels.", int maxHeight = 300,int minWidth=30,int maxWidth=4096)
    {
        var result = new TaskCompletionSource<HealthRegion?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = new List<HealthRegionPicker>();
        void Finish(HealthRegion? region)
        {
            if (!result.TrySetResult(region)) return;
            foreach (var window in windows) window.Close();
        }
        foreach (var screen in owner.Screens.All) windows.Add(new HealthRegionPicker(screen, Finish, hintText, maxHeight,minWidth,maxWidth));
        if (windows.Count == 0) return Task.FromResult<HealthRegion?>(null);
        foreach (var window in windows) { window.Closed += (_, _) => Finish(null); window.Show(); }
        return result.Task;
    }
}
