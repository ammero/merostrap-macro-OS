using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Merostrap.Macros;
namespace Merostrap.UI.Elements.Macros;
public partial class ParryBarPanel : UserControl
{
    public ParryBarPanel()
    {
        InitializeComponent();
        SizeBar.AddHandler(PointerPressedEvent,SizePressed,RoutingStrategies.Tunnel);
        SizeBar.AddHandler(PointerMovedEvent,SizeMoved,RoutingStrategies.Tunnel);
        SizeBar.AddHandler(PointerReleasedEvent,SizeReleased,RoutingStrategies.Tunnel);
    }
    private void SetSize(PointerEventArgs e){if(DataContext is ParryBarController model)model.ScalePercent=50+250*Math.Clamp(e.GetPosition(SizeBar).X/SizeBar.Bounds.Width,0,1);}
    private void SizePressed(object? sender,PointerPressedEventArgs e){if(!e.GetCurrentPoint(SizeBar).Properties.IsLeftButtonPressed)return;SizeBar.Focus();e.Pointer.Capture(SizeBar);SetSize(e);e.Handled=true;}
    private void SizeMoved(object? sender,PointerEventArgs e){if(e.Pointer.Captured!=SizeBar)return;SetSize(e);e.Handled=true;}
    private void SizeReleased(object? sender,PointerReleasedEventArgs e){if(e.Pointer.Captured!=SizeBar)return;SetSize(e);e.Pointer.Capture(null);e.Handled=true;}

    private async void AutoDetectClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ParryBarController model && TopLevel.GetTopLevel(this) is Window owner)
            await model.SelectRegionAsync(owner, true);
    }
    private async void ImageClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ParryBarController model || TopLevel.GetTopLevel(this) is not Window owner) return;
        ImageError.IsVisible = false;
        try
        {
            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose parry bar image", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Images and GIFs") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif"] }]
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) model.SetImage(path);
        }
        catch (Exception) { ImageError.Text = "Could not open this image. Try a PNG or JPG."; ImageError.IsVisible = true; }
    }
    private void RemoveImageClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ParryBarController model) model.ClearImage();
        ImageError.IsVisible = false;
    }
    private async void ColorClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ParryBarController model && TopLevel.GetTopLevel(this) is Window owner)
        {
            using var edit = (owner.DataContext as MacroWorkspace)?.PauseForSettingsEdit();
            var dialog = new MacroColourDialog(model.BarColor);
            var color = await dialog.ShowPickerAsync(owner);
            if (color is not null) model.BarColor = color;
        }
    }
    private async void SelectClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ParryBarController model && TopLevel.GetTopLevel(this) is Window owner)
            await model.SelectRegionAsync(owner);
    }
}
