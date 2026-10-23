using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
namespace Merostrap.UI.Elements.Macros;
public sealed class ThresholdFill : Control
{
 public static readonly StyledProperty<Avalonia.Layout.Orientation> OrientationProperty=AvaloniaProperty.Register<ThresholdFill,Avalonia.Layout.Orientation>(nameof(Orientation));
 public Avalonia.Layout.Orientation Orientation {get=>GetValue(OrientationProperty);set=>SetValue(OrientationProperty,value);}
 public static readonly StyledProperty<double> ValueProperty=AvaloniaProperty.Register<ThresholdFill,double>(nameof(Value));
 public static readonly StyledProperty<IBrush?> FillProperty=AvaloniaProperty.Register<ThresholdFill,IBrush?>(nameof(Fill));
 public static readonly StyledProperty<double> MinimumProperty=AvaloniaProperty.Register<ThresholdFill,double>(nameof(Minimum));
 public static readonly StyledProperty<double> MaximumProperty=AvaloniaProperty.Register<ThresholdFill,double>(nameof(Maximum),100);
 public double Minimum {get=>GetValue(MinimumProperty);set=>SetValue(MinimumProperty,value);}
 public double Maximum {get=>GetValue(MaximumProperty);set=>SetValue(MaximumProperty,value);}
 public double Value {get=>GetValue(ValueProperty);set=>SetValue(ValueProperty,value);}
 public IBrush? Fill {get=>GetValue(FillProperty);set=>SetValue(FillProperty,value);}
 static ThresholdFill(){AffectsRender<ThresholdFill>(ValueProperty,FillProperty,MinimumProperty,MaximumProperty,OrientationProperty);}
 public override void Render(DrawingContext context)
 {
  if(Fill is null)return;
  var fraction=Math.Clamp((Value-Minimum)/Math.Max(1,Maximum-Minimum),0,1);
  var rect=Orientation==Avalonia.Layout.Orientation.Vertical
   ? new Rect(0,Bounds.Height*(1-fraction),Bounds.Width,Bounds.Height*fraction)
   : new Rect(0,0,Bounds.Width*fraction,Bounds.Height);
  context.FillRectangle(Fill,rect,4);
 }
}
