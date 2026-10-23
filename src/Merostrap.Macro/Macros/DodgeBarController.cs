using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Merostrap.Macros.Health;
using Merostrap.UI.Elements.Macros;

namespace Merostrap.Macros;

public sealed class DodgeBarController : ObservableObject, IDisposable
{
    private readonly DodgeBarSettings _settings;
    private readonly Action _save;
    private readonly Action _changed;
    private readonly HealthMonitor _monitor;
    private readonly DispatcherTimer _stale = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DodgeCooldown _motion = new();
    private DodgeBarOverlay? _overlay;
    private long _generation;
    private long _lastSample;
    private long _lastRecovery;
    private readonly Func<DetectedDodgeIcon?> _recover;
    private DetectedDodgeIcon? _recoveryCandidate;
    private bool _recovering;
    private bool _running;
    private bool _disposed;
    private bool _foreground;
    private bool _sampleKnown;
    private double? _percent;
    private string _status = "Select icon";
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
        var folder = Path.Combine(StandalonePaths.ConfigRoot, "DodgeImages");
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

    public DodgeBarController(DodgeBarSettings settings, Action save, Action changed, HealthMonitor? monitor = null, Func<DetectedDodgeIcon?>? recover = null)
    {
        _settings=settings;_save=save;_changed=changed;_motion.CooldownSeconds=settings.CooldownSeconds;
        _recover = recover ?? (monitor is null ? HudBarCapture.DetectDodge : () => null);
        _monitor=monitor??new HealthMonitor(interval:TimeSpan.FromMilliseconds(33),analyze:pixels=>{
            if(_settings.Track is not {} track||_settings.Region is not {} region)return new HealthSample(null,"Select icon");
            var icon=HudBarDetector.Crop(pixels,new(0,0,region.Width,region.Height));
            bool? ready=DodgeIconAnalyzer.Read(icon,track);
            return new HealthSample(ready is null?null:ready.Value?100:0,"Dodge");
        });
        _monitor.Updated+=sample=>{
            long generation=_generation;Dispatcher.UIThread.Post(()=>{if(!_disposed&&_running&&generation==_generation)Receive(sample.Percent);});
        };
        _stale.Tick+=(_,_)=>{
            if(!_running)return;
            long now=Environment.TickCount64;
            if(now-_lastSample>200){_motion.Observe(null,now);SetReading(null,"Unknown");}
            else if(_sampleKnown){var fill=_motion.Advance(now);SetReading(fill,fill>=100?"Ready":"Cooldown");}
        };
        ShowOverlayCommand=new RelayCommand(()=>{_settings.Visible=true;_save();RestoreOverlay();});
        _status=IsReady?"Paused":"Select icon";
    }
    public double CooldownSeconds
    {
        get=>_settings.CooldownSeconds;
        set{if(!double.IsFinite(value))return;_settings.CooldownSeconds=Math.Clamp(value,.25,10);_motion.CooldownSeconds=_settings.CooldownSeconds;_save();OnPropertyChanged();}
    }
    public IRelayCommand ShowOverlayCommand { get; }
    public string BarColor
    {
        get => Avalonia.Media.Color.TryParse(_settings.Color, out _) ? _settings.Color : "#69B9E8";
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
    public double Fill => _percent ?? 0;
    public double FillRatio => Fill / 100;
    public double DisplayFillRatio => FillRatio;
    public double DisplayImageFillWidth => ImageWidth * DisplayFillRatio;
    public string Status => _status;
    public string SelectionLabel => _settings.Region is null ? "Select icon" : "Reselect icon";
    public int? X => _settings.X;
    public int? Y => _settings.Y;
    public void SavePosition(int x, int y) { _settings.X = x; _settings.Y = y; _save(); }
    public void StopCapture() { _running = false; Interlocked.Increment(ref _generation); _recoveryCandidate = null; _monitor.Stop(); }
    public void SetArmed(bool active)
    {
        StopCapture(); _stale.Stop(); _motion.Reset();_sampleKnown=false;
        SetReading(null, _settings.Region is null ? "Select icon" : "Paused");
        if (!active || !IsReady || _disposed) return;
        try
        {
            _running = true; _lastSample = Environment.TickCount64;
            _monitor.Start(_settings.Region! with {Width=Math.Max(30,_settings.Region.Width)}); _stale.Start();
            SetReading(null, "Unknown");
        }
        catch (Exception) { _running = false; SetReading(null, "Reselect icon"); }
    }
    private void Receive(double? percent)
    {
        if(!_running || _disposed)return;
        if(!_foreground){_sampleKnown=false;_motion.Reset();SetReading(null,"Waiting for Roblox");return;}
        var now=Environment.TickCount64;
        if(percent is null && now-_lastSample>=1000 && now-_lastRecovery>=2000 && !_recovering)
            _ = RecoverAsync();
        if(percent is null&&now-_lastSample<=150)return;
        _sampleKnown=percent is not null;
        if(_sampleKnown){_lastSample=now;_recoveryCandidate=null;}
        _motion.Observe(percent is null?null:percent>=99.5,_lastSample);
        double? fill=percent is null?null:_motion.Advance(_lastSample);
        SetReading(fill,fill is null?"Unknown":fill>=100?"Ready":"Cooldown");
    }
    private async Task RecoverAsync()
    {
        _recovering=true;_lastRecovery=Environment.TickCount64;
        var generation=_generation;
        try
        {
            var found=await Task.Run(_recover);
            if(_disposed || !_running || !_foreground || generation!=_generation || _sampleKnown)return;
            if(found is null || !HudBarCapture.SameLocation(found.Region,_recoveryCandidate?.Region))
            { _recoveryCandidate=found; return; }
            _settings.Region=found.Region;_settings.Track=found.Track;_save();
            OnPropertyChanged(nameof(SelectionLabel));OnPropertyChanged(nameof(IsReady));
            SetArmed(true);
        }
        catch(Exception){_recoveryCandidate=null;}
        finally{_recovering=false;}
    }
    private void SetReading(double? percent,string status)
    {
        _percent=percent;_status=status;
        foreach(var name in new[]{nameof(Fill),nameof(FillRatio),nameof(ImageFillWidth),nameof(DisplayFillRatio),nameof(DisplayImageFillWidth),nameof(Status)})OnPropertyChanged(name);
    }
    public void RestoreOverlay()
    {
        if (!Enabled || !_settings.Visible || _disposed) return;
        if (!_foreground) { _overlay?.Hide(); return; }
        if (_overlay is null) { _overlay = new DodgeBarOverlay(this); _overlay.Closed += (_, _) => _overlay = null; }
        if (!_overlay.IsVisible) _overlay.TryShow();
    }
    public void SetForeground(bool foreground)
    {
        if(_foreground && !foreground)Interlocked.Increment(ref _generation);
        _foreground = foreground;
        if(!foreground){_recoveryCandidate=null;_sampleKnown=false;_motion.Reset();SetReading(null,"Waiting for Roblox");}
        if (!foreground) _overlay?.Hide(); else RestoreOverlay();
    }
    public bool IsOverlayForeground(IntPtr handle) => _overlay?.IsVisible == true && _overlay.TryGetPlatformHandle()?.Handle == handle;
    public void HideOverlay() { _settings.Visible = false; _save(); _overlay?.Hide(); }
    public async Task SelectRegionAsync(Window owner, bool automatic=false)
    {
        var workspace = owner.DataContext as MacroWorkspace;
        var wasRunning = workspace?.Enabled == true;
        if (workspace is not null) workspace.Health.PreserveDeafenLatch(() => workspace.Enabled = false);
        SetArmed(false); _overlay?.Hide(); owner.Hide();
        try
        {
            var detected=automatic ? await HudAutoDetection.PickDodgeAsync(owner) : null;
            var region=automatic ? detected?.Region : await HealthRegionPicker.PickAsync(owner,"Select only the leftmost dodge icon while dodge is ready. Esc cancels.",96,6,96);
            if (region is not null)
            {
                await Task.Delay(150); // Let picker windows leave the captured desktop.
                var captured=new WindowsHealthCapture().Capture(region with {Width=Math.Max(30,region.Width)});
                var track=detected?.Track ?? DodgeIconAnalyzer.Calibrate(HudBarDetector.Crop(captured,new(0,0,region.Width,region.Height)));
                if (track is null) { SetReading(null, "Select the dodge icon while ready"); return; }
                _settings.Region = region; _settings.Track = track; _save();
                OnPropertyChanged(nameof(SelectionLabel)); OnPropertyChanged(nameof(IsReady));
            }
            _changed();
        }
        catch (Exception) { SetReading(null, "Could not select icon"); }
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
