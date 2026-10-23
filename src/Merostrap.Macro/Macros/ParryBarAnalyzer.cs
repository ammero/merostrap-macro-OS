using Merostrap.Macros.Health;

namespace Merostrap.Macros;

public sealed record ParryTrack(int Left, int Right, int Row, int Width, int Height);

/// <summary>Reads the purple/cyan/green cooldown strip, never the posture fill below it.</summary>
public static class ParryBarAnalyzer
{
    public static ParryTrack? Calibrate(HealthPixels pixels)
    {
        if (!Valid(pixels)) return null;
        (int Left, int Right, int Count) best = (0, 0, 0);
        var row = 0;
        for (var y = 0; y < pixels.Height; y++)
        {
            var span = Span(pixels, y, 0, pixels.Width - 1);
            if (span.Count > best.Count && Gradient(pixels, y, span.Left, span.Right)) { best = span; row = y; }
        }
        if (best.Right - best.Left < pixels.Width * .9 || best.Count < (best.Right - best.Left) * .78) return null;
        return new(best.Left, best.Right, row, pixels.Width, pixels.Height);
    }

    public static double? Read(HealthPixels pixels, ParryTrack track)
    {
        if (!Valid(pixels) || pixels.Width != track.Width || pixels.Height != track.Height ||
            track.Left < 0 || track.Right >= pixels.Width || track.Right - track.Left < 25 ||
            track.Row < 0 || track.Row >= pixels.Height) return null;
        double? best = null;
        for (var y = Math.Max(0, track.Row - 1); y <= Math.Min(pixels.Height - 1, track.Row + 1); y++)
        {
            var span = Span(pixels, y, track.Left, track.Right);
            // A visible fragment ending against the opaque label cannot reveal
            // the true endpoint. Do not turn that fragment into a cooldown value.
            if (InsideSpectralLabel(span.Right, (track.Left + track.Right) / 2d, track.Right - track.Left + 1) &&
                HasSpectralGap(pixels, y, track.Left, track.Right)) continue;
            if (span.Left > track.Left + 3 || span.Right - span.Left < 6 ||
                span.Count < Math.Max(1, span.Right - span.Left + 1 - (track.Right - track.Left + 1) * .2) * .8 ||
                !Gradient(pixels, y, span.Left, span.Right)) continue;
            var percent = 100d * (span.Right - track.Left + 1) / (track.Right - track.Left + 1);
            best = Math.Max(best ?? 0, Math.Clamp(percent, 0, 100));
        }
        return best;
    }

    private static bool Valid(HealthPixels pixels) => pixels.Width is >= 30 and <= 4096 && pixels.Height is >= 4 and <= 300 &&
        pixels.Bgra.Length == (long)pixels.Width * pixels.Height * 4;
    public static bool IsSpectralCovered(HealthPixels pixels, ParryTrack track)
    {
        if (!Valid(pixels) || pixels.Width != track.Width || pixels.Height != track.Height ||
            track.Left < 0 || track.Right >= pixels.Width || track.Row < 0 || track.Row >= pixels.Height) return false;
        for (int y = Math.Max(0, track.Row - 1); y <= Math.Min(pixels.Height - 1, track.Row + 1); y++)
        {
            var span = Span(pixels, y, track.Left, track.Right);
            // Require the visible left strip as well as neutral label ink. A
            // missing HUD or unrelated centre text is not an occluded cooldown.
            if (span.Left >= track.Left && span.Left <= track.Left + 3 && span.Right - span.Left >= 6 &&
                Color(pixels, span.Left + Math.Max(1, (span.Right - span.Left) / 12), y) == 1 &&
                HasSpectralGap(pixels, y, track.Left, track.Right)) return true;
        }
        return false;
    }
    private static (int Left, int Right, int Count) Span(HealthPixels p, int y, int left, int right)
    {
        int first = -1, last = -1, count = 0, gap = 0;
        for (var x = left; x <= right; x++)
        {
            if (Color(p, x, y) != 0)
            {
                if (first < 0) first = x;
                last = x; count++; gap = 0;
            }
            else if (first >= 0)
            {
                gap++;
                // Spectral's neutral centre label covers the cooldown strip.
                // Bridge only that bounded central area; ordinary gaps elsewhere
                // still end the fill. Both gradient ends remain mandatory.
                if (gap > Math.Max(3, p.Width / 100) &&
                    !InsideSpectralLabel(x, (left + right) / 2d, right - left + 1)) break;
            }
        }
        return (first, last, count);
    }
    internal static bool InsideSpectralLabel(int x, double centre, double width) =>
        width >= 30 && Math.Abs(x - centre) <= width * .1;
    private static bool HasSpectralGap(HealthPixels p, int y, int left, int right)
    {
        int gap = 0, longest = 0, labelPixels = 0;
        for (int x = left; x <= right; x++)
        {
            if (!InsideSpectralLabel(x, (left + right) / 2d, right - left + 1)) continue;
            gap = Color(p, x, y) == 0 ? gap + 1 : 0;
            longest = Math.Max(longest, gap);
            int i = (y * p.Width + x) * 4;
            int b = p.Bgra[i], g = p.Bgra[i + 1], r = p.Bgra[i + 2];
            if (Math.Min(r, Math.Min(g, b)) >= 100 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 20)
                labelPixels++;
        }
        return longest > Math.Max(3, p.Width / 100) && labelPixels >= 2;
    }
    private static bool Gradient(HealthPixels p, int y, int left, int right)
    {
        if (left < 0 || right - left < 6) return false;
        // The game's gradient scales with the current fill. Require both ends so
        // a green overlay, health fill, or unrelated bright patch cannot read ready.
        var inset = Math.Max(1, (right - left) / 12);
        return Color(p, left + inset, y) == 1 && Color(p, right - inset, y) == 3;
    }
    private static int Color(HealthPixels p, int x, int y)
    {
        var i = (y * p.Width + x) * 4;
        int b = p.Bgra[i], g = p.Bgra[i + 1], r = p.Bgra[i + 2];
        if (r > g + 12 && b > g + 12) return 1;
        if (g > r + 12 && g > b + 8) return 3;
        if (b > r + 12 && b >= g - 12) return 2;
        return 0;
    }
}
