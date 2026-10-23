using Avalonia.Controls;
using Avalonia.Interactivity;
using Merostrap.Macros;
namespace Merostrap.UI.Elements.Macros;
public partial class FlowStateOverlayPanel : UserControl
{
    public FlowStateOverlayPanel() { InitializeComponent(); }
    private async void ColorClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FlowStateOverlayController model || TopLevel.GetTopLevel(this) is not Window owner) return;
        using var edit = (owner.DataContext as MacroWorkspace)?.PauseForSettingsEdit();
        var color = await new MacroColourDialog(model.BarColor).ShowPickerAsync(owner);
        if (color is not null) model.BarColor = color;
    }
}
