// SPDX-License-Identifier: MPL-2.0
using System;
using System.Globalization;
using System.Linq;

namespace Merostrap.Macros.Health;
/// <summary>Reads a calibrated health fill interior; the region must span the full available health width.</summary>
public static class HealthBarAnalyzer
{
    /// <summary>Reads a framed whole-bar selection at any fill level.</summary>
    public static HealthSample AnalyzeSelection(HealthPixels pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Width is < 30 or > 4096 || pixels.Height is < 4 or > 300 || pixels.Bgra is null ||
            pixels.Bgra.LongLength != (long)pixels.Width * pixels.Height * 4)
            return new(null, "Invalid health image.");
        var live = LiveFill(pixels);
        var counts = new int[pixels.Height];
        for (var y = 0; y < pixels.Height; y++)
            for (var x = 0; x < pixels.Width; x++)
            {
                var i = (y * pixels.Width + x) * 4;
                if (live(pixels.Bgra[i + 2], pixels.Bgra[i + 1], pixels.Bgra[i]) || IsWither(pixels.Bgra[i + 2], pixels.Bgra[i + 1], pixels.Bgra[i])) counts[y]++;
            }
        AddTemporaryEvidence(pixels,counts,live);
        var maximum = counts.Max();
        if (maximum < 3) return new(null, "Health fill not detected.");
        var rows = SupportedRows(counts);
        if (rows.Length < 4) return new(null, "Health fill is unclear.");
        var left = -1;
        for (var x = 0; x < pixels.Width; x++)
        {
            var votes = 0;
            foreach (var y in rows)
            {
                var i = (y * pixels.Width + x) * 4;
                if (live(pixels.Bgra[i + 2], pixels.Bgra[i + 1], pixels.Bgra[i])) votes++;
            }
            if (votes > rows.Length / 2) { left = x; break; }
        }
        if (left < 0) return new(null, "Select the whole health bar.");
        // Temporary health overlays the left edge. Include its pale prefix before
        // inferring the opposite frame inset; orange alone would shift calibration.
        var orangeLeft = left;
        int paleGap=0;
        for(int cursor=left-1;cursor>=0;cursor--)
        {
            var votes = 0;
            foreach (var y in rows)
            {
                var i = (y * pixels.Width + cursor) * 4;
                if (IsTemporary(pixels.Bgra[i + 2], pixels.Bgra[i + 1], pixels.Bgra[i])) votes++;
            }
            if(votes>rows.Length/2){left=cursor;paleGap=0;}
            else if(cursor>=pixels.Width*.42&&cursor<=pixels.Width*.58&&++paleGap<=Math.Clamp(pixels.Width/100,2,12))continue;
            else break;
        }
        if (orangeLeft != left && orangeLeft - left < 3) left = orangeLeft;
        if (left > pixels.Width * .2) return new(null, "Select the whole health bar.");
        // Infer the opposite frame inset from the left, preserving the empty bar width.
        var width = pixels.Width - left * 2;
        var interior = new HealthPixels(width, rows.Length, new byte[width * rows.Length * 4]);
        for (var y = 0; y < rows.Length; y++)
            Array.Copy(pixels.Bgra, (rows[y] * pixels.Width + left) * 4, interior.Bgra, y * width * 4, width * 4);
        var sample = Analyze(interior);
        return sample.Percent is { } percent ? sample with { Status = $"Health {percent:0.#}%" } : sample;
    }

    /// <summary>Finds the fill bounds inside a full-health selection, excluding frame, text and other bars.</summary>
    public static HealthRegion? FindInterior(HealthPixels pixels)
    {
        if (pixels.Width is < 30 or > 4096 || pixels.Height is < 4 or > 300 || pixels.Bgra is null ||
            pixels.Bgra.LongLength != (long)pixels.Width * pixels.Height * 4) return null;
        var live = LiveFill(pixels);
        var counts = new int[pixels.Height];
        for (var y = 0; y < pixels.Height; y++)
            for (var x = 0; x < pixels.Width; x++)
            {
                var i = (y * pixels.Width + x) * 4;
                if (live(pixels.Bgra[i + 2], pixels.Bgra[i + 1], pixels.Bgra[i])) counts[y]++;
            }
        var maximum = 0;
        foreach (var count in counts) maximum = Math.Max(maximum, count);
        if (maximum < pixels.Width * 0.7) return null;
        var top = -1; var bottom = -1;
        for (var y = 0; y < pixels.Height; y++)
            if (counts[y] >= maximum * 0.5) { if (top < 0) top = y; bottom = y; }
        if (bottom - top + 1 < 4) return null;
        var left = pixels.Width; var right = -1;
        for (var x = 0; x < pixels.Width; x++)
        {
            var votes = 0; var rows = 0;
            for (var y = top; y <= bottom; y++)
            {
                if (counts[y] < maximum * 0.98) continue;
                rows++;
                var i = (y * pixels.Width + x) * 4;
                if (live(pixels.Bgra[i + 2], pixels.Bgra[i + 1], pixels.Bgra[i])) votes++;
            }
            if (votes > 0 && votes > rows / 2) { left = Math.Min(left, x); right = x; }
        }
        if (right - left + 1 < 30 || left > pixels.Width * .2 || Math.Abs(left - (pixels.Width - right - 1)) > Math.Max(2, pixels.Width / 100)) return null;
        var cropped = new HealthPixels(right - left + 1, bottom - top + 1, new byte[(right - left + 1) * (bottom - top + 1) * 4]);
        for (var y = 0; y < cropped.Height; y++)
            Array.Copy(pixels.Bgra, ((top + y) * pixels.Width + left) * 4, cropped.Bgra, y * cropped.Width * 4, cropped.Width * 4);
        return Analyze(cropped).Percent is >= 98 ? new HealthRegion(left, top, cropped.Width, cropped.Height) : null;
    }

    public static HealthSample Analyze(HealthPixels pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Width is < 30 or > 4096 || pixels.Height is < 4 or > 300 || pixels.Bgra == null ||
            pixels.Bgra.LongLength != (long)pixels.Width * pixels.Height * 4)
            return new(null, "Invalid health image.");

        // Select rows with the most live-fill plus Wither evidence, so full-width
        // decorative orange trim cannot outvote a withered bar. This excludes centered white text
        // and the HEALTHY badge over the top edge; a majority of clean rows must agree.
        var live = LiveFill(pixels);
        var orangeCounts = new int[pixels.Height];
        var maximum = 0;
        for (var y = 0; y < pixels.Height; y++)
        {
            for (var x = 0; x < pixels.Width; x++)
            {
                var index = (y * pixels.Width + x) * 4;
                if (live(pixels.Bgra[index + 2], pixels.Bgra[index + 1], pixels.Bgra[index]) || IsWither(pixels.Bgra[index + 2], pixels.Bgra[index + 1], pixels.Bgra[index])) orangeCounts[y]++;
            }
            maximum = Math.Max(maximum, orangeCounts[y]);
        }
        AddTemporaryEvidence(pixels,orangeCounts,live);
        if (maximum == 0) return Unknown();
        var rowCount = 0;
        var rows = new bool[pixels.Height];
        foreach (var y in SupportedRows(orangeCounts)) { rows[y] = true; rowCount++; }
        if (rowCount < Math.Max(3, pixels.Height / 5)) return Unknown();
        var required = rowCount / 2 + 1;
        var filled = new bool[pixels.Width];
        var temporary = new bool[pixels.Width];
        var wither = new bool[pixels.Width];
        for (var x = 0; x < pixels.Width; x++)
        {
            var orange = 0; var pale = 0; var blue = 0;
            for (var y = 0; y < pixels.Height; y++)
            {
                if (!rows[y]) continue;
                var index = (y * pixels.Width + x) * 4;
                if (live(pixels.Bgra[index + 2], pixels.Bgra[index + 1], pixels.Bgra[index])) orange++;
                if (IsTemporary(pixels.Bgra[index + 2], pixels.Bgra[index + 1], pixels.Bgra[index])) pale++;
                if (IsWither(pixels.Bgra[index + 2], pixels.Bgra[index + 1], pixels.Bgra[index])) blue++;
            }
            filled[x] = orange >= required;
            temporary[x] = pale >= required;
            wither[x] = blue >= required;
        }

        var maximumGap = Math.Clamp(pixels.Width / 100, 2, 12);
        var first = Array.FindIndex(filled, column => column);
        if (first < 0) return Unknown();
        var temporaryWidth = 0;
        if (first > maximumGap)
        {
            // A genuine buffer is a continuous pale prefix attached to orange,
            // never the central HEALTH label or an isolated white UI decoration.
            var paleColumns = 0; var missing = 0;
            for (var x = 0; x < first; x++)
            {
                if (temporary[x]) { paleColumns++; missing = 0; }
                else if (++missing > maximumGap) return Unknown();
            }
            if (paleColumns < 3 || !temporary[0]) return Unknown();
            temporaryWidth = first;
            for (var x = 0; x < first; x++) filled[x] = temporary[x];
            first = 0;
        }
        var end = first;
        var gap = 0;
        var supportedColumns = 0;
        for (var x = first; x < filled.Length; x++)
        {
            if (filled[x]) { end = x + 1; gap = 0; supportedColumns++; }
            else if (++gap > maximumGap) break;
        }
        if (supportedColumns < Math.Max(3, Math.Min(8, pixels.Width / 40))) return Unknown();
        // Two widely separated orange areas indicate the wrong region or an obstructed bar.
        for (var x = Math.Min(end + maximumGap, filled.Length); x < filled.Length; x++)
            if (filled[x]) return new(null, "Health area is obstructed or contains multiple orange sections.");
        var percent = Math.Round(end * 100.0 / pixels.Width, 1);
        var witherWidth = 0;
        // Wither occupies the unavailable right-hand suffix, not an arbitrary blue
        // patch or the differently coloured Depths health bar.
        // A narrow frame/antialiased end cap can cover the final blue pixels.
        // Still require the blue run to reach the right edge within the same
        // small gap tolerance used for occluded fill ticks.
        var rightWither = Array.FindLastIndex(wither, column => column);
        if (rightWither >= end && pixels.Width - 1 - rightWither <= maximumGap)
        {
            var missing = 0; var blueColumns = 0; var start = pixels.Width;
            for (var x = rightWither; x >= end; x--)
            {
                if (wither[x]) { start = x; blueColumns++; missing = 0; }
                else if (++missing > maximumGap) break;
            }
            if (blueColumns >= 3) witherWidth = pixels.Width - start;
        }
        return new(percent, "Health " + percent.ToString("0.#", CultureInfo.InvariantCulture) + "%",
            Math.Round(witherWidth * 100.0 / pixels.Width, 1), Math.Round(temporaryWidth * 100.0 / pixels.Width, 1));
    }
    private static void AddTemporaryEvidence(HealthPixels p,int[] counts,Func<byte,byte,byte,bool> live)
    {
        for(int y=0;y<p.Height;y++)
        {
            int first=-1;
            for(int x=0;x<p.Width;x++)
            {
                int i=(y*p.Width+x)*4;
                if(live(p.Bgra[i+2],p.Bgra[i+1],p.Bgra[i]) || IsWither(p.Bgra[i+2],p.Bgra[i+1],p.Bgra[i])){first=x;break;}
            }
            int pale=0,gap=0;
            for(int x=first-1;x>=0;x--)
            {
                int i=(y*p.Width+x)*4;
                if(IsTemporary(p.Bgra[i+2],p.Bgra[i+1],p.Bgra[i])){pale+=1+gap;gap=0;}
                else if(x>=p.Width*.42&&x<=p.Width*.58&&++gap<=Math.Clamp(p.Width/100,2,12))continue;
                else break;
            }
            if(pale>=3)counts[y]+=pale;
        }
    }
    private static int[] SupportedRows(int[] counts)
    {
        var active=Enumerable.Range(0,counts.Length).Where(y=>counts[y]>=3).ToArray();
        if(active.Length==0)return [];
        double centre=(active[0]+active[^1])/2d;
        int tolerance=Math.Max(1,(int)Math.Ceiling(counts.Max()*.02));
        int[] best=[];double bestDistance=double.MaxValue;int bestCount=0;
        // Prefer a repeated interior reading over a few wider decorative trim rows.
        // Equal-support groups use vertical balance, then the cleaner/wider fill.
        foreach(int count in active.Select(y=>counts[y]).Distinct())
        {
            var group=active.Where(y=>Math.Abs(counts[y]-count)<=tolerance).ToArray();
            double distance=Math.Abs(group.Average()-centre);
            if(group.Length>best.Length || group.Length==best.Length &&
                (distance<bestDistance-.01 || Math.Abs(distance-bestDistance)<.01 && count>bestCount))
            {best=group;bestDistance=distance;bestCount=count;}
        }
        return best;
    }
    // Select the live palette from the anchored health fill, not its name or hue.
    // A uniform blue Depths bar (even the same blue as surface Wither) is health.
    private static Func<byte, byte, byte, bool> LiveFill(HealthPixels pixels)
    {
        var required = Math.Max(3, pixels.Height / 5);
        for (var x = 0; x < pixels.Width; x++)
        {
            var warm = 0;
            for (var y = 0; y < pixels.Height; y++)
            {
                var i = (y * pixels.Width + x) * 4;
                if (IsOrange(pixels.Bgra[i + 2], pixels.Bgra[i + 1], pixels.Bgra[i])) warm++;
            }
            if (warm >= required) return IsOrange;
        }
        for (var x = 0; x < pixels.Width; x++)
        {
            var values = new List<byte>();
            for (var y = 0; y < pixels.Height; y++)
            {
                var i = (y * pixels.Width + x) * 4;
                if (IsBlue(pixels.Bgra[i + 2], pixels.Bgra[i + 1], pixels.Bgra[i])) values.Add(pixels.Bgra[i]);
            }
            if (values.Count < required) continue;
            values.Sort();
            var floor = Math.Max(30, values[values.Count / 2] * .65);
            return (r, g, b) => IsBlue(r, g, b) && b >= floor;
        }
        return IsOrange;
    }
    private static bool IsBlue(byte r, byte g, byte b)
        => b >= 35 && b >= r + 12 && b >= g + 5;
    public static bool IsLiveHealthColour(byte r, byte g, byte b) => IsOrange(r, g, b) || IsBlue(r, g, b);
    public static bool IsTemporaryHealthColour(byte r, byte g, byte b) => IsTemporary(r, g, b);
    private static bool IsTemporary(byte r, byte g, byte b)
        => Math.Min(r, Math.Min(g, b)) >= 115 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 40;
    private static bool IsWither(byte r, byte g, byte b)
        => b is >= 35 and <= 145 && r <= 100 && b >= r + 12 && b >= g + 5;
    private static HealthSample Unknown() => new(null, "Health bar not detected. Select the full health bar.");
    private static bool IsOrange(byte red, byte green, byte blue)
    {
        var minimum = Math.Min(green, blue);
        var delta = red - minimum;
        if (red < 85 || red <= green || green <= blue || delta < red * 0.35) return false;
        var hue = 60.0 * (green - blue) / delta;
        return hue is >= 10 and <= 50;
    }
}
