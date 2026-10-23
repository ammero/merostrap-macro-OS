using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Merostrap.Macros;
using Merostrap.Macros.Health;
using System.Runtime.InteropServices;

namespace Merostrap.UI.Elements.Macros;

public static class RitualAutoDetection
{
    public static async Task<HealthRegion?> PickAsync(Window owner)
    {
        owner.Hide();
        try
        {
            WindowsHealthCapture.FocusRobloxForDetection();
            await Task.Delay(350);
            var scan = await Task.Run(() =>
            {
                var window = WindowsHealthCapture.ForegroundRobloxWindow();
                if (WindowsHealthCapture.ForegroundRobloxArea(true) is not { } area) return (Area: (HealthRegion?)null, Pixels: (HealthPixels?)null, Region: (HealthRegion?)null);
                var capture = new WindowsHealthCapture();
                var pixels = new HealthPixels(area.Width, area.Height, new byte[area.Width*area.Height*4]);
                for(int y=0;y<area.Height;y+=300)
                {
                    if (window == nint.Zero || WindowsHealthCapture.ForegroundRobloxWindow() != window || WindowsHealthCapture.ForegroundRobloxArea(true) != area) return (Area: (HealthRegion?)null, Pixels: (HealthPixels?)null, Region: (HealthRegion?)null);
                    var part = capture.Capture(new(area.X,area.Y+y,area.Width,Math.Min(300,area.Height-y)));
                    Array.Copy(part.Bgra,0,pixels.Bgra,y*area.Width*4,part.Bgra.Length);
                }
                var found = RitualPromptDetector.Detect(pixels);
                return WindowsHealthCapture.ForegroundRobloxWindow()==window && WindowsHealthCapture.ForegroundRobloxArea(true)==area ? (Area: area, Pixels: pixels, Region: found) : (Area: (HealthRegion?)null, Pixels: (HealthPixels?)null, Region: (HealthRegion?)null);
            });
            owner.Show(); owner.Activate();
            var dialog = new Window { Title = "Ritual prompt", Width = 520, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            OverlayCapturePrivacy.Attach(dialog);
            if(scan.Region is not { } region || scan.Pixels is null || scan.Area is null)
            {
                var close = new Button { Content = "OK" }; close.Click += (_,_)=>dialog.Close();
                dialog.Content=new StackPanel { Margin=new Thickness(20), Spacing=16, Children={new TextBlock { Text="Couldn't identify the outlined prompt frame. Show the frame with its letters or numbers visible in Roblox and retry, or use Select area. Your saved area has been kept.", TextWrapping=TextWrapping.Wrap },close}};
                await dialog.ShowDialog(owner); return null;
            }
            using var bitmap = Bitmap(RitualPromptDetector.HudCrop(scan.Pixels,region));
            var use = new Button { Content="Use this area" }; var cancel = new Button { Content="Cancel" };
            use.Click+=(_,_)=>dialog.Close(true); cancel.Click+=(_,_)=>dialog.Close(false);
            dialog.Content=new StackPanel { Margin=new Thickness(20),Spacing=16,Children={new TextBlock { Text="Confirm this area covers the outlined prompt frame and both rows, excluding the title.",TextWrapping=TextWrapping.Wrap },new Image { Source=bitmap, Height=120,Stretch=Stretch.Uniform },new StackPanel { Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=8,Children={use,cancel}}}};
            if(!await dialog.ShowDialog<bool>(owner)) return null;
            return region with { X=region.X+scan.Area.X, Y=region.Y+scan.Area.Y };
        }
        finally { owner.Show();owner.Activate(); }
    }
    private static WriteableBitmap Bitmap(HealthPixels pixels)
    {
        var bitmap=new WriteableBitmap(new PixelSize(pixels.Width,pixels.Height),new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Opaque);
        using var buffer=bitmap.Lock();
        for(int y=0;y<pixels.Height;y++) Marshal.Copy(pixels.Bgra,y*pixels.Width*4,buffer.Address+y*buffer.RowBytes,pixels.Width*4);
        return bitmap;
    }
}
