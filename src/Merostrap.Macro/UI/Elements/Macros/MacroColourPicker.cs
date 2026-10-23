using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GradientStop = Avalonia.Media.GradientStop;
using GradientStops = Avalonia.Media.GradientStops;
namespace Merostrap.UI.Elements.Macros;
public sealed class MacroColourPlane : Control
{
    public double Hue {get;set;}
    public double Saturation {get;set;}=1;
    public double Value {get;set;}=1;
    public bool IsHue {get;set;}
    public event Action? Changed;
    public MacroColourPlane(){Focusable=true;Cursor=new Cursor(StandardCursorType.Cross);}
    public static Color FromHsv(double h,double s,double v)
    {
        double c=v*s,x=c*(1-Math.Abs((h/60)%2-1)),m=v-c;
        var (r,g,b)=h switch {<60=>(c,x,0d),<120=>(x,c,0d),<180=>(0d,c,x),<240=>(0d,x,c),<300=>(x,0d,c),_=>(c,0d,x)};
        return Color.FromRgb((byte)Math.Round((r+m)*255),(byte)Math.Round((g+m)*255),(byte)Math.Round((b+m)*255));
    }
    public override void Render(DrawingContext context)
    {
        var rect=new Rect(Bounds.Size);
        var brush=new LinearGradientBrush{StartPoint=new RelativePoint(0,0,RelativeUnit.Relative),EndPoint=new RelativePoint(1,0,RelativeUnit.Relative)};
        if(IsHue){for(int i=0;i<=6;i++)brush.GradientStops.Add(new GradientStop(FromHsv(i*60%360,1,1),i/6d));context.FillRectangle(brush,rect,5);var p=new Point(Math.Clamp(Hue/360*Bounds.Width,6,Bounds.Width-6),Bounds.Height/2);context.DrawEllipse(Brushes.White,new Pen(Brushes.Black,2),p,5,5);}
        else {
            brush.GradientStops.Add(new GradientStop(Colors.White,0));brush.GradientStops.Add(new GradientStop(FromHsv(Hue,1,1),1));
            context.FillRectangle(brush,rect,7);
            var shade=new LinearGradientBrush{StartPoint=new RelativePoint(0,0,RelativeUnit.Relative),EndPoint=new RelativePoint(0,1,RelativeUnit.Relative),GradientStops=new GradientStops{new(Colors.Transparent,0),new(Colors.Black,1)}};
            context.FillRectangle(shade,rect,7);
            var p=new Point(Math.Clamp(Saturation*Bounds.Width,6,Bounds.Width-6),Math.Clamp((1-Value)*Bounds.Height,6,Bounds.Height-6));context.DrawEllipse(null,new Pen(Brushes.Black,4),p,5,5);context.DrawEllipse(null,new Pen(Brushes.White,2),p,5,5);
        }
    }
    void Update(Point p){if(IsHue)Hue=Math.Clamp(p.X/Bounds.Width,0,.999999)*360;else{Saturation=Math.Clamp(p.X/Bounds.Width,0,1);Value=1-Math.Clamp(p.Y/Bounds.Height,0,1);}InvalidateVisual();Changed?.Invoke();}
    protected override void OnPointerPressed(PointerPressedEventArgs e){base.OnPointerPressed(e);if(!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)return;Focus();e.Pointer.Capture(this);Update(e.GetPosition(this));e.Handled=true;}
    protected override void OnPointerMoved(PointerEventArgs e){base.OnPointerMoved(e);if(e.Pointer.Captured==this)Update(e.GetPosition(this));}
    protected override void OnPointerReleased(PointerReleasedEventArgs e){base.OnPointerReleased(e);e.Pointer.Capture(null);}
    protected override void OnKeyDown(KeyEventArgs e){double step=e.KeyModifiers.HasFlag(KeyModifiers.Shift)?.1:.01;if(e.Key is not(Key.Left or Key.Right or Key.Up or Key.Down))return;if(IsHue)Hue=(Hue+(e.Key is Key.Left or Key.Down?-1:1)*step*360+360)%360;else if(e.Key is Key.Left or Key.Right)Saturation=Math.Clamp(Saturation+(e.Key==Key.Left?-step:step),0,1);else Value=Math.Clamp(Value+(e.Key==Key.Down?-step:step),0,1);InvalidateVisual();Changed?.Invoke();e.Handled=true;}
}
