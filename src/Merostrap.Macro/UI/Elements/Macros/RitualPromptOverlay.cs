using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Merostrap.Macros;

namespace Merostrap.UI.Elements.Macros;

/// <summary>A click-through outline, excluded from the pixels used for recognition.</summary>
public sealed class RitualPromptOverlay : Window
{
    private readonly Border _outline = new() { BorderThickness=new Thickness(2), CornerRadius=new CornerRadius(4), Background=Brushes.Transparent };
    public RitualPromptOverlay()
    {
        WindowDecorations=WindowDecorations.None;CanResize=false;ShowActivated=false;ShowInTaskbar=false;Topmost=true;
        Background=Brushes.Transparent;TransparencyBackgroundFallback=Brushes.Transparent;
        TransparencyLevelHint=[WindowTransparencyLevel.Transparent];Content=_outline;
        Win32Properties.AddWindowStylesCallback(this,(style,extended)=>((style & ~0x00CF0000u)|0x80000000u,extended|0x080000A0u));
        OverlayCapturePrivacy.Attach(this);
    }
    public void Update(RitualPromptHighlight prompt)
    {
        var area=prompt.Area;
        if(area.Width is <1 or >512 || area.Height is <1 or >128) { Hide();return; }
        var point=new PixelPoint(area.X-6,area.Y-6);
        var scale=Screens.ScreenFromPoint(point)?.Scaling ?? 1;
        Width=(area.Width+12)/scale;Height=(area.Height+12)/scale;
        _outline.BorderBrush=new SolidColorBrush(Color.Parse(prompt.Sent ? "#80DBAE" : "#FF5D66"));
        Position=point;
        Show();Position=point;
    }
}
