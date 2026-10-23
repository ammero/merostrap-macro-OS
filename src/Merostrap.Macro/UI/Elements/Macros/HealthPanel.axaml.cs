using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Merostrap.Macros;

namespace Merostrap.UI.Elements.Macros;

public partial class HealthPanel : UserControl
{
    public HealthPanel()
    {
        InitializeComponent();
        DeafenBar.AddHandler(PointerPressedEvent, BarPressed, RoutingStrategies.Tunnel);
        DeafenBar.AddHandler(PointerMovedEvent, BarMoved, RoutingStrategies.Tunnel);
        DeafenBar.AddHandler(PointerReleasedEvent, BarReleased, RoutingStrategies.Tunnel);
    }
    private void SetBarPosition(PointerEventArgs e)
    {
        var percent=(int)Math.Round(Math.Clamp(e.GetPosition(DeafenBar).X / DeafenBar.Bounds.Width, 0, 1)*100);
        if(DataContext is HealthAutomation health) health.DeafenPercent=Math.Clamp(percent,1,100);
    }
    private void BarPressed(object? sender,PointerPressedEventArgs e)
    {
        if(!e.GetCurrentPoint(DeafenBar).Properties.IsLeftButtonPressed)return;
        DeafenBar.Focus();e.Pointer.Capture(DeafenBar);SetBarPosition(e);e.Handled=true;
    }
    private void BarMoved(object? sender,PointerEventArgs e)
    {
        if(e.Pointer.Captured!=DeafenBar)return;SetBarPosition(e);e.Handled=true;
    }
    private void BarReleased(object? sender,PointerReleasedEventArgs e)
    {
        if(e.Pointer.Captured!=DeafenBar)return;SetBarPosition(e);e.Pointer.Capture(null);e.Handled=true;
    }
    private async void AutoDetectClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HealthAutomation health && TopLevel.GetTopLevel(this) is Window owner)
            await health.SelectRegionAsync(owner, true);
    }
    private async void SelectRegionClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HealthAutomation health && TopLevel.GetTopLevel(this) is Window owner)
            await health.SelectRegionAsync(owner);
    }
}
