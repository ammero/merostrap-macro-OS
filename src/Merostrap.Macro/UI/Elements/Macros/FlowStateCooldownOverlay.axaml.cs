using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Merostrap.Macros;

namespace Merostrap.UI.Elements.Macros;
public partial class FlowStateCooldownOverlay : Window
{
    private readonly FlowStateOverlayController _model;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _positionReady;
    public FlowStateCooldownOverlay() : this(MacroWorkspace.Current.FlowStateOverlay) { }
    public FlowStateCooldownOverlay(FlowStateOverlayController model)
    {
        _model = model; InitializeComponent(); DataContext = model;
        Win32Properties.AddWindowStylesCallback(this, (style, extended) =>
            ((style & ~0x00CF0000u) | 0x80000000u, extended | 0x08000080u));
        Win32Properties.AddWndProcHookCallback(this, (nint handle, uint message, nint wParam, nint lParam, ref bool handled) =>
        { if (message == 0x0021) { handled = true; return new nint(3); } return nint.Zero; });
        Opened += (_, _) =>
        {
            var desired = new PixelPoint(model.X ?? 24, model.Y ?? 240);
            var screen = Screens.ScreenFromPoint(desired) ?? Screens.Primary;
            if (screen is not null)
            {
                var area = screen.WorkingArea;
                desired = new(Math.Clamp(desired.X, area.X, Math.Max(area.X, area.Right - (int)(Width * screen.Scaling))),
                    Math.Clamp(desired.Y, area.Y, Math.Max(area.Y, area.Bottom - (int)(Height * screen.Scaling))));
            }
            Position = desired; _positionReady = true;

        };
        _save.Tick += (_, _) => FlushPosition();
        PositionChanged += (_, _) => { if (_positionReady) { _save.Stop(); _save.Start(); } };
        Closing += (_, _) => { if (IsVisible) FlushPosition(); _positionReady = false; _save.Stop(); };
        RobloxOverlayPlacement.Attach(this);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && !IsVisible && _positionReady) FlushPosition();
    }
    private void FlushPosition() { _save.Stop(); if (_positionReady) _model.SavePosition(Position.X, Position.Y); }
    private void DragPressed(object? sender, PointerPressedEventArgs e)
    { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); }
}
