// SPDX-License-Identifier: MPL-2.0
using System;
namespace Merostrap.Macros.Health;
public sealed record HealthRegion(int X, int Y, int Width, int Height)
{
    public void Validate()
    {
        if (Width is < 30 or > 4096 || Height is < 4 or > 300)
            throw new ArgumentException("Select a health bar interior between 30 and 4096 pixels wide and 4 and 300 pixels high.");
        if ((long)X + Width > int.MaxValue || (long)Y + Height > int.MaxValue)
            throw new ArgumentException("The health region exceeds screen coordinate limits.");
    }
}
/// <summary>Tightly packed top-down BGRA pixels. GDI may leave alpha zero; color recognition ignores alpha.</summary>
public sealed record HealthPixels(int Width, int Height, byte[] Bgra);
public sealed record HealthSample(double? Percent, string Status, double? WitherPercent = null, double? TemporaryPercent = null);
public enum HealthThresholdTransition { None, Low, Recovered }

public sealed record HealthLabel(double Percent, HealthRegion Bounds);
