using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Merostrap.UI.Elements.Macros;

/// <summary>A focused keyboard and side-mouse-button recorder.</summary>
public sealed class KeybindButton : Button
{
    private static long _ignoreShortcutsUntil;
    private static KeybindButton? _recorder;
    private TopLevel? _captureRoot;
    private double _captureMinWidth;
    private IDisposable? _editSession;
    public static bool SuppressShortcuts => _recorder?.IsListening == true || Environment.TickCount64 < _ignoreShortcutsUntil;
    public static readonly StyledProperty<string> ValueProperty = AvaloniaProperty.Register<KeybindButton, string>(
        nameof(Value), "", defaultBindingMode: BindingMode.TwoWay);
    public string Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public static readonly StyledProperty<bool> AllowMainMouseButtonsProperty = AvaloniaProperty.Register<KeybindButton, bool>(nameof(AllowMainMouseButtons));
    public bool AllowMainMouseButtons { get => GetValue(AllowMainMouseButtonsProperty); set => SetValue(AllowMainMouseButtonsProperty, value); }
    public bool IsListening { get; private set; }
    protected override Type StyleKeyOverride => typeof(Button);

    public KeybindButton()
    {
        MinWidth = 68; Height = 32; Padding = new Thickness(10, 0);
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono, Consolas, monospace");
        FontSize = 12; CornerRadius = new CornerRadius(0);
        Background = Brush.Parse("#1B1D24"); BorderBrush = Brush.Parse("#3C404D"); BorderThickness = new Thickness(1);
        ToolTip.SetTip(this, "Click, then press a keyboard key or side mouse button anywhere in this window. Numpad keys are separate. Esc cancels. Right-click or Delete clears when not recording.");
        AddHandler(KeyDownEvent, CaptureKey, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, e) => {
            if(e.Handled || e.GetCurrentPoint(this).Properties.PointerUpdateKind!=PointerUpdateKind.RightButtonPressed ||
                IsListening && AllowMainMouseButtons)return;
            e.Handled=true;
            e.Pointer.Capture(null);
            ClearKey();
        },RoutingStrategies.Tunnel,true);
        UpdateCaption();
    }

    private void ClearKey()
    {
        _ignoreShortcutsUntil=Environment.TickCount64+500;
        EndCapture();
        SetCurrentValue(ValueProperty, "");
    }
    public void BeginCapture()
    {
        _recorder?.EndCapture();
        _recorder = this;
        _captureMinWidth = MinWidth;
        SetCurrentValue(MinWidthProperty, Math.Max(MinWidth, Bounds.Width));
        IsListening = true;
        _captureRoot = TopLevel.GetTopLevel(this);
        if(_captureRoot is MacroWindow window)window.BeginKeyboardEditing();
        _editSession = (_captureRoot?.DataContext as Merostrap.Macros.MacroWorkspace)?.PauseForSettingsEdit();
        _captureRoot?.AddHandler(PointerPressedEvent, CaptureMouse, RoutingStrategies.Tunnel, true);
        // Avalonia reports an additional mouse button as a move while another button is held.
        _captureRoot?.AddHandler(PointerMovedEvent, CaptureMouse, RoutingStrategies.Tunnel, true);
        Focus(); UpdateCaption();
    }
    private void EndCapture()
    {
        var window=_captureRoot as MacroWindow;
        _captureRoot?.RemoveHandler(PointerPressedEvent, CaptureMouse);
        _captureRoot?.RemoveHandler(PointerMovedEvent, CaptureMouse);
        _captureRoot = null;
        if (IsListening) SetCurrentValue(MinWidthProperty, _captureMinWidth);
        IsListening = false;
        if (ReferenceEquals(_recorder, this)) _recorder = null;
        UpdateCaption();
        var edit = _editSession; _editSession = null;
        edit?.Dispose();
        window?.EndKeyboardEditing();
    }
    protected override void OnClick() { base.OnClick(); BeginCapture(); }
    protected override void OnLostFocus(FocusChangedEventArgs e) { EndCapture(); base.OnLostFocus(e); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { EndCapture(); base.OnDetachedFromVisualTree(e); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is MacroWindow)
        {
            this.Bind(BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("MacroInput"));
            UpdateCaption();
        }
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty) UpdateCaption();
    }
    private void UpdateCaption()
    {
        Content = IsListening ? "…" : string.IsNullOrWhiteSpace(Value) ? "Not set" : Value.ToUpperInvariant();
        if (TopLevel.GetTopLevel(this) is MacroWindow)
            this.Bind(BorderBrushProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(IsListening ? "MacroAccent" : "MacroBorder"));
        else BorderBrush = Brush.Parse(IsListening ? "#B8A8FF" : "#3C404D");
    }
    private void CaptureKey(object? sender, KeyEventArgs e)
    {
        if (!IsListening) {
            if(e.Key==Key.Delete){e.Handled=true;ClearKey();}
            return;
        }
        _ignoreShortcutsUntil = Environment.TickCount64 + 500;
        e.Handled = true;
        if (e.Key == Key.Escape) { EndCapture(); return; }
        var key = KeyName(e.Key, e.PhysicalKey);
        if (key is null) return;
        SetCurrentValue(ValueProperty, key);
        EndCapture();
    }
    private void CaptureMouse(object? sender, PointerEventArgs e)
    {
        if (!IsListening) return;
        var key = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.XButton1Pressed => "mouse4",
            PointerUpdateKind.XButton2Pressed => "mouse5",
            PointerUpdateKind.LeftButtonPressed when AllowMainMouseButtons => "mouse1",
            PointerUpdateKind.RightButtonPressed when AllowMainMouseButtons => "mouse2",
            PointerUpdateKind.MiddleButtonPressed when AllowMainMouseButtons => "mouse3", _ => null
        };
        if (key is null) return;
        e.Handled = true;
        _ignoreShortcutsUntil = Environment.TickCount64 + 500;
        e.Pointer.Capture(null);
        SetCurrentValue(ValueProperty, key);
        EndCapture();
    }
    private static string? KeyName(Key key, PhysicalKey physical)
    {
        if (physical >= PhysicalKey.NumPad0 && physical <= PhysicalKey.NumPad9)
            return "numpad" + ((int)physical - (int)PhysicalKey.NumPad0).ToString(CultureInfo.InvariantCulture);
        var numpad = physical switch
        {
            PhysicalKey.NumPadAdd => "numpad add", PhysicalKey.NumPadSubtract => "numpad subtract",
            PhysicalKey.NumPadMultiply => "numpad multiply", PhysicalKey.NumPadDivide => "numpad divide",
            PhysicalKey.NumPadDecimal => "numpad decimal", PhysicalKey.NumPadEnter => "numpad enter", _ => null
        };
        if (numpad is not null) return numpad;
        if (key >= Key.A && key <= Key.Z) return ((char)('a' + (int)key - (int)Key.A)).ToString();
        if (key >= Key.D0 && key <= Key.D9) return ((int)key - (int)Key.D0).ToString(CultureInfo.InvariantCulture);
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return "numpad" + ((int)key - (int)Key.NumPad0).ToString(CultureInfo.InvariantCulture);
        if (key >= Key.F1 && key <= Key.F24) return "f" + ((int)key - (int)Key.F1 + 1).ToString(CultureInfo.InvariantCulture);
        return key switch
        {
            Key.Back => "backspace", Key.Tab => "tab", Key.Enter => "enter", Key.Space => "space",
            Key.LeftCtrl or Key.RightCtrl => "ctrl", Key.LeftShift or Key.RightShift => "shift",
            Key.LeftAlt or Key.RightAlt => "alt", Key.CapsLock => "caps lock",
            Key.Up => "up", Key.Down => "down", Key.Left => "left", Key.Right => "right",
            Key.Home => "home", Key.End => "end", Key.PageUp => "page up", Key.PageDown => "page down",
            Key.Insert => "insert", Key.Delete => "delete", Key.OemTilde => "`",
            Key.Add => "numpad add", Key.Subtract => "numpad subtract", Key.Multiply => "numpad multiply",
            Key.Divide => "numpad divide", Key.Decimal => "numpad decimal",
            Key.OemMinus => "-", Key.OemPlus => "=", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
            Key.OemPipe or Key.OemBackslash => "\\", Key.OemSemicolon => ";", Key.OemQuotes => "'",
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/", _ => null
        };
    }
}
