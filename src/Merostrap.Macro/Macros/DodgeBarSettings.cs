using Merostrap.Macros.Health;
namespace Merostrap.Macros;

public sealed class DodgeBarSettings
{
    public double CooldownSeconds { get; set; } = 1.75;
    public double ScalePercent { get; set; } = 100;
    public string Color { get; set; } = "#69B9E8";
    public string? ImagePath { get; set; }
    public bool Enabled { get; set; }
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public HealthRegion? Region { get; set; }
    public DodgeIconTrack? Track { get; set; }
}
