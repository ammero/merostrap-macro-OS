using System;
using System.Collections.Generic;

namespace Merostrap.Macros;

/// <summary>Recognizes physical move gestures before their final attack reaches Roblox.</summary>
internal sealed class SilentheartFlowDetector(string dodge, string crouch, string jump, string feint)
{
    private readonly HashSet<string> _down = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _lastForward = -10000, _dodgeUntil, _slideUntil, _aerialUntil;
    private bool _sprinting, _aerialAttack;
    public bool Observe(MacroInputEvent input, long now)
    {
        lock (_gate) return ObserveCore(input, now);
    }
    private bool ObserveCore(MacroInputEvent input, long now)
    {
        if (input.IsInjected) return false;
        var key = MacroKeys.Normalize(input.Key);
        if (!input.IsDown) { _down.Remove(key); if (key == "w") _sprinting = false; return false; }
        if (!_down.Add(key)) return false;
        if (key == "w") { _sprinting = now - _lastForward <= 600; _lastForward = now; }
        if (key == dodge) _dodgeUntil = now + 250;
        if (key == crouch && _down.Contains("w") && _sprinting) _slideUntil = now + 700;
        if (key == jump) { _aerialUntil = now + 1200; _aerialAttack = false; }
        if (key == feint && _down.Contains("ctrl")) return true;
        if (key != "mouse1") return false;
        if (_dodgeUntil > now || _slideUntil > now) { _dodgeUntil = _slideUntil = 0; return true; }
        // Relentless Hunt is a second attack during an aerial attack, not the jump's first M1.
        if (_aerialUntil > now)
        {
            if (_aerialAttack) { _aerialUntil = 0; _aerialAttack = false; return true; }
            _aerialAttack = true; _aerialUntil = Math.Min(_aerialUntil, now + 700);
        }
        return false;
    }
    public void Reset()
    {
        lock (_gate)
        {
            _down.Clear(); _lastForward = -10000; _dodgeUntil = _slideUntil = _aerialUntil = 0;
            _sprinting = _aerialAttack = false;
        }
    }
}
