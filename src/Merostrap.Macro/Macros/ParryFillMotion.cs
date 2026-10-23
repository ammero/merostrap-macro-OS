namespace Merostrap.Macros;

/// <summary>Display-only motion; never used to authorize a game input.</summary>
public sealed class ParryFillMotion
{
    private double? _visible;
    private double _rate;
    private long _visibleAt;
    private long? _advancedAt;
    private bool _covered;
    public double Value { get; private set; }
    public double DisplayValue { get; private set; }
    public void Observe(double? percent, bool spectralCovered, long now)
    {
        if (percent is { } measured && double.IsFinite(measured))
        {
            measured = Math.Clamp(measured, 0, 100);
            if (_visible is { } previous && now > _visibleAt)
            {
                double difference = measured - previous;
                if (difference < -3) _rate = 0;
                else if (difference > .1)
                {
                    double rate = Math.Clamp(difference * 1000 / (now - _visibleAt), 0, 300);
                    _rate = _rate > 0 ? (_rate + rate) / 2 : rate;
                }
                if (difference < -20) DisplayValue = measured;
            }
            else DisplayValue = measured;
            _visible = Value = measured;
            _visibleAt = now;
            _covered = false;
        }
        else if (spectralCovered && _visible.HasValue && now - _visibleAt <= 2500)
            _covered = true;
        else Reset();
    }
    public void Advance(long now)
    {
        if (_covered && _visible is { } visible)
        {
            if (now - _visibleAt > 2500) Reset();
            else Value = Math.Min(99, visible + _rate * Math.Max(0, now - _visibleAt) / 1000);
        }
        double elapsed = _advancedAt is { } previous ? Math.Max(0, now - previous) : 0;
        _advancedAt = now;
        DisplayValue += (Value - DisplayValue) * (1 - Math.Exp(-elapsed / 60));
        if (Math.Abs(Value - DisplayValue) < .01) DisplayValue = Value;
    }
    public void Reset()
    {
        _visible = null; _rate = 0; _covered = false; _advancedAt = null;
        Value = DisplayValue = 0;
    }
}
