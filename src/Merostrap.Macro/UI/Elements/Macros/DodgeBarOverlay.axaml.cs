using AnimatedImage.Avalonia;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Merostrap.Macros;

namespace Merostrap.UI.Elements.Macros;
public partial class DodgeBarOverlay : Window
{
    private static DodgeBarOverlay? _visibleOverlay;
    private Mutex? _visibilityMutex;
    // A session-wide lease also covers a second Merostrap process.
    public bool TryShow()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsVisible) return true;
        if (_visibleOverlay is not null) return false;
        var mutex = new Mutex(false, "Local\\Merostrap.OpenMacro.DodgeBarOverlay");
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { mutex.Dispose(); return false; }
        _visibilityMutex = mutex;
        _visibleOverlay = this;
        try { Show(); return true; }
        catch { ReleaseVisibility(); throw; }
    }
    private void ReleaseVisibility()
    {
        if (_visibilityMutex is null) return;
        _visibilityMutex.ReleaseMutex();
        _visibilityMutex.Dispose();
        _visibilityMutex = null;
        if (ReferenceEquals(_visibleOverlay, this)) _visibleOverlay = null;
    }
    private readonly DodgeBarController _model;
    private bool _positionReady;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(300) };
    public bool CanDrag => true;
    public DodgeBarOverlay() : this(MacroWorkspace.Current.DodgeBar) { }
    public DodgeBarOverlay(DodgeBarController model)
    {
        _model = model; InitializeComponent(); DataContext = model;
        // A popup has no caption-sized system minimum, unlike an overlapped window.
        Win32Properties.AddWindowStylesCallback(this, (style, extended) =>
            ((style & ~0x00CF0000u) | 0x80000000u, extended));
        _model.PropertyChanged += ImageChanged;
        _save.Tick += (_, _) => FlushPosition();
        Opened += (_, _) =>
        {
            var desired = new PixelPoint(model.X ?? 24, model.Y ?? 230);
            var screen = Screens.ScreenFromPoint(desired) ?? Screens.Primary;
            if (screen is not null)
            {
                var area = screen.WorkingArea;
                desired = new(Math.Clamp(desired.X, area.X, Math.Max(area.X, area.Right - (int)(Width * screen.Scaling))),
                    Math.Clamp(desired.Y, area.Y, Math.Max(area.Y, area.Bottom - (int)(80 * screen.Scaling))));
            }
            Position = desired; _positionReady = true;

        };
        PositionChanged += (_, _) => { if (_positionReady) { _save.Stop(); _save.Start(); } };
        Closing += (_, _) => { if (IsVisible) FlushPosition(); _save.Stop(); _positionReady = false; };
        Closed += (_, _) => { ReleaseVisibility(); _model.PropertyChanged -= ImageChanged; ImageBehavior.SetAnimatedSource(DodgeImage, null!); };
        RobloxOverlayPlacement.Attach(this);
    }
    private void ImageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DodgeBarController.AnimatedImagePath)) UpdateAnimation();
    }
    private void UpdateAnimation()
    {
        if (DodgeImage is null || _model is null) return;
        ImageBehavior.SetAnimatedSource(DodgeImage, null!);
        DodgeImage.Source = _model.BarImage;
        if (IsVisible && _model.AnimatedImagePath is { } path)
        {
            ImageBehavior.SetAnimatedSource(DodgeImage, new AnimatedImageSourceUri(new Uri(path, UriKind.Absolute)));
            ImageBehavior.SetRepeatBehavior(DodgeImage, RepeatBehavior.Forever);
        }
    }
    private void FlushPosition() { _save.Stop(); if (_positionReady) _model.SavePosition(Position.X, Position.Y); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && !IsVisible) ReleaseVisibility();
        if (change.Property == IsVisibleProperty && !IsVisible && _positionReady) FlushPosition();
        if (change.Property == IsVisibleProperty) UpdateAnimation();
    }
    private void DragPressed(object? sender, PointerPressedEventArgs e)
    { if (CanDrag && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); }
}
