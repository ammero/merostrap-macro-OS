using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Merostrap.Macros.Health;
using Merostrap.UI.Elements.Macros;

namespace Merostrap.Macros;

/// <summary>Opt-in screen health tracking and a Discord shortcut per low-health cycle.</summary>
public sealed class HealthAutomation : ObservableObject, IDisposable
{
    private readonly string _path;
    private readonly Action _changed;
    private readonly Action _resumeAfterEdit;
    private readonly Func<bool> _resumeRequested;
    private readonly HealthMonitor _monitor;
    private readonly HealthTextReader? _textReader;
    private readonly IMacroInput _input;
    private readonly IDiscordDeafenState? _discord;
    private readonly Func<HealthRegion?> _recover;
    private long _lastGoodSample, _lastRecovery;
    private bool _recovering;
    private bool ShouldTrack => Enabled && (_armed || _preview);
    private HealthRegion? _recoveryCandidate;
    public event Action<HealthRegion>? Recovered;
    private CancellationTokenSource? _confirmation;
    private string _deafenStatus = "Unconfirmed";
    public bool ShowDeafenStatus => _discord is not null && Enabled && AutoDeafen;
    public string DeafenStatus { get => _deafenStatus; private set => SetProperty(ref _deafenStatus, value); }
    private readonly HealthThresholdLatch _threshold = new();
    private Settings _settings = new();
    private bool _armed, _preview, _fired, _disposed;
    private int _generation, _sampleSequence;
    private readonly object _inputGate = new();
    private string? _pendingKey;
    private object? _pressOwner;
    public string? Fault { get; private set; }
    private bool _storageBlocked;
    private string _status = "Select health bar";
    private double? _percent;
    private HealthSample _sample = new(null, "Waiting for health");
    public HealthAutomation(string path, Action changed, HealthMonitor? monitor = null, IMacroInput? input = null, IDiscordDeafenState? discord = null, Action? resumeAfterEdit = null, Func<bool>? resumeRequested = null, Func<DetectedHudBars?>? recover = null)
    {
        // Keep experimental confirmation opt-in; production uses Discord's global keybind.
        _discord = discord;
        _recover = recover is not null ? () => recover()?.Health : monitor is null ? HudBarCapture.DetectHealth : () => null;
        _deafenStatus = discord is null ? "Paused" : "Unconfirmed";
        _resumeAfterEdit = resumeAfterEdit ?? (() => SetArmed(true));
        _resumeRequested = resumeRequested ?? (() => _armed);
        _path = path; _changed = changed; _textReader = monitor is null ? new HealthTextReader() : null; _monitor = monitor ?? new HealthMonitor(analyze: _textReader!.Analyze); _input = input ?? new WindowsMacroInput();
        try { if (File.Exists(path)) _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _storageBlocked = true; _status = "Could not load health settings; existing file preserved"; }
        _monitor.Updated += sample =>
        {
            var generation = _generation;
            var sequence = Interlocked.Increment(ref _sampleSequence);
            Dispatcher.UIThread.Post(() => { if (!_disposed && generation == _generation && sequence == _sampleSequence) Receive(sample); });
        };

    }
    public bool Enabled { get => _settings.Enabled; set { if (value == Enabled) return; _settings.Enabled = value; Update(); OnPropertyChanged(); } }
    public bool AutoDeafen { get => _settings.AutoDeafen; set { if (value == AutoDeafen) return; _settings.AutoDeafen = value; Update(); OnPropertyChanged(); } }
    public string DeafenKey { get => _settings.DeafenKey; set { if (value == DeafenKey) return; _settings.DeafenKey = value; Update(); OnPropertyChanged(); } }
    public decimal? DeafenPercent
    {
        get => _settings.DeafenPercent is >= 1 and <= 100 ? _settings.DeafenPercent : 50;
        set
        {
            if (value is null || value < 1 || value > 100 || value != decimal.Truncate(value.Value) || value == DeafenPercent) return;
            _settings.DeafenPercent = (int)value.Value; Update(); OnPropertyChanged();
        }
    }
    public string RegionLabel => _settings.Region is null ? "Select bar" : "Reselect bar";
    public HealthRegion? Region => _settings.Region;
    public double? Percent => !_disposed && Environment.TickCount64 - _lastGoodSample < 1500 ? _percent : null;
    public HealthSample Sample => Percent is not null ? _sample : new(null, "Health unavailable");
    public string Reading => _percent is double percent ? $"{percent:0}%" : "N/A";
    public bool HasReading => _percent.HasValue;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsArmed => _armed;
    public bool IsReady => Enabled && _settings.Region is not null && (!AutoDeafen || ValidKey);
    private bool ValidKey => !string.IsNullOrWhiteSpace(DeafenKey) && MacroKeys.TryVirtualKey(DeafenKey, out _) && MacroKeys.Normalize(DeafenKey) != "esc" && !DeafenKey.StartsWith("mouse", StringComparison.OrdinalIgnoreCase);
    public void SetPreview(bool preview) { _preview = preview; RefreshMonitor(); }
    public void SetArmed(bool armed)
    {
        StopInputs(); _threshold.Reset(); _fired = false;
        lock (_inputGate) _armed = armed && IsReady && _pendingKey is null;
        if (armed && !_armed && Fault is null) Status = _settings.Region is null ? "Select health bar" : "Set a Discord deafen key";
        RefreshMonitor(); OnPropertyChanged(nameof(IsArmed));
        if (_discord is null) DeafenStatus = _armed ? "Ready" : "Paused";
    }
    private void RefreshMonitor()
    {
        _recoveryCandidate = null; _lastGoodSample = Environment.TickCount64;
        _monitor.Stop(); _generation++; _percent = null; OnPropertyChanged(nameof(Reading)); OnPropertyChanged(nameof(HasReading));
        if (!_disposed && ShouldTrack && _settings.Region is not null)
        {
            try { _monitor.Start(_settings.Region); Status = Fault ?? "Waiting for Roblox"; }
            catch (Exception ex) { _armed = false; Status = "Health capture: " + ex.Message; }
        }
    }
    private void Receive(HealthSample sample)
    {
        _sample = sample; _percent = sample.Percent; OnPropertyChanged(nameof(Reading)); OnPropertyChanged(nameof(HasReading));
        if (sample.Percent is not null) _lastGoodSample = Environment.TickCount64;
        if (Fault is not null) { Status = Fault; return; }
        Status = sample.Status;
        if (sample.Percent is not null) { _lastGoodSample = Environment.TickCount64; _recoveryCandidate = null; }
        else if (ShouldTrack && _input.IsForegroundRoblox && !_recovering &&
            Environment.TickCount64 - _lastGoodSample >= 1000 && Environment.TickCount64 - _lastRecovery >= 2000)
            _ = RecoverAsync();
        if (!_armed || !AutoDeafen || !_input.IsForegroundRoblox) return;
        // Re-arm after genuine recovery, including after settings edits reset the
        // threshold detector. Never press the toggle just because health recovered.
        if (sample.Percent is double percent && double.IsFinite(percent) &&
            percent <= 100 && percent > Math.Min(100, (int)DeafenPercent!.Value + 5))
        {
            _fired = false;
            if (_confirmation is null && _discord is null) DeafenStatus = "Ready";
        }
        if (_confirmation is not null) return;
        var transition = _threshold.Observe(sample, (int)DeafenPercent!.Value);
        if (transition == HealthThresholdTransition.Low && !_fired)
        {
            _fired = true;
            _ = ConfirmDeafenAsync();
        }

    }
    private async Task ConfirmDeafenAsync()
    {
        var session = new CancellationTokenSource();
        _confirmation = session;
        DeafenStatus = _discord is null ? "Sending…" : "Checking…";
        try
        {
            var before = _discord is null ? null : await _discord.ReadAsync(session.Token);
            if (session.IsCancellationRequested || !_armed) return;
            if (before == true) { DeafenStatus = "Already deafened"; return; }
            if (!await SendDiscordShortcutAsync(session.Token)) { DeafenStatus = _discord is null ? "Keybind failed" : "Unconfirmed"; _fired = false; return; }
            if (session.IsCancellationRequested || !_armed) return;
            if (_discord is null) { DeafenStatus = "Keybind sent"; return; }
            DeafenStatus = "Confirming…";
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(150, session.Token);
                var after = await _discord.ReadAsync(session.Token);
                if (session.IsCancellationRequested || !_armed) return;
                if (after == true) { DeafenStatus = "Deafen confirmed"; return; }
            }
            DeafenStatus = "Unconfirmed";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!session.IsCancellationRequested) DeafenStatus = "Unconfirmed"; }
        finally { if (ReferenceEquals(_confirmation, session)) _confirmation = null; session.Dispose(); }
    }

    private async Task<bool> SendDiscordShortcutAsync(CancellationToken cancellationToken)
    {
        var owner = new object();
        var success = false;
        lock (_inputGate)
        {
            if (!_armed || !ValidKey || !_input.IsAvailable || !_input.IsForegroundRoblox || _pendingKey is not null) { _threshold.Reset(); return false; }
            var key = MacroKeys.Normalize(DeafenKey);
            try { _pressOwner = owner; _pendingKey = key; _input.Key(key, true); success = true; }
            catch (Exception ex) { Fault = "Discord shortcut: " + ex.Message; _armed = false; }
        }
        try { if (success) await Task.Delay(100, cancellationToken); }
        finally
        {
            lock (_inputGate)
            {
                if (ReferenceEquals(_pressOwner, owner) && !ReleaseKey()) success = false;
                if (Fault is not null) { Status = Fault; OnPropertyChanged(nameof(IsArmed)); }
            }
        }
        return success;
    }
    private bool ReleaseKey()
    {
        if (_pendingKey is null) return true;
        try { _input.Key(_pendingKey, false); _pendingKey = null; _pressOwner = null; return true; }
        catch (Exception ex) { _armed = false; Fault = "Could not release Discord key: " + ex.Message; return false; }
    }
    public async Task SelectRegionAsync(Window owner, bool automatic = false)
    {
        var resume = _resumeRequested();
        var fired = _fired;
        _changed(); SetArmed(false); _monitor.Stop();
        owner.Hide();
        try
        {
            var region = automatic ? await HudAutoDetection.PickHealthAsync(owner) : await HealthRegionPicker.PickAsync(owner);
            if (region is not null)
            {
                ApplyDetectedRegion(region);
            }
        }
        catch (Exception ex) { Status = "Could not detect health bar: " + ex.Message; }
        finally
        {
            ResumeAfterRegionSelection(resume, fired);
            owner.Show(); owner.Activate();
        }
    }
    private void ResumeAfterRegionSelection(bool resume, bool fired)
    {
        ResumeAfterEdit(resume, fired);
    }
    private async Task RecoverAsync()
    {
        _recovering = true; _lastRecovery = Environment.TickCount64;
        var generation = _generation;
        try
        {
            var found = await Task.Run(_recover);
            if (_disposed || generation != _generation || !ShouldTrack ||
                !_input.IsForegroundRoblox || _percent is not null) return;
            if (found is null || !HudBarCapture.SameLocation(found, _recoveryCandidate)) { _recoveryCandidate = found; return; }
            Recovered?.Invoke(found);
            ApplyDetectedRegion(found);
        }
        catch (Exception) { _recoveryCandidate = null; }
        finally { _recovering = false; }
    }
    public void ApplyDetectedRegion(HealthRegion region)
    {
        region.Validate(); _settings.Region = region;
        Update(); OnPropertyChanged(nameof(RegionLabel));
    }
    private void Update(bool passiveCalibration = false)
    {
        var resume = _resumeRequested();
        var fired = _fired;
        if (passiveCalibration) RefreshMonitor();
        else { _changed(); SetArmed(false); }
        if (_storageBlocked) { Status = "Health settings unavailable; existing file preserved"; return; }
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) { Status = "Could not save health settings: " + ex.Message; }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(ShowDeafenStatus));
        if (!passiveCalibration) ResumeAfterEdit(resume, fired);
    }
    private void ResumeAfterEdit(bool resume, bool fired)
    {
        if (!resume) return;
        if (!IsReady)
        {
            return;
        }
        _resumeAfterEdit();
        _fired = fired;
        if (_discord is null) DeafenStatus = _armed ? fired ? "Keybind sent" : "Ready" : "Paused";
    }
    internal void PreserveDeafenLatch(Action reconfigure)
    {
        var fired = _fired;
        try { reconfigure(); }
        finally
        {
            _fired |= fired;
            if (_discord is null && _armed && _fired) DeafenStatus = "Keybind sent";
        }
    }
    public void StopInputs()
    {
        try { _confirmation?.Cancel(); } catch (ObjectDisposedException) { }
        DeafenStatus = _discord is null ? "Paused" : "Unconfirmed";
        lock (_inputGate) { _armed = false; if (ReleaseKey()) Fault = null; }
        _monitor.Stop(); _generation++;
        if (Fault is not null) Status = Fault;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; StopInputs(); _monitor.Dispose(); _textReader?.Dispose(); _input.Dispose(); }
    public sealed class Settings
    {
        public bool Enabled { get; set; }
        public bool AutoDeafen { get; set; }
        public int DeafenPercent { get; set; } = 50;
        public string DeafenKey { get; set; } = "";
        public HealthRegion? Region { get; set; }
    }
}
