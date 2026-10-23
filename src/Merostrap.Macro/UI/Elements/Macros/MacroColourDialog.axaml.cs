using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Threading;
using System.Runtime.InteropServices;
namespace Merostrap.UI.Elements.Macros;
public partial class MacroColourDialog : Window
{
    bool updating;
    bool editingHex;
    public MacroColourDialog(){InitializeComponent();
        OverlayCapturePrivacy.Attach(this);
        Win32Properties.AddWindowStylesCallback(this,(style,extended)=>(style,extended|0x08000080u));
        Win32Properties.AddWndProcHookCallback(this,(nint h,uint message,nint w,nint l,ref bool handled)=>{
            if(message==0x0021 && !editingHex){handled=true;return new nint(3);}return nint.Zero;
        });
        AddHandler(PointerPressedEvent,(_,e)=>{
            bool hex=e.Source is Visual visual && (visual==Hex || visual.GetVisualAncestors().Contains(Hex));
            SetHexEditing(hex);
        },RoutingStrategies.Tunnel,true);
        Hex.LostFocus+=(_,_)=>Dispatcher.UIThread.Post(()=>SetHexEditing(false));
        Hex.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;Apply.Focus();SetHexEditing(false);}};
        Deactivated+=(_,_)=>{editingHex=false;ConfigureActivation();};
        Opened+=(_,_)=>{
            var screen=Screens.ScreenFromPoint(Position)??Screens.Primary;
            if(screen is not null){var area=screen.WorkingArea;Position=new PixelPoint(Math.Clamp(Position.X,area.X,Math.Max(area.X,area.Right-(int)Math.Ceiling(Width*screen.Scaling))),Math.Clamp(Position.Y,area.Y,Math.Max(area.Y,area.Bottom-(int)Math.Ceiling(Height*screen.Scaling))));}
            ConfigureActivation();
        };
        Plane.Changed+=Sync;HueStrip.Changed+=()=>{Plane.Hue=HueStrip.Hue;Plane.InvalidateVisual();Sync();};SetColour(Colors.White);
    }
    public MacroColourDialog(string hex):this(){if(Color.TryParse(hex,out var c))SetColour(c);}
    public async Task<string?> ShowPickerAsync(Window owner)
    {
        try {return await ShowDialog<string?>(owner);}
        finally {if(owner is MacroWindow macro)macro.RestoreGameFocus();}
    }
    void SetHexEditing(bool value)
    {
        if(editingHex==value)return;editingHex=value;ConfigureActivation();
        if(value)Activate();else if(Owner is MacroWindow macro)macro.RestoreGameFocus();
    }
    void ConfigureActivation()
    {
        if(!OperatingSystem.IsWindows() || TryGetPlatformHandle()?.Handle is not {} handle || handle==nint.Zero)return;
        var extended=GetWindowLong(handle,-20);
        SetWindowLong(handle,-20,editingHex?extended&~0x08000000:extended|0x08000080);
    }
    [DllImport("user32.dll")]static extern int GetWindowLong(nint window,int index);
    [DllImport("user32.dll")]static extern int SetWindowLong(nint window,int index,int value);
    void SetColour(Color c){double r=c.R/255d,g=c.G/255d,b=c.B/255d,max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),d=max-min;double h=d==0?Plane.Hue:max==r?60*((g-b)/d%6):max==g?60*((b-r)/d+2):60*((r-g)/d+4);Plane.Hue=HueStrip.Hue=(h+360)%360;Plane.Saturation=max==0?0:d/max;Plane.Value=max;Plane.InvalidateVisual();HueStrip.InvalidateVisual();Sync();}
    void Sync(){var c=MacroColourPlane.FromHsv(Plane.Hue,Plane.Saturation,Plane.Value);updating=true;Hex.Text=$"#{c.R:X2}{c.G:X2}{c.B:X2}";updating=false;Preview.Background=new SolidColorBrush(c);Error.IsVisible=false;Apply.IsEnabled=true;}
    void HexChanged(object? sender,TextChangedEventArgs e){if(updating||Plane==null||Apply==null)return;var text=Hex.Text??"";bool valid=text.Length==7&&text[0]=='#'&&Color.TryParse(text,out _);Error.IsVisible=!valid;Apply.IsEnabled=valid;if(valid)SetColour(Color.Parse(text));}
    void DragPressed(object? sender,PointerPressedEventArgs e){if(e.Source is not Button&&e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)BeginMoveDrag(e);}
    void CancelClicked(object? sender,RoutedEventArgs e)=>Close(null);
    void ApplyClicked(object? sender,RoutedEventArgs e){if(Apply.IsEnabled)Close(Hex.Text);}
    protected override void OnKeyDown(KeyEventArgs e){base.OnKeyDown(e);if(e.Key==Key.Escape){Close(null);e.Handled=true;}}
}
