using Avalonia.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Merostrap.Macros;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Runtime.InteropServices;

namespace Merostrap.UI.Elements.Macros;

public partial class MacroWindow : Window
{
    private readonly MacroWorkspace _workspace;
    private bool _positionReady;
    private bool _keyboardEditing;
    private readonly DispatcherTimer _savePosition = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private long _lastAnimationTick;
    private double _animationElapsed, _animationDuration, _fromScale;
    private bool _reducedAnimation;
    private nint _returnWindow;
    private Size? _compactSize;
    private readonly DispatcherTimer _searchAnimation = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double _searchTargetWidth;
    private long _searchLastTick;
    public bool IsPanelOpen { get; private set; }
    public MacroWindow() : this(MacroWorkspace.Current) { }
    public MacroWindow(MacroWorkspace workspace)
    {
        _workspace = workspace; InitializeComponent(); DataContext = workspace;
        OverlayCapturePrivacy.Attach(this);
        Win32Properties.AddWindowStylesCallback(this,(style,extended)=>(style,(extended & ~0x08000080u) | 0x00040000u));
        Deactivated+=(_,_)=> { _keyboardEditing=false;ConfigureOverlayWindow(); };
        AddHandler(PointerPressedEvent,(_,e)=> {
            if (e.Source is Visual visual && (visual is TextBox || visual.GetVisualAncestors().OfType<TextBox>().Any())) BeginKeyboardEditing();
        },RoutingStrategies.Tunnel,true);
        AddHandler(GotFocusEvent,(_,e)=> { if(e.Source is TextBox) BeginKeyboardEditing(); },RoutingStrategies.Bubble,true);
        AddHandler(LostFocusEvent,(_,_)=>Dispatcher.UIThread.Post(()=> {
            if(FocusManager?.GetFocusedElement() is not TextBox && !KeybindButton.SuppressShortcuts) EndKeyboardEditing();
        }),RoutingStrategies.Bubble,true);
        PresetNameBox.KeyDown+=(_,e)=> {
            if(e.Key is Key.Enter or Key.Escape) { e.Handled=true;RenameToggle.IsChecked=false;EndKeyboardEditing(); }
        };
        _savePosition.Tick += (_, _) => FlushPosition();
        PositionChanged += (_, _) => { if (_positionReady && WindowState == Avalonia.Controls.WindowState.Normal) { _savePosition.Stop(); _savePosition.Start(); } };
        MacroTheme.Apply(this, workspace.GuiTheme);
        workspace.PropertyChanged += ThemeChanged;
        _animation.Tick += (_, _) => TickAnimation();
        _searchAnimation.Tick += (_, _) => TickSearchAnimation();
        Opened += (_, _) => { RestorePosition(); ConfigureOverlayWindow(); };
        SizeChanged += (_, _) => ClipNativeFrame();

        Closing += (_, _) => { if (IsVisible) FlushPosition(); _positionReady = false; _savePosition.Stop(); };
        Closed += (_, _) => {IsPanelOpen = false; _animation.Stop(); _searchAnimation.Stop(); workspace.PropertyChanged -= ThemeChanged; };
    }
    private void RenamePresetClicked(object? sender,RoutedEventArgs e)
    {
        if(RenameToggle.IsChecked!=true) { EndKeyboardEditing();return; }
        Dispatcher.UIThread.Post(()=> {
            if(!IsVisible || RenameToggle.IsChecked!=true)return;
            BeginKeyboardEditing();PresetNameBox.Focus();PresetNameBox.SelectAll();
        },DispatcherPriority.Loaded);
    }
    internal void BeginKeyboardEditing()
    {
        if(_keyboardEditing)return;
        _keyboardEditing=true;ConfigureOverlayWindow();Activate();
    }
    internal void EndKeyboardEditing()
    {
        if(!_keyboardEditing)return;
        _keyboardEditing=false;ConfigureOverlayWindow();
    }
    private void RestorePosition()
    {
        if (_workspace.GuiX is { } x && _workspace.GuiY is { } y)
        {
            var desired = new PixelPoint(x, y);
            var screen = Screens.ScreenFromPoint(desired) ?? Screens.Primary;
            if (screen is not null)
            {
                var area = screen.WorkingArea;
                desired = new(Math.Clamp(x, area.X, Math.Max(area.X, area.Right - (int)Math.Ceiling(Width * screen.Scaling))),
                    Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - (int)Math.Ceiling(Height * screen.Scaling))));
            }
            Position = desired;
        }
        _positionReady = true;
    }
    private void FlushPosition()
    {
        _savePosition.Stop();
        if (_positionReady && WindowState == Avalonia.Controls.WindowState.Normal) _workspace.SaveGuiPosition(Position.X, Position.Y);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && !IsVisible && _positionReady) { FlushPosition();EndKeyboardEditing(); }
    }
    private void SetDropdownArrow(object? sender, bool open)
    {
        if(sender is Avalonia.Controls.Primitives.Popup { TemplatedParent: ComboBox combo })
        {
            var arrow=combo.GetVisualDescendants().OfType<Control>().FirstOrDefault(c=>c.Name=="MacDropdownArrow");
            if(arrow!=null){arrow.RenderTransformOrigin=RelativePoint.Center;arrow.RenderTransform=new RotateTransform(open?180:0);}
        }
    }
    private void DropdownClosed(object? sender, EventArgs e)=>SetDropdownArrow(sender,false);
    private void DropdownOpened(object? sender, EventArgs e)
    {
        SetDropdownArrow(sender,true);
        if(sender is not Avalonia.Controls.Primitives.Popup { Child: Border menu } popup)return;
        var reduced=_workspace.ReduceMotion;
        var transform=new ScaleTransform(1,reduced ? .96 : .88);
        menu.RenderTransformOrigin=new RelativePoint(.5,0,RelativeUnit.Relative);
        menu.RenderTransform=transform;
        var start=Environment.TickCount64;
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(16) };
        timer.Tick+=(_,_)=> {
            if(!popup.IsOpen || !IsVisible){timer.Stop();transform.ScaleY=1;return;}
            double progress=Math.Clamp((Environment.TickCount64-start)/180d,0,1);
            transform.ScaleY=1-(reduced ? .04 : .12)*Math.Pow(1-progress,3);
            if(progress>=1){timer.Stop();transform.ScaleY=1;}
        };
        timer.Start();
    }
    private void ThemeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MacroWorkspace.GuiTheme) && sender is MacroWorkspace workspace)
            MacroTheme.Apply(this, workspace.GuiTheme);
    }
    public void ShowPanel(bool activate = false)
    {
        if (OperatingSystem.IsWindows() && Merostrap.Macros.Health.WindowsHealthCapture.ForegroundRobloxWindow() is var game && game != nint.Zero)
            _returnWindow = game;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        var animate = !IsPanelOpen;
        IsPanelOpen = true;
        ShowActivated = activate;
        if (animate && !IsVisible)
        {
            _reducedAnimation = _workspace.ReduceMotion;
            GuiSurface.Opacity = 1;
            ((ScaleTransform)GuiSurface.RenderTransform!).ScaleX = _reducedAnimation ? .94 : .88;
            ((ScaleTransform)GuiSurface.RenderTransform!).ScaleY = _reducedAnimation ? .94 : .88;
        }
        Show();
        ConfigureOverlayWindow();
        if (activate) Activate();
        if (animate) AnimatePanel(true);

    }
    public void HidePanel()
    {
        WindowState = WindowState.Minimized;
    }
    private void AnimatePanel(bool opening)
    {
        _animation.Stop();
        _fromScale = ((ScaleTransform)GuiSurface.RenderTransform!).ScaleX;
        _lastAnimationTick = Environment.TickCount64;
        _animationElapsed = 0;
        _reducedAnimation = _workspace.ReduceMotion;
        _animationDuration = _reducedAnimation ? (opening ? 180 : 140) : opening ? 180 : 120;
        _animation.Start();
    }
    private void TickAnimation()
    {
        var now = Environment.TickCount64;
        // Window creation or a busy UI thread must not consume the whole animation
        // before its first frames can be displayed.
        _animationElapsed += Math.Clamp(now - _lastAnimationTick, 0, 32);
        _lastAnimationTick = now;
        double progress = Math.Clamp(_animationElapsed / _animationDuration, 0, 1);
        double eased = _reducedAnimation ? progress * progress * (3 - 2 * progress) : 1 - Math.Pow(1 - progress, 3);
        double targetScale = IsPanelOpen ? 1 : _reducedAnimation ? .94 : .88;
        double scale = _fromScale + (targetScale - _fromScale) * eased;
        ((ScaleTransform)GuiSurface.RenderTransform!).ScaleX = scale;
        ((ScaleTransform)GuiSurface.RenderTransform!).ScaleY = scale;
        if (progress >= 1) FinishAnimation();
    }
    private void FinishAnimation()
    {
        _animation.Stop();
        if (!IsPanelOpen)
        {
            var restore = IsActive;
            Hide();
            if (restore) RestoreGameFocus();
        }
        GuiSurface.Opacity = 1;
        ((ScaleTransform)GuiSurface.RenderTransform!).ScaleX = 1;
        ((ScaleTransform)GuiSurface.RenderTransform!).ScaleY = 1;
    }
    internal void RestoreGameFocus()
    {
        if (!OperatingSystem.IsWindows() || _returnWindow == nint.Zero) return;
        GetWindowThreadProcessId(_returnWindow, out var pid);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            if (process.ProcessName is "RobloxPlayerBeta" or "RobloxPlayer") _ = SetForegroundWindow(_returnWindow);
        }
        catch (ArgumentException) { }
    }
    private void ConfigureOverlayWindow()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } handle || handle == nint.Zero) return;
        // The standalone editor is a normal taskbar app; floating HUD windows keep their own styles.
        var style = nint.Size == 8 ? GetWindowLongPtr(handle, -20).ToInt64() : GetWindowLong(handle, -20);
        style = (style & ~0x08000080L) | 0x40000L;
        if (nint.Size == 8) _ = SetWindowLongPtr(handle, -20, new nint(style));
        else _ = SetWindowLong(handle, -20, (int)style);
        _ = SetWindowPos(handle, nint.Zero, 0, 0, 0, 0, 0x37);
        ClipNativeFrame();
    }
    private void ClipNativeFrame()
    {
        if(!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not { } handle || handle==nint.Zero || !GetWindowRect(handle,out var rect))return;
        int inset=(int)Math.Round(2*RenderScaling),radius=(int)Math.Round(24*RenderScaling);
        var region=CreateRoundRectRgn(inset,inset,rect.Right-rect.Left-inset+1,rect.Bottom-rect.Top-inset+1,radius,radius);
        if(region!=nint.Zero && SetWindowRgn(handle,region,true)==0)DeleteObject(region);
        // Windows owns the region after a successful SetWindowRgn call.
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window,out NativeRect rect);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left,int top,int right,int bottom,int ellipseWidth,int ellipseHeight);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window,nint region,[MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    private async void PickPathClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MacroFieldRow field }) return;
        using var edit = (DataContext as MacroWorkspace)?.PauseForSettingsEdit();
        try
        {
            if (field.Key == "basepath")
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select ritual template folder", AllowMultiple = false });
                if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) field.Value = path;
            }
            else
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select message file", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Text files") { Patterns = ["*.txt"] }] });
                if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) field.Value = path;
            }
        }
        catch (Exception ex) { if (DataContext is MacroWorkspace model) model.ReportError("Could not select path: " + ex.Message); }
        finally { ShowPanel(false); }
    }
    private async void PickPointClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MacroFieldRow field }) return;
        using var edit = (DataContext as MacroWorkspace)?.PauseForSettingsEdit();
        Hide();
        try
        {
            var point = await MacroPointPicker.PickAsync(this, field.Label);
            if (point is { } picked) field.Value = $"{picked.X} {picked.Y}";
        }
        catch (Exception ex) { if (DataContext is MacroWorkspace model) model.ReportError("Could not pin point: " + ex.Message); }
        finally { ShowPanel(false); }
    }
    private void TitlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
    }
    private async void DetectRitualClicked(object? sender, RoutedEventArgs e) => await PickRitual(sender, true);
    private async void SelectRitualClicked(object? sender, RoutedEventArgs e) => await PickRitual(sender, false);
    private async Task PickRitual(object? sender, bool automatic)
    {
        if (sender is not Button { DataContext: MacroRow row } || !row.IsRitual || DataContext is not MacroWorkspace workspace) return;
        using var edit = workspace.PauseForSettingsEdit();
        try
        {
            Merostrap.Macros.Health.HealthRegion? region;
            if (automatic) region = await RitualAutoDetection.PickAsync(this);
            else
            {
                Hide();
                region = await HealthRegionPicker.PickAsync(this, "Drag around the entire outlined ritual frame, including all four edges. Exclude the title. Esc cancels.");
            }
            if (region is not null) row.SetRitualArea(region);
        }
        catch (Exception ex) { workspace.ReportError("Could not select ritual prompt: " + ex.Message); }
        finally { ShowPanel(false); }
    }
    private void FieldChoicePressed(object? sender, PointerPressedEventArgs e)
    { if(sender is ComboBox {DataContext: MacroFieldRow field}) field.RefreshChoices(); }
    private void CloseClicked(object? sender, RoutedEventArgs e) => Close();
    private void MinimizeClicked(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void SearchToggleClicked(object? sender, RoutedEventArgs e)
    {
        _searchAnimation.Stop();
        _searchTargetWidth = SearchToggle.IsChecked == true ? 220 : 0;
        _searchLastTick = Environment.TickCount64;
        if (_searchTargetWidth > 0) { SearchBox.Width = 220; SearchBox.IsVisible = true; SearchSurface.Background = (IBrush)this.Resources["MacroTextField"]!; SearchBox.Focus(); }
        SearchSurface.Width = _searchTargetWidth + 30;
        if (_searchTargetWidth == 0 && DataContext is MacroWorkspace workspace) workspace.Search = "";
        _searchAnimation.Start();
    }
    private void TickSearchAnimation()
    {
        // Width transitions run on Avalonia's render clock. The timer only finalizes
        // visibility, so it cannot introduce uneven animation frames.
        if (Environment.TickCount64 - _searchLastTick < 260) return;
        _searchAnimation.Stop();
        if (_searchTargetWidth == 0) { SearchBox.IsVisible = false; SearchBox.Width = 0; SearchSurface.Background = Brushes.Transparent; }

    }
    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SearchToggle.IsChecked = false; SearchToggleClicked(SearchToggle, new RoutedEventArgs());
        SearchToggle.Focus(); e.Handled = true;
    }
    private void ExpandClicked(object? sender, RoutedEventArgs e)
    {
        if (_compactSize is { } compact) { Width = compact.Width; Height = compact.Height; _compactSize = null; }
        else { _compactSize = new Size(Width, Height); Width = 920; Height = 640; }
    }
}
