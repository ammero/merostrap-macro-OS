using Merostrap.Macros.Health;
namespace Merostrap.Macros;

public sealed class ParryBarSettings
{
    public double ScalePercent { get; set; } = 100;
    public string Color { get; set; } = "#39BA68";
    public string? ImagePath { get; set; }
    public bool Enabled { get; set; }
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public HealthRegion? Region { get; set; }
    public ParryTrack? Track { get; set; }
}
