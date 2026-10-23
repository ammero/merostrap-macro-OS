using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Merostrap.Macros;
using Merostrap.Macros.Health;
using System.Runtime.InteropServices;

namespace Merostrap.UI.Elements.Macros;

public static class HudAutoDetection
{
    public static async Task<HealthRegion?> PickHealthAsync(Window owner) => (await PickAsync(owner, false))?.Region;
    public static async Task<DetectedParryBar?> PickParryAsync(Window owner)
    {
        var found=await PickAsync(owner,true);
        return found?.Track is { } track ? new(found.Region,track) : null;
    }
    public static async Task<DetectedDodgeIcon?> PickDodgeAsync(Window owner)
    {
        var found=await PickAsync(owner,false,true);
        return found?.DodgeTrack is {} track ? new(found.Region,track,1) : null;
    }
    private sealed record Detection(HealthRegion Region, ParryTrack? Track, DodgeIconTrack? DodgeTrack=null);
    private static async Task<Detection?> PickAsync(Window owner, bool parryOnly, bool dodgeOnly=false)
    {
        owner.Hide();
        try
        {
            WindowsHealthCapture.FocusRobloxForDetection();
            await Task.Delay(100);
            var scan = await Task.Run(async () =>
            {
                using var labelReader=parryOnly || dodgeOnly ? null : new HealthTextReader();
                var scanTime=System.Diagnostics.Stopwatch.StartNew();
                for (int attempt = 0; attempt < 40 && scanTime.Elapsed<TimeSpan.FromSeconds(10); attempt++)
                {
                    try
                    {
                    if (WindowsHealthCapture.ForegroundRobloxArea() is { } area)
                    {
                        var capture = new WindowsHealthCapture();
                        var pixels = new HealthPixels(area.Width, area.Height, new byte[area.Width * area.Height * 4]);
                        int step = (area.Height + 1) / 2;
                        for (int y = 0; y < area.Height; y += step)
                        {
                            var part = capture.Capture(new(area.X, area.Y + y, area.Width, Math.Min(step, area.Height - y)));
                            Array.Copy(part.Bgra, 0, pixels.Bgra, y * area.Width * 4, part.Bgra.Length);
                        }
                        Detection? found;
                        if(dodgeOnly) {
                            var dodge=DodgeIconDetector.DetectInHud(pixels);
                            found=dodge is null?null:new(dodge.Region,null,dodge.Track);
                        } else found = parryOnly
                            ? HudBarDetector.DetectParry(pixels) is { } parry ? new(parry.Parry,parry.Track) : null
                            : HudBarDetector.DetectHealthInHud(pixels,labelReader!.FindHealthLabel,labelReader!.ValidateDetectedBar) is { } health ? new(health,null) : null;
                        if (WindowsHealthCapture.ForegroundRobloxArea() == area && found is not null)
                            return (Area: area, Pixels: pixels, Found: found);
                    }
                    else if (attempt < 4) WindowsHealthCapture.FocusRobloxForDetection();
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException)
                    {
                        // A display/window transition can invalidate one capture. Retry
                        // without discarding the previously saved calibration.
                    }
                    await Task.Delay(250);
                }
                return (Area: (HealthRegion?)null, Pixels: (HealthPixels?)null, Found: (Detection?)null);
            });
            owner.Show(); owner.Activate();
            if (scan.Found is not { } bars || scan.Pixels is null || scan.Area is null)
            {
                await ShowMissing(owner,parryOnly,dodgeOnly);
                return null;
            }
            using var preview = Bitmap(HudBarDetector.Crop(scan.Pixels, bars.Region));
            var label=dodgeOnly ? "Dodge" : parryOnly ? "Parry" : "Health";
            var dialog = new HudDetectionDialog { Title = "Detected " + label.ToLowerInvariant() + (dodgeOnly ? " icon" : " bar"), Width = 680, SizeToContent = SizeToContent.Height, CanResize = false };
            var use = new Button { Content = dodgeOnly ? "Use this icon" : "Use this bar" };
            var cancel = new Button { Content = "Cancel" };
            use.Click += (_, _) => dialog.Close(true);
            cancel.Click += (_, _) => dialog.Close(false);
            dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children =
            {
                new TextBlock { Text = dodgeOnly ? "Dodge icon" : parryOnly ? "Parry - confirm the cooldown is full" : "Health" },
                new Image { Source = preview, Height = dodgeOnly ? 64 : parryOnly ? 20 : 44, Stretch = Stretch.Uniform },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { use, cancel } }
            }};
            if (!await dialog.ShowDialog<bool>(owner)) return null;
            HealthRegion Absolute(HealthRegion r) => r with { X = r.X + scan.Area.X, Y = r.Y + scan.Area.Y };
            return bars with { Region = Absolute(bars.Region) };
        }
        finally { owner.Show(); owner.Activate(); }
    }
    private static WriteableBitmap Bitmap(HealthPixels pixels)
    {
        var bitmap = new WriteableBitmap(new PixelSize(pixels.Width, pixels.Height), new Vector(96,96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
        for (int y = 0; y < pixels.Height; y++) Marshal.Copy(pixels.Bgra, y * pixels.Width * 4, buffer.Address + y * buffer.RowBytes, pixels.Width * 4);
        return bitmap;
    }
    private static async Task ShowMissing(Window owner, bool parryOnly, bool dodgeOnly=false)
    {
        var dialog = new HudDetectionDialog { Title = "Auto-detect", Width = 430, SizeToContent = SizeToContent.Height, CanResize = false };
        var close = new Button { Content = "OK" }; close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = { new TextBlock { Text = dodgeOnly ? "Couldn't find the dodge icon. Keep the HUD visible and wait until dodge is ready, then retry or select it manually." : parryOnly ? "Couldn't find the parry bar. Keep your HUD visible and wait until parry is ready, then retry or select it manually." : "Couldn't find the health bar. Keep your HUD visible, then retry or select the health bar manually.", TextWrapping = TextWrapping.Wrap }, close } };
        await dialog.ShowDialog(owner);
    }
}


