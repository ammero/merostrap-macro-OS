using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Merostrap.Macros;

namespace Merostrap.UI.Elements.Macros;

public partial class MacroOverlay : Window
{
    private readonly MacroWorkspace _workspace;
    private bool _positionReady;
    private readonly DispatcherTimer _savePosition = new() { Interval = TimeSpan.FromMilliseconds(300) };
    public bool CanDrag => !_workspace.OverlayLocked;
    public MacroOverlay() : this(MacroWorkspace.Current) { }
    public MacroOverlay(MacroWorkspace workspace)
    {
        _workspace = workspace; InitializeComponent(); DataContext = workspace;
        _savePosition.Tick += (_, _) => { _savePosition.Stop(); workspace.SaveOverlayPosition(Position.X, Position.Y); };
        Opened += (_, _) => { ResetPosition(); _positionReady = true; };
        PositionChanged += (_, _) => { if (_positionReady) { _savePosition.Stop(); _savePosition.Start(); } };
        Closing += (_, _) => { _savePosition.Stop(); if (_positionReady && IsVisible) workspace.SaveOverlayPosition(Position.X, Position.Y); _positionReady = false; };
        RobloxOverlayPlacement.Attach(this);
    }
    public void ResetPosition()
    {
        var desired = new PixelPoint(_workspace.OverlayX ?? 24, _workspace.OverlayY ?? 80);
        var screen = Screens.ScreenFromPoint(desired) ?? Screens.Primary;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            var width = (int)Math.Ceiling(Width * screen.Scaling);
            var height = (int)Math.Ceiling(Bounds.Height * screen.Scaling);
            desired = new PixelPoint(Math.Clamp(desired.X, area.X, Math.Max(area.X, area.Right - width)), Math.Clamp(desired.Y, area.Y, Math.Max(area.Y, area.Bottom - height)));
        }
        Position = desired;
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Hiding can happen before the position debounce fires. Flush it so a
        // quick hide/show does not restore the previous position.
        if (change.Property == IsVisibleProperty && !IsVisible && _positionReady)
        {
            _savePosition.Stop();
            _workspace.SaveOverlayPosition(Position.X, Position.Y);
        }
    }
    private void DragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (CanDrag && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
    private void HideClicked(object? sender, RoutedEventArgs e) { _workspace.OverlayVisible = false; Hide(); }
}
