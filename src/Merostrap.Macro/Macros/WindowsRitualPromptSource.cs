using Merostrap.Macros.Health;
using System.Runtime.InteropServices;
namespace Merostrap.Macros;
public sealed class WindowsRitualPromptSource : IRitualPromptSource
{
    private readonly WindowsHealthCapture _capture=new();
    private readonly RitualPromptReader _reader=new();
    private bool _disposed;
    public bool DetectionConfirmsAbsence { get; private set; }
    public (nint Window, int X, int Y, int Width, int Height, uint Dpi)? Viewport
    {
        get
        {
            if(_disposed || !OperatingSystem.IsWindows())return null;
            var window=WindowsHealthCapture.ForegroundRobloxWindow();
            if(window==nint.Zero || !GetClientRect(window,out var rect))return null;
            var point=new NativePoint();
            if(!ClientToScreen(window,ref point) || rect.Right<160 || rect.Bottom<80)return null;
            uint dpi=GetDpiForWindow(window);
            return dpi>0 && WindowsHealthCapture.ForegroundRobloxWindow()==window
                ? (window,point.X,point.Y,rect.Right,rect.Bottom,dpi) : null;
        }
    }
    public HealthRegion? DetectRegion(CancellationToken token)
    {
        DetectionConfirmsAbsence=false;
        token.ThrowIfCancellationRequested();
        if (_disposed || !OperatingSystem.IsWindows()) return null;
        var viewport = Viewport;
        if(viewport is not { } client)return null;
        int width=Math.Min(4096,client.Width),height=Math.Min(2160,client.Height/2);
        var area=new HealthRegion(client.X+(client.Width-width)/2,client.Y,width,height);
        var pixels = new HealthPixels(area.Width, area.Height, new byte[checked(area.Width * area.Height * 4)]);
        for (int y = 0; y < area.Height; y += 300)
        {
            token.ThrowIfCancellationRequested();
            if (Viewport != viewport) return null;
            var part = _capture.Capture(new(area.X, area.Y + y, area.Width, Math.Min(300, area.Height - y)));
            Array.Copy(part.Bgra, 0, pixels.Bgra, y * area.Width * 4, part.Bgra.Length);
        }
        token.ThrowIfCancellationRequested();
        var outline=RitualPromptDetector.ReadOutline(pixels);
        var found = outline==RitualPromptDetector.OutlineState.Visible ? RitualPromptDetector.Detect(pixels) : null;
        token.ThrowIfCancellationRequested();
        if (Viewport != viewport) return null;
        DetectionConfirmsAbsence=outline==RitualPromptDetector.OutlineState.Absent;
        return found is null ? null : found with { X = found.X + area.X, Y = found.Y + area.Y };
    }
    public RitualFrame Read(HealthRegion region)
    {
        if(_disposed || !OperatingSystem.IsWindows() || !WindowsHealthCapture.IsForegroundRoblox())return new([],false);
        var viewport=Viewport;
        if(viewport is null)return new([],false);
        try {
            HealthPixels pixels;
            HealthPixels? outlinePixels = null;
            if (region.Width <= 4084 && region.Height <= 288)
            {
                try {
                    // Read the glyphs and their outline from one captured frame.
                    outlinePixels = _capture.Capture(new(region.X-6,region.Y-6,region.Width+12,region.Height+12));
                    pixels = RitualPromptDetector.HudCrop(outlinePixels, new(6,6,region.Width,region.Height));
                } catch { outlinePixels = null; pixels = _capture.Capture(region); }
            }
            else pixels = _capture.Capture(region);
            if(Viewport!=viewport)return new([],false);
            var outlined=RitualPromptDetector.OutlineState.Uncertain;
            // An unreadable glyph is not proof of a new cast. Reset only after the
            // previously observed outlined prompt frame has actually disappeared.
            if(outlinePixels is not null) {
                try {
                    outlined=RitualPromptDetector.ReadOutline(outlinePixels,new(6,6,region.Width,region.Height));
                } catch { outlined=RitualPromptDetector.OutlineState.Uncertain; }
            }
            // Validate the cheap outline first. Scenery must neither authorize
            // input nor pay for several OCR recovery passes on every poll.
            var frame=RitualPromptDetector.GateRecognition(()=>_reader.Analyze(pixels),outlined);
            return Viewport==viewport?frame:new([],false);
        }
        catch { return new([],false); }
    }
    public bool IsPromptVisible(HealthRegion region)
    {
        if(_disposed || !OperatingSystem.IsWindows())return false;
        var viewport=Viewport;
        if(viewport is null)return false;
        try
        {
            var pixels=_capture.Capture(new(region.X-6,region.Y-6,region.Width+12,region.Height+12));
            return RitualPromptDetector.ReadOutline(pixels,new(6,6,region.Width,region.Height))==RitualPromptDetector.OutlineState.Visible &&
                Viewport==viewport;
        }
        catch {return false;}
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetClientRect(nint window,out NativeRect rect);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool ClientToScreen(nint window,ref NativePoint point);
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint window);
    public void Dispose(){_disposed=true;_reader.Dispose();}
}
