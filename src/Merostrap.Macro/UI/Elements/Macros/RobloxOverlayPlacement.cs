using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Merostrap.Macros;

namespace Merostrap.UI.Elements.Macros;

/// <summary>Moves floating overlays with the game display while preserving their local offset.</summary>
public static class RobloxOverlayPlacement
{
    public static PixelPoint Compute(PixelPoint desired,PixelRect source,double sourceScale,PixelRect target,double targetScale,double width,double height)
    {
        var x=target.X+(desired.X-source.X)/sourceScale*targetScale;
        var y=target.Y+(desired.Y-source.Y)/sourceScale*targetScale;
        return new((int)Math.Clamp(Math.Round(x),target.X,Math.Max(target.X,target.Right-Math.Ceiling(width*targetScale))),
            (int)Math.Clamp(Math.Round(y),target.Y,Math.Max(target.Y,target.Bottom-Math.Ceiling(height*targetScale))));
    }
    public static PixelPoint Clamp(Window window,PixelPoint desired)
    {
        var target=Target(window);
        if(target is null)return desired;
        var source=window.Screens.ScreenFromPoint(desired)??target;
        return Compute(desired,source.WorkingArea,source.Scaling,target.WorkingArea,target.Scaling,
            double.IsFinite(window.Width)?window.Width:window.Bounds.Width,Math.Max(window.Bounds.Height,40));
    }
    private static Avalonia.Platform.Screen? Target(Window window)
    {
        if(RobloxDisplay.TargetBounds is { } bounds)
            return window.Screens.ScreenFromPoint(new(bounds.X+bounds.Width/2,bounds.Y+bounds.Height/2));
        return null;
    }
    public static void Attach(Window window,bool topRight=false)
    {
        OverlayCapturePrivacy.Attach(window);
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
        void Place()
        {
            if(!window.IsVisible)return;
            var target=Target(window);if(target is null)return;
            var desired=topRight ? new PixelPoint(target.WorkingArea.Right-(int)Math.Ceiling(window.Width*target.Scaling)-16,target.WorkingArea.Y+16) : Clamp(window,window.Position);
            if(desired!=window.Position)window.Position=desired;
        }
        timer.Tick+=(_,_)=>Place();
        window.Opened+=(_,_)=>{Place();timer.Start();};
        window.PropertyChanged+=(_,e)=>
        {
            if(e.Property!=Visual.IsVisibleProperty)return;
            if(window.IsVisible){Place();timer.Start();}else timer.Stop();
        };
        window.Closed+=(_,_)=>timer.Stop();
    }
}
