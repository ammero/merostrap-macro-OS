using Merostrap.Macros.Health;

namespace Merostrap.Macros;

/// <summary>Finds the ready HUD in foreground Roblox without moving focus or opening UI.</summary>
public static class HudBarCapture
{
    private static readonly Lazy<HealthTextReader> HealthLabel = new(() => new());
    public static bool SameLocation(DetectedHudBars bars, DetectedHudBars? previous) => previous is not null &&
        Math.Abs((long)bars.Parry.X - previous.Parry.X) <= 3 &&
        Math.Abs((long)bars.Parry.Y - previous.Parry.Y) <= 3 &&
        Math.Abs((long)bars.Parry.Width - previous.Parry.Width) <= 4 &&
        bars.Parry.Height == previous.Parry.Height;
    public static bool SameLocation(HealthRegion region, HealthRegion? previous) => previous is not null &&
        Math.Abs((long)region.X-previous.X)<=3 && Math.Abs((long)region.Y-previous.Y)<=3 &&
        Math.Abs((long)region.Width-previous.Width)<=4 && Math.Abs((long)region.Height-previous.Height)<=2;
    public static bool SameLocation(DetectedParryBar bar, DetectedParryBar? previous) => previous is not null && SameLocation(bar.Parry,previous.Parry);
    public static HealthRegion? DetectHealth()
    {
        var scan=Capture();
        return scan is { } s && HudBarDetector.DetectHealthInHud(s.Pixels,HealthLabel.Value.FindHealthLabel,HealthLabel.Value.ValidateDetectedBar) is { } health ? Absolute(health,s.Area) : null;
    }
    public static DetectedDodgeIcon? DetectDodge()
    {
        var scan=Capture();
        if(scan is not { } s)return null;
        var found=DodgeIconDetector.DetectInHud(s.Pixels);
        // Detection can take longer than capture; reject a window/focus change during the scan.
        return found is not null && WindowsHealthCapture.ForegroundRobloxArea()==s.Area
            ? found with {Region=Absolute(found.Region,s.Area)} : null;
    }
    public static DetectedParryBar? DetectParry()
    {
        var scan=Capture();
        return scan is { } s && HudBarDetector.DetectParry(s.Pixels) is { } parry ? parry with {Parry=Absolute(parry.Parry,s.Area)} : null;
    }
    public static DetectedHudBars? Detect()
    {
        var scan=Capture();
        return scan is { } s && HudBarDetector.Detect(s.Pixels) is { } found ? found with {Health=Absolute(found.Health,s.Area),Parry=Absolute(found.Parry,s.Area)} : null;
    }
    private static HealthRegion Absolute(HealthRegion r,HealthRegion area)=>r with {X=r.X+area.X,Y=r.Y+area.Y};
    private static (HealthRegion Area,HealthPixels Pixels)? Capture()
    {
        if (WindowsHealthCapture.ForegroundRobloxArea() is not { } area) return null;
        var capture = new WindowsHealthCapture();
        var pixels = new HealthPixels(area.Width, area.Height, new byte[area.Width * area.Height * 4]);
        int step = (area.Height + 1) / 2;
        for (int y = 0; y < area.Height; y += step)
        {
            var part = capture.Capture(new(area.X, area.Y + y, area.Width, Math.Min(step, area.Height - y)));
            Array.Copy(part.Bgra, 0, pixels.Bgra, y * area.Width * 4, part.Bgra.Length);
        }
        return WindowsHealthCapture.ForegroundRobloxArea()==area ? (area,pixels) : null;
    }
}

