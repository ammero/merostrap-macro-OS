// SPDX-License-Identifier: MPL-2.0
using System;

namespace Merostrap.Macros.Health;
/// <summary>Two readings at/below the selected threshold; unknown readings cannot trigger.</summary>
public sealed class HealthThresholdLatch
{
    private readonly object _gate = new();
    private bool _armed = true;
    private bool _low;
    private int _lowStreak;
    public bool IsLow { get { lock (_gate) return _low; } }
    public HealthThresholdTransition Observe(HealthSample sample, int threshold = 50)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (threshold is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(threshold));
        lock (_gate)
        {
            if (sample.Percent is not { } percent || !double.IsFinite(percent) || percent is < 0 or > 100)
            { _lowStreak = 0; return HealthThresholdTransition.None; }
            if (percent > Math.Min(100, threshold + 5))
            {
                var recovered = _low;
                _armed = true; _low = false; _lowStreak = 0;
                return recovered ? HealthThresholdTransition.Recovered : HealthThresholdTransition.None;
            }
            if (_low || !_armed || percent > threshold) { _lowStreak = 0; return HealthThresholdTransition.None; }
            if (++_lowStreak < 2) return HealthThresholdTransition.None;
            _low = true; _lowStreak = 0;
            return HealthThresholdTransition.Low;
        }
    }
    public void Reset() { lock (_gate) { _armed = true; _low = false; _lowStreak = 0; } }
}
