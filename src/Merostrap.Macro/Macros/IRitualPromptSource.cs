using Merostrap.Macros.Health;

namespace Merostrap.Macros;

public readonly record struct RitualGlyph(char? Character, int X, int Y, int Width, int Height, float Confidence, float InkBrightness = 255);
public sealed record RitualFrame(IReadOnlyList<RitualGlyph> Glyphs, bool Reliable = true, bool PromptAbsent = false);
public sealed record RitualPromptHighlight(HealthRegion Area, bool Sent);

/// <summary>Local recognition boundary; coordinates are relative to the selected prompt area.</summary>
public interface IRitualPromptSource : IDisposable
{
    (nint Window, int X, int Y, int Width, int Height, uint Dpi)? Viewport => null;
    bool DetectionConfirmsAbsence => false;
    HealthRegion? DetectRegion(CancellationToken token) => null;
    RitualFrame Read(HealthRegion region);
    bool IsPromptVisible(HealthRegion region)
    {
        var frame=Read(region);
        return frame.Reliable && !frame.PromptAbsent && frame.Glyphs.Count>0;
    }
}
