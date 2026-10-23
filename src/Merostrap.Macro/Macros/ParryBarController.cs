using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Merostrap.Macros.Health;
using Merostrap.UI.Elements.Macros;

namespace Merostrap.Macros;

public sealed class ParryBarController : ObservableObject, IDisposable
{
    private readonly ParryBarSettings _settings;
    private readonly Action _save;
    private readonly Action _changed;
    private readonly HealthMonitor _monitor;
    private readonly Func<DetectedParryBar?> _recover;
    public event Action<DetectedParryBar>? Recovered;
    private long _lastGoodSample, _lastRecovery;
    private bool _recovering;
    private DetectedParryBar? _recoveryCandidate;
    private readonly DispatcherTimer _stale = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly ParryFillMotion _motion = new();
    private bool _spectralCovered;
    private ParryBarOverlay? _overlay;
    private long _generation;
    private long _lastSample;
    private bool _running;
    private bool _disposed;
    private bool _foreground;
    private double? _percent;
    private string _status = "Select bar";
    private Avalonia.Media.Imaging.Bitmap? _barImage;
    private bool _imageLoaded;
    public Avalonia.Media.Imaging.Bitmap? BarImage { get { LoadImage(); return _barImage; } }
    public bool HasImage => BarImage is not null;
    public string? AnimatedImagePath => HasImage && string.Equals(Path.GetExtension(_settings.ImagePath), ".gif", StringComparison.OrdinalIgnoreCase) ? _settings.ImagePath : null;
    public double ScalePercent
    {
        get => double.IsFinite(_settings.ScalePercent) && _settings.ScalePercent is >= 50 and <= 300 ? _settings.ScalePercent : 100;
        set
        {
            if (!double.IsFinite(value)) return;
            var scale = Math.Clamp(Math.Round(value), 50, 300);
            if (ScalePercent == scale) return;
            _settings.ScalePercent = scale; _save();
            OnPropertyChanged(); OnPropertyChanged(nameof(ScaleLabel));
            OnPropertyChanged(nameof(BarWidth)); OnPropertyChanged(nameof(BarHeight));
            OnPropertyChanged(nameof(ImageWidth)); OnPropertyChanged(nameof(ImageHeight));
            OnPropertyChanged(nameof(ImageFillWidth));
            OnPropertyChanged(nameof(DisplayImageFillWidth));
        }
    }
    public string ScaleLabel => $"{ScalePercent:0}%";
    public double BarWidth => 260 * ScalePercent / 100;
    public double BarHeight => 18 * ScalePercent / 100;
    public double ImageWidth => BarWidth - 2;
    public double ImageHeight => BarHeight - 2;
    public double ImageFillWidth => ImageWidth * FillRatio;
    public string ImageLabel => HasImage ? "Change image" : "Choose image";
    private void LoadImage()
    {
        if (_imageLoaded || _disposed) return;
        _imageLoaded = true;
        try { if (File.Exists(_settings.ImagePath)) _barImage = new Avalonia.Media.Imaging.Bitmap(_settings.ImagePath); }
        catch (Exception) { _barImage = null; }
    }
    public void SetImage(string path)
    {
        using var stream = File.OpenRead(path);
        using var decoded = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 1024);
        var folder = Path.Combine(StandalonePaths.ConfigRoot, "ParryImages");
        Directory.CreateDirectory(folder);
        var gif = string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);
        var destination = Path.Combine(folder, Guid.NewGuid().ToString("N") + (gif ? ".gif" : ".png"));
        if (gif) File.Copy(path, destination); else decoded.Save(destination, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        var image = new Avalonia.Media.Imaging.Bitmap(destination);
        var previous = _barImage;
        _barImage = image; _imageLoaded = true; _settings.ImagePath = destination;
        NotifyImage(); previous?.Dispose();
    }
    public void ClearImage()
    {
        var previous = _barImage;
        _barImage = null; _imageLoaded = true; _settings.ImagePath = null;
        NotifyImage(); previous?.Dispose();
    }
    private void NotifyImage()
    {
        _save(); OnPropertyChanged(nameof(BarImage)); OnPropertyChanged(nameof(HasImage)); OnPropertyChanged(nameof(ImageLabel)); OnPropertyChanged(nameof(AnimatedImagePath));
    }

    public ParryBarController(ParryBarSettings settings, Action save, Action changed, HealthMonitor? monitor = null, Func<DetectedHudBars?>? recover = null)
    {
        _settings = settings; _save = save; _changed = changed;
        _recover = recover is not null ? () => recover() is { } found ? new(found.Parry,found.Track) : null : monitor is null ? HudBarCapture.DetectParry : () => null;
        _monitor = monitor ?? new HealthMonitor(interval: TimeSpan.FromMilliseconds(33), analyze: pixels =>
        {
            if (_settings.Track is not { } track) return new HealthSample(null, "Parry");
            var percent = ParryBarAnalyzer.Read(pixels, track);
            return new HealthSample(percent, percent is null && ParryBarAnalyzer.IsSpectralCovered(pixels, track) ? "Spectral" : "Parry");
        });
        _monitor.Updated += sample =>
        {
            var generation = _generation;
            Dispatcher.UIThread.Post(() => {
                if (!_disposed && _running && generation == _generation)
                { _spectralCovered = sample.Status == "Spectral"; Receive(sample.Percent); }
            });
        };
        _stale.Tick += (_, _) => {
            if (!_running) return;
            var now = Environment.TickCount64;
            if (now - _lastSample > 200) { _spectralCovered = false; SetReading(null, "Unknown"); }
            _motion.Advance(now);
            OnPropertyChanged(nameof(DisplayFillRatio)); OnPropertyChanged(nameof(DisplayImageFillWidth));
            if (_spectralCovered) { OnPropertyChanged(nameof(Fill)); OnPropertyChanged(nameof(FillRatio)); OnPropertyChanged(nameof(ImageFillWidth)); }
        };
        ShowOverlayCommand = new RelayCommand(() => { _settings.Visible = true; _save(); RestoreOverlay(); });
        _status = IsReady ? "Paused" : "Select bar";
    }
    public IRelayCommand ShowOverlayCommand { get; }
    public string BarColor
    {
        get => Avalonia.Media.Color.TryParse(_settings.Color, out _) ? _settings.Color : "#39BA68";
        set
        {
            if (!Avalonia.Media.Color.TryParse(value, out var color)) return;
            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            if (hex == BarColor) return;
            _settings.Color = hex;
            _save(); OnPropertyChanged(); OnPropertyChanged(nameof(FillBrush));
        }
    }
    public Avalonia.Media.IBrush FillBrush => Avalonia.Media.Brush.Parse(BarColor);
    public bool Enabled
    {
        get => _settings.Enabled;
        set
        {
            if (value == Enabled) return;
            _settings.Enabled = value;
            if (value) _settings.Visible = true;
            else { SetArmed(false); _overlay?.Hide(); }
            _save(); OnPropertyChanged(); OnPropertyChanged(nameof(IsReady)); _changed(); RestoreOverlay();
        }
    }
    public bool IsReady => Enabled && _settings.Region is not null && _settings.Track is not null;
    public bool IsRunning => _running;
    public double Fill => _percent ?? (_spectralCovered ? _motion.Value : 0);
    public double FillRatio => Fill / 100;
    public double DisplayFillRatio => _motion.DisplayValue / 100;
    public double DisplayImageFillWidth => ImageWidth * DisplayFillRatio;
    public string Status => _status;
    public string SelectionLabel => _settings.Region is null ? "Select bar" : "Reselect bar";
    public int? X => _settings.X;
    public int? Y => _settings.Y;
    public void SavePosition(int x, int y) { _settings.X = x; _settings.Y = y; _save(); }
    public void StopCapture() { _running = false; Interlocked.Increment(ref _generation); _recoveryCandidate = null; _monitor.Stop(); }
    public void SetArmed(bool active)
    {
        StopCapture(); _stale.Stop(); _spectralCovered = false; _motion.Reset();
        SetReading(null, _settings.Region is null ? "Select bar" : "Paused");
        if (!active || !IsReady || _disposed) return;
        try
        {
            _running = true; _lastGoodSample = _lastSample = Environment.TickCount64;
            _monitor.Start(_settings.Region!); _stale.Start();
            SetReading(null, "Unknown");
        }
        catch (Exception) { _running = false; SetReading(null, "Reselect bar"); }
    }
    private void Receive(double? percent)
    {
        _lastSample = Environment.TickCount64;
        if (percent is not null) { _lastGoodSample = _lastSample; _recoveryCandidate = null; }
        else if (!_spectralCovered && _foreground && !_recovering && _lastSample - _lastGoodSample >= 1000 && _lastSample - _lastRecovery >= 2000)
            _ = RecoverAsync();
        SetReading(percent, percent is null ? "Unknown" : percent >= 99.5 ? "Ready" : "Cooldown");
    }
    private async Task RecoverAsync()
    {
        _recovering = true; _lastRecovery = Environment.TickCount64;
        var generation = _generation;
        try
        {
            var found = await Task.Run(_recover);
            if (_disposed || !_running || !_foreground || generation != _generation || _percent is not null) return;
            // Require the same ready gradient in two scans.
            // A loading screen or a partially filled cooldown must not become calibration.
            if (found is null || !HudBarCapture.SameLocation(found, _recoveryCandidate)) { _recoveryCandidate = found; return; }
            ApplyDetectedParry(found);
            Recovered?.Invoke(found);
            SetArmed(true);
        }
        catch (Exception) { _recoveryCandidate = null; }
        finally { _recovering = false; }
    }
    private void SetReading(double? percent, string status)
    {
        _motion.Observe(percent, _spectralCovered && status == "Unknown", Environment.TickCount64);
        _motion.Advance(Environment.TickCount64);
        if (_spectralCovered && percent is null && _motion.Value > 0) status = "Estimated";
        _percent = percent; _status = status; OnPropertyChanged(nameof(Fill)); OnPropertyChanged(nameof(FillRatio)); OnPropertyChanged(nameof(ImageFillWidth)); OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(DisplayFillRatio)); OnPropertyChanged(nameof(DisplayImageFillWidth));
    }
    public void RestoreOverlay()
    {
        if (!Enabled || !_settings.Visible || _disposed) return;
        if (!_foreground) { _overlay?.Hide(); return; }
        if (_overlay is null) { _overlay = new ParryBarOverlay(this); _overlay.Closed += (_, _) => _overlay = null; }
        if (!_overlay.IsVisible) _overlay.TryShow();
    }
    public void SetForeground(bool foreground)
    {
        _foreground = foreground;
        if (!foreground) _recoveryCandidate = null;
        if (!foreground) _overlay?.Hide(); else RestoreOverlay();
    }
    public bool IsOverlayForeground(IntPtr handle) => _overlay?.IsVisible == true && _overlay.TryGetPlatformHandle()?.Handle == handle;
    public void HideOverlay() { _settings.Visible = false; _save(); _overlay?.Hide(); }
    public void ApplyDetectedBars(DetectedHudBars bars) => ApplyDetectedParry(new(bars.Parry,bars.Track));
    public void ApplyDetectedParry(DetectedParryBar bars)
    {
        bars.Parry.Validate();
        _settings.Region = bars.Parry; _settings.Track = bars.Track; _save();
        OnPropertyChanged(nameof(SelectionLabel)); OnPropertyChanged(nameof(IsReady));
    }
    public async Task SelectRegionAsync(Window owner, bool automatic = false)
    {
        var workspace = owner.DataContext as MacroWorkspace;
        var wasRunning = workspace?.Enabled == true;
        if (workspace is not null) workspace.Health.PreserveDeafenLatch(() => workspace.Enabled = false);
        SetArmed(false); _overlay?.Hide(); owner.Hide();
        try
        {
            var detected = automatic ? await HudAutoDetection.PickParryAsync(owner) : null;
            var region = automatic ? detected?.Parry : await HealthRegionPicker.PickAsync(owner, "Select the full parry cooldown bar while it is ready. Esc cancels.");
            if (region is not null)
            {
                await Task.Delay(150); // Let picker windows leave the captured desktop.
                var track = detected?.Track ?? ParryBarAnalyzer.Calibrate(new WindowsHealthCapture().Capture(region));
                if (track is null) { SetReading(null, "Select the full coloured bar while ready"); return; }
                _settings.Region = region; _settings.Track = track; _save();
                OnPropertyChanged(nameof(SelectionLabel)); OnPropertyChanged(nameof(IsReady));
            }
            _changed();
        }
        catch (Exception) { SetReading(null, "Could not select bar"); }
        finally
        {
            if (!_disposed)
            {
                if (wasRunning && workspace is not null && !workspace.Enabled)
                    workspace.Health.PreserveDeafenLatch(() => workspace.Enabled = true);
                owner.Show(); owner.Activate(); RestoreOverlay();
            }
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; SetArmed(false); _monitor.Dispose(); _overlay?.Close(); _barImage?.Dispose();
    }
}
