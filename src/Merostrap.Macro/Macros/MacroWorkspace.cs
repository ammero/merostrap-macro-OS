using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using LucideAvalonia.Enum;
using Merostrap.UI.Elements.Macros;

namespace Merostrap.Macros;

/// <summary>One shared session for settings, the editor, and the floating overlay.</summary>
public sealed class MacroWorkspace : ObservableObject, IDisposable
{
    private static MacroWorkspace? _current;
    public static MacroWorkspace Current => _current ??= new MacroWorkspace(Path.Combine(StandalonePaths.ConfigRoot, "Macros.json"), singleInstance: true);
    public static void Shutdown() => _current?.Dispose();
    public static bool HasBackgroundSession => _current is { _disposed: false, _sessionStopped: false } workspace &&
        (workspace._backgroundSessionRequested || workspace._sessionRequested || workspace._enabled);
    // Hard-exit paths may run off the UI thread: release input without touching windows.
    public static void StopInputs() { _current?._shortcuts?.Dispose(); _current?._engine?.Dispose(); _current?.Health.StopInputs(); _current?.ParryBar.StopCapture(); _current?.DodgeBar.StopCapture(); }
    public bool CanEnableMacros => !_disposed;
    private readonly MacroStore _store;
    private readonly MacroPreferences _preferences;
    private readonly Func<MacroEngine> _engineFactory;
    private readonly Func<bool> _isRobloxForeground;
    private readonly bool _useNativeShortcuts;
    private nint _guiWindowHandle;
    private RitualPromptOverlay? _ritualPromptOverlay;
    private readonly DispatcherTimer _overlayFocus = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _overlayContext;
    private MacroEngine? _engine;
    private MacroWindow? _window;
    private MacroOverlay? _overlay;
    private bool _enabled;
    private bool _sessionRequested;
    private bool _backgroundSessionRequested;
    private bool _sessionStopped;
    private int _settingsEditDepth;
    private bool _paused = true;
    private WindowsMacroInput? _shortcuts;
    private readonly HashSet<string> _shortcutKeysDown = new();
    private bool _disposed;
    private readonly bool _singleInstance;
    private MacroSessionLease? _sessionLease;
    private readonly DispatcherTimer _openRequests = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool OwnSession(bool requestOpen = false)
    {
        if (_disposed) return false;
        if (!_singleInstance || !OperatingSystem.IsWindows()) return true;
        Dispatcher.UIThread.VerifyAccess();
        _sessionLease ??= new MacroSessionLease();
        if (_sessionLease.TryAcquire()) { _openRequests.Start(); return true; }
        if (requestOpen) _sessionLease.RequestOpen();
        Status = "The macro is already open.";
        return false;
    }
    private string _search = "";
    private string _category = "Combat";
    private string _status = "Off · enable macros with F8";
    private string _saveStatus = "Changes save locally";
    private readonly List<MacroRow> _rows = [];

    public MacroWorkspace(string path, Func<MacroEngine>? engineFactory = null, Health.HealthMonitor? parryMonitor = null, Func<bool>? isRobloxForeground = null, bool singleInstance = false, Health.HealthMonitor? dodgeMonitor = null)
    {
        _singleInstance = singleInstance;
        _useNativeShortcuts = singleInstance && engineFactory is null;
        _openRequests.Tick += (_, _) => ProcessSessionRequests();
        _isRobloxForeground = isRobloxForeground ?? Merostrap.Macros.Health.WindowsHealthCapture.IsForegroundRoblox;
        _overlayFocus.Tick += (_, _) => RefreshOverlayVisibility();
        _engineFactory = engineFactory ?? (() => new MacroEngine(new WindowsMacroInput(), () => new WindowsRitualPromptSource(), windowCapture: GankWindowCapture.CaptureAsync, charismaSourceFactory: () => new WindowsCharismaPromptSource(), clipboardSetter: WindowsClipboardText.CopyAsync));
        _store = new MacroStore(path);
        try { _preferences = _store.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _preferences = new MacroPreferences();
            _status = "Could not load macro settings: " + ex.Message;
            _saveStatus = "Storage unavailable";
        }
        RobloxDisplay.SelectedDevice = _preferences.DisplayDevice; RefreshDisplays();
        if (_store.LastLoadWarning is { Length: > 0 } warning) _status = warning;
        if (!ValidGuiKey(_preferences.GuiKeybind)) _preferences.GuiKeybind = "f10";
        else _preferences.GuiKeybind = MacroKeys.Normalize(_preferences.GuiKeybind);
        Profiles = new ObservableCollection<string>(_preferences.Profiles.Select(p => p.Name));
        Categories = MacroCatalog.All.Select(m => m.Category).Distinct().Append("Health").Append("Parry Bar").Append("Dodge Bar").Append("Settings").ToArray();
        if (!GuiThemes.Contains(_preferences.GuiTheme)) _preferences.GuiTheme = "Obsidian";
        ParryBar = new ParryBarController(_preferences.ParryBar, Save, () =>
        {
            var resume = _sessionRequested || ParryBar!.Enabled;
            Health!.PreserveDeafenLatch(() => { Enabled = false; if (resume) Enabled = true; });
            RestoreOverlay();
        }, parryMonitor);
        DodgeBar=new DodgeBarController(_preferences.DodgeBar,Save,()=>{
            bool resume=_sessionRequested||DodgeBar!.Enabled;
            Health!.PreserveDeafenLatch(()=>{Enabled=false;if(resume)Enabled=true;});RestoreOverlay();
        },dodgeMonitor);
        FlowStateOverlay = new FlowStateOverlayController(_preferences.FlowStateOverlay ??= new(), Save, RestoreOverlay, _useNativeShortcuts);
        Health = new HealthAutomation(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "Health.json"), HealthChanged, resumeAfterEdit: ResumeAfterEdit, resumeRequested: () => _sessionRequested);
        Health.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Health.IsArmed)) { SetProperty(ref _enabled, _engine?.IsRunning == true || Health.IsArmed || ParryBar.IsRunning || DodgeBar.IsRunning, nameof(Enabled)); OnPropertyChanged(nameof(SessionLabel)); } if (Health.Fault is not null) Status = Health.Fault; else if (Enabled && _engine?.IsRunning != true && e.PropertyName == nameof(Health.Status)) Status = Health.Status; };
        OpenWindowCommand = new RelayCommand(OpenWindow);
        ToggleOverlayCommand = new RelayCommand(() => OverlayVisible = !OverlayVisible);
        ToggleLockCommand = new RelayCommand(() => OverlayLocked = !OverlayLocked);
        ResetPositionCommand = new RelayCommand(ResetPosition);
        LoadRows();
        if (_useNativeShortcuts) StartShortcuts();
    }

    public ObservableCollection<string> Profiles { get; }
    public HealthAutomation Health { get; }
    public DodgeBarController DodgeBar { get; }
    public ParryBarController ParryBar { get; }
    public FlowStateOverlayController FlowStateOverlay { get; }
    public IReadOnlyList<string> Categories { get; }
    public IReadOnlyList<string> GuiThemes { get; } = new[] { "Obsidian", "Midnight", "Crimson", "Evergreen", "Amethyst" };
    public bool ReduceMotion
    {
        get => _preferences.ReduceMotion;
        set { if (value == ReduceMotion) return; _preferences.ReduceMotion = value; Save(); OnPropertyChanged(); }
    }
    public string GuiTheme
    {
        get => _preferences.GuiTheme;
        set
        {
            if (!GuiThemes.Contains(value) || value == _preferences.GuiTheme) return;
            _preferences.GuiTheme = value; OnPropertyChanged(); Save();
            if (_sessionLease is { IsOwner: false }) _sessionLease.RequestGuiKeyUpdate();
        }
    }
    public ObservableCollection<MacroRow> VisibleMacros { get; } = [];
    public IRelayCommand OpenWindowCommand { get; }
    public IRelayCommand ToggleOverlayCommand { get; }
    public IRelayCommand ToggleLockCommand { get; }
    public IRelayCommand ResetPositionCommand { get; }
    public bool RuntimeAvailable => OperatingSystem.IsWindows();
    public string GuiKeybind
    {
        get => _preferences.GuiKeybind;
        set
        {
            value = MacroKeys.Normalize(value);
            if (!ValidGuiKey(value)) { Status = "Choose one GUI key other than Escape, F8 or F9."; OnPropertyChanged(); return; }
            if (value == _preferences.GuiKeybind) return;
            ApplyGuiKeybind(value);
            Save();
            if (_singleInstance && _sessionLease?.IsOwner == false) _sessionLease.RequestGuiKeyUpdate();
        }
    }
    private void ApplyGuiKeybind(string value)
    {
        _preferences.GuiKeybind = value;
        if (_engine is not null) _engine.InterfaceKey = value;
        _shortcuts?.Dispose(); _shortcuts = null; _shortcutKeysDown.Clear();
        if (_useNativeShortcuts) StartShortcuts();
        OnPropertyChanged(nameof(GuiKeybind));
    }
    private static bool ValidGuiKey(string? value) => value is not null &&
        (value.Length == 0 || (MacroKeys.TryVirtualKey(value, out _) && MacroKeys.Normalize(value) is not ("esc" or "f8" or "f9")));
    public void ToggleGui()
    {
        if (_window?.IsPanelOpen == true && _window.WindowState != Avalonia.Controls.WindowState.Minimized) _window.HidePanel();
        else OpenWindow(activate: false);
    }
    public bool FunctionalityEnabled
    {
        get => !_paused && CanEnableMacros;
        set
        {
            if (value && !CanEnableMacros) { Status = "Session closed"; OnPropertyChanged(); return; }
            if (value == FunctionalityEnabled) return;
            _paused = !value;
            Health.PreserveDeafenLatch(() => Enabled = value);
            OnPropertyChanged();
        }
    }
    public bool FasterHoldEnabled
    {
        get => _rows.First(r => r.Definition.Id == "hold-m1").Enabled;
        set => _rows.First(r => r.Definition.Id == "hold-m1").Enabled = value;
    }
    private void StartShortcuts()
    {
        if (!_useNativeShortcuts || _shortcuts is not null) return;
        if (!OwnSession()) return;
        // Test workspaces use a fake engine and must never install desktop hooks.
        var keys = new HashSet<string> { "f8", "f9" };
        if (GuiKeybind.Length > 0) keys.Add(GuiKeybind);
        var input = new WindowsMacroInput { GlobalKeys = keys };
        input.Input += e => Dispatcher.UIThread.Post(() => HandleShortcut(e));
        try { input.Start(e => !e.IsInjected && e.Key == GuiKeybind &&
            (_isRobloxForeground() || (_guiWindowHandle != nint.Zero && GetForegroundWindow() == _guiWindowHandle))); _shortcuts = input; }
        catch (Exception ex) { input.Dispose(); Status = "Shortcut unavailable: " + ex.Message; }
    }
    public void HandleShortcut(MacroInputEvent input)
    {
        if (_disposed || input.IsInjected || (input.Key is not ("f8" or "f9") && input.Key != GuiKeybind)) return;
        if (!input.IsDown) { _shortcutKeysDown.Remove(input.Key); return; }
        if (!_shortcutKeysDown.Add(input.Key) || KeybindButton.SuppressShortcuts) return;
        if (GuiKeybind.Length > 0 && input.Key == GuiKeybind)
        {
            if (_settingsEditDepth > 0) return;
            if (_isRobloxForeground() || _window?.IsActive == true) ToggleGui();
            return;
        }
        if (_sessionStopped) return;
        if (input.Key == "f8") FunctionalityEnabled = !FunctionalityEnabled;
        else FasterHoldEnabled = !FasterHoldEnabled;
    }
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value && !CanEnableMacros) { Status = "Session closed"; OnPropertyChanged(); return; }
            if (value && _sessionStopped) return;
            if (value && !OwnSession(true)) return;
            if (value) _backgroundSessionRequested = true;
            _sessionRequested = value;
            if (value && (_paused || _settingsEditDepth > 0)) return;
            if (value) { StartShortcuts(); RestoreOverlay(); }
            if (_enabled == value && value) return;
            if (value)
            {
                if (!RuntimeAvailable) { Status = "Input macros are available on Windows."; return; }
                var hasMacros = _rows.Any(row => row.Enabled && row.Supported);
                if (!hasMacros && !Health.IsReady && !ParryBar.IsReady && !DodgeBar.IsReady) { Status = Health.Enabled ? "Select the health bar and configure its Discord key." : "Choose at least one available macro first."; OnPropertyChanged(); return; }
                try
                {
                    if (hasMacros)
                    {
                        _engine ??= CreateEngine();
                        _engine.Start(_preferences.Profiles[SelectedProfileIndex]);
                    }
                    if (Health.Enabled) Health.SetArmed(true);
                    ParryBar.SetArmed(true);
                    DodgeBar.SetArmed(true);
                    SetProperty(ref _enabled, _engine?.IsRunning == true || Health.IsArmed || ParryBar.IsRunning || DodgeBar.IsRunning);
                    OnPropertyChanged(nameof(Enabled));
                    Status = hasMacros && _engine?.IsRunning != true ? _engine!.Status : _engine?.IsRunning == true ? _engine.Status : Health.Status;
                }
                catch (Exception ex)
                {
                    _engine?.Stop();
                    Health.SetArmed(false);
                    ParryBar.SetArmed(false);
                DodgeBar.SetArmed(false);
                    Status = "Could not enable macros: " + ex.Message;
                    OnPropertyChanged();
                }
            }
            else
            {
                _engine?.Stop();
                Health.SetArmed(false);
                ParryBar.SetArmed(false);
                DodgeBar.SetArmed(false);
                SetProperty(ref _enabled, false);
                Status = _engine?.Status is { } stopStatus && stopStatus != "Off"
                    ? stopStatus : Health.Fault ?? "Off · enable macros in Merostrap";
            }
            OnPropertyChanged(nameof(SessionLabel));
        }
    }
    private MacroEngine CreateEngine()
    {
        var engine = _engineFactory();
        engine.InterfaceKey = GuiKeybind;
        engine.RitualDiagnostic += message => Trace.TraceInformation("Ritual trace: t="+Environment.TickCount64+"; "+message);
        var profile = _preferences.Profiles[_preferences.SelectedProfile];
        engine.RitualAreaDetected += area => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !ReferenceEquals(_engine, engine) ||
                !ReferenceEquals(profile, _preferences.Profiles[_preferences.SelectedProfile])) return;
            // Persist calibration without restarting the active tracker and replaying its pending key.
            _rows.Single(r => r.IsRitual).SetRitualArea(area, restart: false);
            Save();
        });
        engine.RitualPromptChanged += prompt => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !ReferenceEquals(_engine,engine)) return;
            if (prompt is null || !Enabled) { _ritualPromptOverlay?.Hide(); return; }
            (_ritualPromptOverlay ??= new RitualPromptOverlay()).Update(prompt);
        });
        engine.StatusChanged += message => Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !Enabled) return;
            SetProperty(ref _enabled, engine.IsRunning || Health.IsArmed || ParryBar.IsRunning || DodgeBar.IsRunning, nameof(Enabled));
            Status = message;
            OnPropertyChanged(nameof(SessionLabel));
        });
        return engine;
    }
    public void ReportError(string message) => Status = message;
    public IDisposable PauseForSettingsEdit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_settingsEditDepth++ == 0)
        {
            var requested = _sessionRequested;
            Health.PreserveDeafenLatch(() => { Enabled = false; _sessionRequested = requested; });
        }
        return new SettingsEdit(this);
    }
    private sealed class SettingsEdit(MacroWorkspace workspace) : IDisposable
    {
        private MacroWorkspace? _workspace = workspace;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _workspace, null);
            if (owner is null || owner._disposed) return;
            if (--owner._settingsEditDepth == 0 && owner._sessionRequested)
                owner.Health.PreserveDeafenLatch(owner.ResumeAfterEdit);
        }
    }
    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(DisplayStatus)); } }
    public string DisplayStatus => Status.StartsWith("Off", StringComparison.Ordinal) || Status.StartsWith("Settings changed", StringComparison.Ordinal) ? "Off" : Status;
    public string SaveStatus { get => _saveStatus; private set => SetProperty(ref _saveStatus, value); }
    public string SessionLabel => Enabled ? "ENABLED" : "OFF";
    public string Search { get => _search; set { if (SetProperty(ref _search, value)) { Filter(); OnPropertyChanged(nameof(IsHealthPage)); OnPropertyChanged(nameof(IsParryPage)); OnPropertyChanged(nameof(IsDodgePage)); OnPropertyChanged(nameof(IsSettingsPage)); OnPropertyChanged(nameof(IsMacroPage)); OnPropertyChanged(nameof(IsSilentheartPage)); Health.SetPreview(IsHealthPage); } } }
    private bool HasSearch => !string.IsNullOrWhiteSpace(Search);
    public string SelectedCategory { get => _category; set { if (SetProperty(ref _category, value)) { if(value=="Settings")RefreshDisplays(); Filter(); OnPropertyChanged(nameof(IsHealthPage)); OnPropertyChanged(nameof(IsParryPage)); OnPropertyChanged(nameof(IsDodgePage)); OnPropertyChanged(nameof(IsSettingsPage)); OnPropertyChanged(nameof(IsMacroPage)); OnPropertyChanged(nameof(IsSilentheartPage)); Health.SetPreview(IsHealthPage); } } }
    public bool IsSilentheartPage => !HasSearch && SelectedCategory == "Silentheart";
    public bool IsHealthPage => !HasSearch && SelectedCategory == "Health";
    public bool IsDodgePage => !HasSearch && SelectedCategory == "Dodge Bar";
    public bool IsParryPage => !HasSearch && SelectedCategory == "Parry Bar";
    public bool IsSettingsPage => !HasSearch && SelectedCategory == "Settings";
    public bool IsMacroPage => !IsHealthPage && !IsParryPage && !IsDodgePage && !IsSettingsPage;
    public bool IsEmpty => IsMacroPage && VisibleMacros.Count == 0;
    public string ResultCount => $"{VisibleMacros.Count} macros";
    public IReadOnlyList<MacroBindingSummary> OverlayBindings => _rows.Where(r => r.Enabled && r.Supported).Select(r =>
    {
        if (r.IsRitual) return new MacroBindingSummary(r.Name, "Automatic");
        var bind = r.HeaderKey;
        if (bind is not null) return new MacroBindingSummary(r.Name, string.IsNullOrWhiteSpace(bind.Value) ? "Unset" : bind.Value.ToUpperInvariant());
        try { var workflow = MacroWorkflows.Create(r.Definition.Id, r.Fields.ToDictionary(f => f.Key, f => f.Value)); return new MacroBindingSummary(r.Name, string.Join(" / ", workflow.Triggers.Select(k => k == "primary" ? "M1" : k.ToUpperInvariant())) is { Length: > 0 } keys ? keys : "Typing"); }
        catch (ArgumentException) { return new MacroBindingSummary(r.Name, "Unset"); }
        catch (FormatException) { return new MacroBindingSummary(r.Name, "Unset"); }
        catch (OverflowException) { return new MacroBindingSummary(r.Name, "Unset"); }
    }).Concat(Health.Enabled && Health.AutoDeafen
        ? new[] { new MacroBindingSummary("Auto Deafen", string.IsNullOrWhiteSpace(Health.DeafenKey) ? "Unset" : Health.DeafenKey.ToUpperInvariant()) }
        : Array.Empty<MacroBindingSummary>()).ToArray();
    public bool NoOverlayBindings => OverlayBindings.Count == 0;
    public string EnabledCount => $"{_rows.Count(r => r.Enabled)} selected";
    public int SelectedProfileIndex
    {
        get => _preferences.SelectedProfile;
        set
        {
            if (value < 0 || value >= Profiles.Count || value == SelectedProfileIndex) return;
            var resume = _sessionRequested;
            Health.PreserveDeafenLatch(() =>
            {
                Enabled = false;
                _preferences.SelectedProfile = value;
                LoadRows(); Save();
                if (resume) ResumeAfterEdit();
            });
            OnPropertyChanged(); OnPropertyChanged(nameof(PresetName));
            OnPropertyChanged(nameof(FasterHoldEnabled));
        }
    }
    public string PresetName
    {
        get => _preferences.Profiles[SelectedProfileIndex].Name;
        set
        {
            if (value == PresetName) return;
            _preferences.Profiles[SelectedProfileIndex].Name = value;
            Profiles[SelectedProfileIndex] = value;
            Save(); OnPropertyChanged();
        }
    }
    public bool OverlayVisible
    {
        get => _preferences.OverlayVisible;
        set
        {
            if (value == OverlayVisible && (!value || _overlay != null)) return;
            _preferences.OverlayVisible = value;
            Save(); OnPropertyChanged();
            RestoreOverlay();
        }
    }
    public ObservableCollection<MacroDisplayOption> DisplayOptions {get;}=[];
    public MacroDisplayOption? SelectedDisplay
    {
        get=>DisplayOptions.FirstOrDefault(d=>d.Id==_preferences.DisplayDevice);
        set
        {
            if(value is null || value.Id==_preferences.DisplayDevice)return;
            _preferences.DisplayDevice=value.Id;RobloxDisplay.SelectedDevice=value.Id;Save();OnPropertyChanged();
        }
    }
    public void RefreshDisplays()
    {
        DisplayOptions.Clear();foreach(var option in RobloxDisplay.Options())DisplayOptions.Add(option);
        if(!DisplayOptions.Any(d=>d.Id==_preferences.DisplayDevice))DisplayOptions.Add(new(_preferences.DisplayDevice,"Display disconnected"));
        OnPropertyChanged(nameof(SelectedDisplay));
    }
    public bool OverlayLocked
    {
        get => _preferences.OverlayLocked;
        set { if (value == OverlayLocked) return; _preferences.OverlayLocked = value; Save(); OnPropertyChanged(); OnPropertyChanged(nameof(LockIcon)); OnPropertyChanged(nameof(LockLabel)); }
    }
    public LucideIconNames LockIcon => OverlayLocked ? LucideIconNames.LockKeyhole : LucideIconNames.LockKeyholeOpen;
    public string LockLabel => OverlayLocked ? "Unlock overlay position" : "Lock overlay position";
    public int? OverlayX => _preferences.OverlayX;
    public int? GuiX => _preferences.GuiX;
    public int? GuiY => _preferences.GuiY;
    public void SaveGuiPosition(int x, int y) { _preferences.GuiX = x; _preferences.GuiY = y; Save(); }
    public int? OverlayY => _preferences.OverlayY;
    public void SaveOverlayPosition(int x, int y) { _preferences.OverlayX = x; _preferences.OverlayY = y; Save(); }
    public void RestoreOverlay()
    {
        if (!OwnSession()) return;
        if (OverlayVisible || ParryBar.Enabled || DodgeBar.Enabled || FlowStateOverlay.Enabled) _overlayFocus.Start();
        else _overlayFocus.Stop();
        RefreshOverlayVisibility();
    }
    public void RefreshOverlayVisibility()
    {
        if (_disposed || !OwnSession()) return;
        if (!CanEnableMacros) {
            _overlayFocus.Stop(); _overlayContext = false; _overlay?.Hide();
            ParryBar.SetForeground(false); DodgeBar.SetForeground(false); FlowStateOverlay.SetForeground(false);
            return;
        }
        if (!OverlayVisible && !ParryBar.Enabled && !DodgeBar.Enabled && !FlowStateOverlay.Enabled)
        {
            _overlayFocus.Stop();
            _overlayContext = false;
            _overlay?.Hide();
            ParryBar.SetForeground(false);
        DodgeBar.SetForeground(false);
        FlowStateOverlay.SetForeground(false);
            return;
        }
        var foreground = _isRobloxForeground();
        // Clicking or dragging an overlay is still part of the Roblox interaction.
        // Other Merostrap windows and other applications never qualify.
        if (!foreground && _overlayContext && OperatingSystem.IsWindows())
        {
            var handle = GetForegroundWindow();
            foreground = handle != IntPtr.Zero &&
                ((_overlay?.IsVisible == true && _overlay.TryGetPlatformHandle()?.Handle == handle) || ParryBar.IsOverlayForeground(handle) || DodgeBar.IsOverlayForeground(handle));
        }
        if(RobloxDisplay.SelectedDevice.Length>0 && RobloxDisplay.TargetBounds is null)foreground=false;
        _overlayContext = foreground;
        if (foreground && OverlayVisible) ShowOverlay(); else _overlay?.Hide();
        ParryBar.SetForeground(foreground);
        DodgeBar.SetForeground(foreground);
        FlowStateOverlay.SetForeground(foreground);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    private void ShowOverlay()
    {
        if (_overlay is null)
        {
            _overlay = new MacroOverlay(this);
            _overlay.Closed += (_, _) => _overlay = null;
        }
        if (!_overlay.IsVisible) _overlay.Show();
    }
    private void ResetPosition()
    {
        _preferences.OverlayX = null; _preferences.OverlayY = null; Save();
        _overlay?.ResetPosition();
    }
    public void OpenWindow() => OpenWindow(activate: true);
    private void OpenWindow(bool activate)
    {
        if (!OwnSession(true)) return;
        StartShortcuts();
        if (_window is null)
        {
            _window = new MacroWindow(this);
            _window.Closed += (_, _) => { _window = null; _guiWindowHandle = nint.Zero; Health.SetPreview(false); };
        }
        _window.ShowPanel(activate);
        _guiWindowHandle = _window.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        Health.SetPreview(IsHealthPage);
        if (_window.WindowState == Avalonia.Controls.WindowState.Minimized) _window.WindowState = Avalonia.Controls.WindowState.Normal;
        RestoreOverlay();
    }
    public void OpenForRobloxLaunch(bool launchEnabled)
    {
        if (!launchEnabled || !RuntimeAvailable) return;
        OpenWindow();
        StartSession();
    }
    public void StopSession()
    {
        if (_disposed) return;
        _backgroundSessionRequested = false;
        _sessionStopped = true;
        if (_singleInstance && OperatingSystem.IsWindows())
        {
            _sessionLease ??= new MacroSessionLease();
            if (!_sessionLease.IsOwner) _sessionLease.RequestStop();
        }
        Enabled = false;
        _shortcutKeysDown.Clear();
        _overlayFocus.Stop();
        _window?.Close(); _overlay?.Close();
        _ritualPromptOverlay?.Close(); _ritualPromptOverlay=null;
        ParryBar.SetForeground(false);
        DodgeBar.SetForeground(false);
        FlowStateOverlay.SetForeground(false);
    }
    private long _lastHudDiagnostic;
    internal void ProcessSessionRequests()
    {
        if(Environment.GetEnvironmentVariable("MEROSTRAP_HUD_DIAGNOSTICS")=="1" && Environment.TickCount64-_lastHudDiagnostic>2000) {
            _lastHudDiagnostic=Environment.TickCount64;
            var fg=GetForegroundWindow();GetWindowThreadProcessId(fg,out var pid);
            string name;try{using var process=System.Diagnostics.Process.GetProcessById((int)pid);name=process.ProcessName;}catch{name="unavailable";}
            Trace.WriteLine($"HUD diagnostics: pid={pid}/{name}; foreground={_isRobloxForeground()}; context={_overlayContext}; focusTimer={_overlayFocus.IsEnabled}; enabled={Enabled}; requested={_sessionRequested}; editing={_settingsEditDepth}; access={CanEnableMacros}; dodge={DodgeBar.IsRunning}/{DodgeBar.Status}/{DodgeBar.Fill}; parry={ParryBar.IsRunning}/{ParryBar.Status}/{ParryBar.Fill}");
        }
        if (_sessionLease?.TakeGuiKeyUpdate() == true)
        {
            try
            {
                var preferences = _store.Load();
                var key = preferences.GuiKeybind;
                if (ValidGuiKey(key)) ApplyGuiKeybind(MacroKeys.Normalize(key));
                if (GuiThemes.Contains(preferences.GuiTheme)) { _preferences.GuiTheme = preferences.GuiTheme; OnPropertyChanged(nameof(GuiTheme)); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Status = "Could not update GUI hotkey: " + ex.Message; }
        }
        bool stopRequested = _sessionLease?.TakeStopRequest() == true;
        bool startRequested = _sessionLease?.TakeStartRequest() == true;
        if (stopRequested || startRequested)
        {
            _sessionLease!.TakeOpenRequest();
            if (stopRequested) StopSession();
            else StartSession();
        }
        else if (_sessionLease?.TakeOpenRequest() == true) OpenWindow();
    }
    public void StartSession()
    {
        if (_disposed) return;
        _paused = false; OnPropertyChanged(nameof(FunctionalityEnabled));
        if (_singleInstance && OperatingSystem.IsWindows())
        {
            _sessionLease ??= new MacroSessionLease();
            if (!_sessionLease.TryAcquire()) { _sessionLease.RequestStart(); return; }
            _openRequests.Start();
        }
        _sessionStopped = false;
        Enabled = true;
    }
    private void LoadRows()
    {
        _rows.Clear();
        var profile = _preferences.Profiles[SelectedProfileIndex];
        foreach (var definition in MacroCatalog.All)
        {
            if (!profile.Entries.TryGetValue(definition.Id, out var entry)) profile.Entries[definition.Id] = entry = new MacroEntry();
            _rows.Add(new MacroRow(definition, entry, () =>
            {
                if (entry.Enabled)
                {
                    var other = definition.Id switch { "auto-uppercut" => "dynamic-auto-uppercut", "dynamic-auto-uppercut" => "auto-uppercut", "bell-stack-dodge" => "bell-stack-parry", "bell-stack-parry" => "bell-stack-dodge", _ => null };
                    if (other is not null && _rows.FirstOrDefault(r => r.Definition.Id == other) is { Enabled: true } row) row.Enabled = false;
                }
                Changed();
            }));
        }
        Filter(); OnPropertyChanged(nameof(EnabledCount)); OnPropertyChanged(nameof(OverlayBindings)); OnPropertyChanged(nameof(NoOverlayBindings));
    }
    private void Changed()
    {
        var resume = _sessionRequested;
        Health.PreserveDeafenLatch(() =>
        {
            HealthChanged();
            if (resume) ResumeAfterEdit();
        });
    }
    private void HealthChanged()
    {
        var resume = _sessionRequested;
        Enabled = false;
        _sessionRequested = resume;
        Save();
        OnPropertyChanged(nameof(FasterHoldEnabled));
        OnPropertyChanged(nameof(EnabledCount)); OnPropertyChanged(nameof(OverlayBindings)); OnPropertyChanged(nameof(NoOverlayBindings));
    }
    private void ResumeAfterEdit()
    {
        if ((_engine is null || _engine.Status == "Off") && Health.Fault is null) Enabled = true;
    }
    private void Filter()
    {
        VisibleMacros.Clear();
        foreach (var row in _rows.Where(r => (HasSearch || r.Category == SelectedCategory) &&
            (r.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) || r.Description.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase))))
            VisibleMacros.Add(row);
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(ResultCount));
    }
    private void Save()
    {
        try { _store.Save(_preferences); SaveStatus = "Saved locally"; }
        catch (Exception ex) { SaveStatus = "Not saved"; Status = "Could not save macro settings: " + ex.Message; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _overlayFocus.Stop();
        _shortcuts?.Dispose();
        _engine?.Dispose();
        Health.Dispose();
        ParryBar.Dispose();
        DodgeBar.Dispose();
        FlowStateOverlay.Dispose();
        _ritualPromptOverlay?.Close();
        _window?.Close(); _overlay?.Close();
        _openRequests.Stop(); _sessionLease?.Dispose();
    }
}

public sealed class MacroRow : ObservableObject
{
    private bool _expanded;
    public bool Expanded { get => _expanded; set { if (SetProperty(ref _expanded, value)) { OnPropertyChanged(nameof(DisclosureIcon)); OnPropertyChanged(nameof(ShowDetails)); } } }
    public LucideIconNames DisclosureIcon => Expanded ? LucideIconNames.ChevronUp : LucideIconNames.ChevronDown;
    private readonly MacroEntry _entry;
    private readonly Action _changed;
    public MacroRow(MacroDefinition definition, MacroEntry entry, Action changed)
    {
        Definition = definition; _entry = entry; _changed = changed;
        Fields = definition.Fields.Select(field => new MacroFieldRow(field, entry.Values, () => { OnPropertyChanged(nameof(RitualAreaLabel)); foreach(var f in Fields ?? []) f.RefreshVisibility(); changed(); }, (definition.Id == "ritual-cast" && field.Key.EndsWith("coordinates", StringComparison.Ordinal)))).ToArray();
    }
    public MacroDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Category => Definition.Category;
    public string Description => Definition.Description;
    public bool Supported => MacroEngine.GetSupport(Definition.Id).Supported;
    public string SupportNote => MacroEngine.GetSupport(Definition.Id).Reason;
    public bool Unavailable => !Supported;
    public bool Enabled { get => _entry.Enabled; set { if (_entry.Enabled == value) return; _entry.Enabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowDetails)); _changed(); } }
    public IReadOnlyList<MacroFieldRow> Fields { get; }
    public bool IsFlowState => Definition.Id == "silentheart-flow-state";
    public bool IsRitual => Definition.Id == "ritual-cast";
    public string RitualAreaLabel => string.IsNullOrWhiteSpace(_entry.Values.GetValueOrDefault("startcoordinates")) || string.IsNullOrWhiteSpace(_entry.Values.GetValueOrDefault("endcoordinates")) ? "No prompt area selected" : "Prompt area selected";
    public void SetRitualArea(Health.HealthRegion region, bool restart = true)
    {
        if (!IsRitual) throw new InvalidOperationException("This macro does not use a prompt area.");
        region.Validate();
        _entry.Values["startcoordinates"] = $"{region.X} {region.Y}";
        _entry.Values["endcoordinates"] = $"{region.X + region.Width} {region.Y + region.Height}";
        foreach (var field in Fields.Where(f => f.Key.EndsWith("coordinates", StringComparison.Ordinal))) field.RefreshValue();
        OnPropertyChanged(nameof(RitualAreaLabel));
        if (restart) _changed();
    }
    public MacroFieldRow? HeaderKey => Fields.FirstOrDefault(f => f.Key == (Definition.Id == "silentheart-flow-state" ? "flowstatekeybind" : "keybind"));
    public bool HasHeaderKey => HeaderKey is not null;
    public bool ShowEnableToggle => true;
    public string HeaderKeyValue
    {
        get => HeaderKey?.Value ?? "";
        set { if (HeaderKey is null) return; HeaderKey.Value = value; OnPropertyChanged(); }
    }
    public bool HasCoordinateSettings => IsRitual || Fields.Any(f => f.Key.EndsWith("coordinates", StringComparison.Ordinal));
    public IReadOnlyList<MacroFieldRow> DetailFields => Fields.Where(f => !ReferenceEquals(f, HeaderKey)).ToArray();
    public bool HasDetailSettings => IsRitual || DetailFields.Count > 0;
    public bool ShowDetails => HasDetailSettings && Enabled;
    public bool PlainHeader => !HasDetailSettings;
    public bool HasFields => Fields.Count > 0;
    public bool NoFields => !HasFields;
}

public sealed class MacroFieldRow : ObservableObject
{
    private readonly MacroField _field;
    private readonly Dictionary<string, string> _values;
    private readonly Action _changed;
    private readonly bool _calibration;
    public MacroFieldRow(MacroField field, Dictionary<string, string> values, Action changed, bool calibration = false) { _field = field; _values = values; _changed = changed; _calibration = calibration; }
    public bool IsShown => !_calibration && (Key switch {"webhook_url" => _values.GetValueOrDefault("delivery_mode","0")=="0", "discord_token" or "channel_id" => _values.GetValueOrDefault("delivery_mode","0")=="1", _=>true});
    public void RefreshVisibility() => OnPropertyChanged(nameof(IsShown));
    public void RefreshValue() => OnPropertyChanged(nameof(Value));
    public char SecretMask => Key=="discord_token" ? '\u25CF' : '\0';
    private IReadOnlyList<GankWindowOption>? _windows;
    public void RefreshChoices() {if(Key!="capture_window")return;_windows=GankWindowCapture.List();OnPropertyChanged(nameof(Choices));OnPropertyChanged(nameof(ChoiceIndex));}

    public string Key => _field.Key;
    public string Label => _field.Label;
    public bool IsPoint => !_calibration && Key.EndsWith("coordinates", StringComparison.Ordinal);
    public bool IsPath => Key is "basepath" or "txtpath";
    public bool IsChoice => Key is "scale" or "resolution" or "delivery_mode" or "capture_window";
    public string PickLabel => IsPoint ? (string.IsNullOrWhiteSpace(Value) ? "Pin point" : "Repin · " + Value) : (string.IsNullOrWhiteSpace(Value) ? (Key == "basepath" ? "Choose folder" : "Choose file") : System.IO.Path.GetFileName(Value.TrimEnd('\\', '/')));
    public IReadOnlyList<string> Choices => Key == "capture_window" ? (_windows ??= GankWindowCapture.List()).Select(w=>w.Label).ToArray() : Key=="delivery_mode" ? new[]{"Webhook","Selfbot"} : Key == "scale" ? new[] { "100%", "125%" } : new[] { "1128 × 634", "1366 × 768", "1600 × 900", "1600 × 1200", "1920 × 1080", "1920 × 1200", "2560 × 1080", "2560 × 1440", "3440 × 1440" };
    public int ChoiceIndex { get => Key=="capture_window" ? (_windows ??= GankWindowCapture.List()).ToList().FindIndex(w=>w.Id==Value) : int.TryParse(Value, out var i) ? i : -1; set { if(value>=0 && value<Choices.Count)Value=Key=="capture_window" ? _windows![value].Id : value.ToString(System.Globalization.CultureInfo.InvariantCulture); } }

    public string Help => _field.Help;
    public bool IsBoolean => _field.DefaultValue is "true" or "false";
    public bool IsKeybind => _field.Key == "keyToPress" || _field.Key.EndsWith("keybind", StringComparison.Ordinal);
    public bool AllowsMainMouse => Key == "feintkeybind";
    public bool IsText => !_calibration && !IsBoolean && !IsKeybind && !IsPoint && !IsPath && !IsChoice;
    public bool BooleanValue { get => bool.TryParse(Value, out var enabled) && enabled; set => Value = value ? "true" : "false"; }
    public string Value { get { var raw=_values.GetValueOrDefault(_field.Key, _field.DefaultValue);return Key=="discord_token" ? GankSecrets.Read(raw) : raw; } set { if (Value == value) return; _values[_field.Key] = Key=="discord_token" ? GankSecrets.Protect(value) : value; OnPropertyChanged(); OnPropertyChanged(nameof(BooleanValue)); OnPropertyChanged(nameof(PickLabel)); OnPropertyChanged(nameof(ChoiceIndex)); _changed(); } }
}

public sealed record MacroBindingSummary(string Name, string Keys);
