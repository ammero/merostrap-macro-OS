using Avalonia.Threading;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Merostrap.Macros.Health;
using Merostrap.UI.Elements.Macros;

namespace Merostrap.Macros;

// The shade detects activation; elapsed time supplies the requested ten-second counter.
public sealed class FlowStateCooldownCounter
{
    private long? _started;
    private double? _previous;
    public FlowStateCooldownSample Observe(FlowStateCooldownSample sample, long now)
    {
        if (sample.ReadyPercent is {} reading)
        {
            if ((_previous is null && reading < 95) || (_previous is {} previous && previous - reading > 30))
                _started = now;
            _previous = reading;
        }
        if (_started is {} start)
        {
            var percent = Math.Clamp((now - start) / 1000 * 10, 0, 100);
            return new(percent, percent == 100 ? "Ready" : "Cooling down");
        }
        return sample.ReadyPercent >= 95 ? new(100, "Ready") : new(null, sample.Status);
    }
}

public sealed class FlowStateOverlayController : ObservableObject, IDisposable
{
    private readonly FlowStateOverlaySettings _settings;
    private readonly Action _save, _changed;
    private readonly bool _native;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly FlowStateCooldownReader _reader = new();
    private readonly FlowStateCooldownCounter _counter = new();
    private FlowStateCooldownOverlay? _overlay;
    private bool _busy, _disposed, _foreground;
    private long _retry;
    private double? _percent;
    private string _status = "Waiting for Roblox";
    public FlowStateOverlayController(FlowStateOverlaySettings settings, Action save, Action changed, bool native = false)
    {
        _settings = settings; _save = save; _changed = changed; _native = native; _status = settings.Enabled ? "Waiting for Roblox" : "Off";
        _timer.Tick += async (_, _) => await ReadAsync();
    }
    public bool Enabled
    {
        get => _settings.Enabled;
        set { if (value == Enabled) return; _settings.Enabled = value; _save(); OnPropertyChanged(); _changed(); }
    }
    public IReadOnlyList<string> Layouts { get; } = new[] { "Horizontal", "Vertical" };
    public int LayoutIndex
    {
        get => _settings.Vertical ? 1 : 0;
        set
        {
            if (value is not (0 or 1) || value == LayoutIndex) return;
            _settings.Vertical = value == 1; _save(); OnPropertyChanged();
            OnPropertyChanged(nameof(Orientation)); OnPropertyChanged(nameof(BarWidth)); OnPropertyChanged(nameof(BarHeight));
        }
    }
    public Avalonia.Layout.Orientation Orientation => _settings.Vertical ? Avalonia.Layout.Orientation.Vertical : Avalonia.Layout.Orientation.Horizontal;
    public double BarWidth => _settings.Vertical ? 18 : 260;
    public double BarHeight => _settings.Vertical ? 260 : 18;
    public string BarColor
    {
        get => Color.TryParse(_settings.Color, out _) ? _settings.Color : "#39BA68";
        set { if (!Color.TryParse(value, out _) || value == BarColor) return; _settings.Color = value; _save(); OnPropertyChanged(); OnPropertyChanged(nameof(FillBrush)); }
    }
    public IBrush FillBrush => Brush.Parse(BarColor);
    public double Fill => _percent ?? 0;
    public string Status => _status;
    public int? X => _settings.X;
    public int? Y => _settings.Y;
    public void SavePosition(int x, int y) { _settings.X = x; _settings.Y = y; _save(); }
    public void SetReading(FlowStateCooldownSample sample)
    {
        _percent = sample.ReadyPercent; _status = sample.Status;
        OnPropertyChanged(nameof(Fill)); OnPropertyChanged(nameof(Status));
    }
    public void SetForeground(bool foreground)
    {
        _foreground = foreground;
        if (!Enabled || !foreground || _disposed)
        {
            _timer.Stop(); _overlay?.Hide(); if (!Enabled) SetReading(new(null, "Off")); return;
        }
        if (_native)
        {
            _overlay ??= new FlowStateCooldownOverlay(this);
            if (!_overlay.IsVisible) _overlay.Show();
            _timer.Start();
        }
    }
    private async Task ReadAsync()
    {
        if (_busy || _disposed || !_foreground || !Enabled || Environment.TickCount64 < _retry) return;
        _busy = true;
        try
        {
            var sample = await Task.Run(() =>
            {
                var window = WindowsHealthCapture.ForegroundRobloxWindow();
                var area = WindowsHealthCapture.ForegroundRobloxClientArea();
                if (window == nint.Zero || area is null) return new FlowStateCooldownSample(null, "Waiting for Roblox");
                int height = Math.Min(216, Math.Max(40, area.Height / 10));
                var strip = new HealthRegion(area.X + area.Width / 5, area.Y + area.Height - height, area.Width * 3 / 5, height);
                var pixels = new WindowsHealthCapture().Capture(strip);
                return WindowsHealthCapture.ForegroundRobloxWindow() == window ? _reader.Analyze(pixels) : new(null, "Waiting for Roblox");
            });
            if (!_disposed) SetReading(_counter.Observe(sample, Environment.TickCount64));
            if (sample.ReadyPercent is null) _retry = Environment.TickCount64 + 1000;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_disposed) SetReading(_counter.Observe(new(null, "Hotbar unavailable"), Environment.TickCount64));
            _retry = Environment.TickCount64 + 1000;
        }
        finally { _busy = false; if (_disposed) _reader.Dispose(); }
    }
    public void Dispose() { _disposed = true; _timer.Stop(); _overlay?.Close(); if (!_busy) _reader.Dispose(); }
}
