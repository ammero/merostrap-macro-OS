// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Merostrap.Macros;

/// <summary>One cancellable input workflow at a time, scoped to the foreground Roblox player.</summary>
public sealed class MacroEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly object _lifecycle = new();
    private readonly IMacroInput _input;
    private readonly Func<IRitualPromptSource>? _ritualSourceFactory;
    private readonly Func<ICharismaPromptSource>? _charismaSourceFactory;
    private readonly Func<string,CancellationToken,Task>? _clipboardSetter;
    private readonly Func<Uri, string, CancellationToken, Task> _webhookSender;
    private readonly Func<GankAlert,CancellationToken,Task>? _gankSender;
    private readonly Func<string,CancellationToken,Task<byte[]>>? _windowCapture;
    private readonly HashSet<string> _physical = new(StringComparer.Ordinal);
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ritualHeld = new(StringComparer.Ordinal);
    private readonly HashSet<MacroInputEvent> _pendingFlowInputs = [];
    private MacroWorkflow? _flowWorkflow;
    private List<MacroWorkflow> _workflows = [];
    private CancellationTokenSource? _session;
    private Run? _active;
    private Action<MacroInputEvent>? _inputHandler;
    private string? _releaseError;
    private string _status = "Off";
    private string _issues = "";
    private bool _disposed;
    private bool _started;
    private bool _monitorStarted;
    private bool _foreground;
    private bool _automaticPaused;
    private bool _ritualRecoveryRequested;
    private long _lastWPress;
    private bool _sprinting;
    private string _interfaceKey = "";
    public string InterfaceKey
    {
        get { lock (_gate) return _interfaceKey; }
        set { lock (_gate) _interfaceKey = MacroKeys.Normalize(value); }
    }

    public MacroEngine() : this(new WindowsMacroInput()) { }
    public MacroEngine(IMacroInput input, Func<IRitualPromptSource>? ritualSourceFactory = null, Func<Uri, string, CancellationToken, Task>? webhookSender = null, Func<GankAlert,CancellationToken,Task>? gankSender = null, Func<string,CancellationToken,Task<byte[]>>? windowCapture = null, Func<ICharismaPromptSource>? charismaSourceFactory = null, Func<string,CancellationToken,Task>? clipboardSetter = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _ritualSourceFactory = ritualSourceFactory; _charismaSourceFactory=charismaSourceFactory; _clipboardSetter=clipboardSetter;
        _webhookSender = webhookSender ?? GankWebhook.SendAsync;
        _gankSender = gankSender; _windowCapture = windowCapture;
    }
    public string Status { get { lock (_gate) return _status; } }
    public bool IsRunning { get { lock (_gate) return _session != null && _started; } }
    public event Action<string>? StatusChanged;
    public event Action<RitualPromptHighlight?>? RitualPromptChanged;
    public event Action<string>? RitualDiagnostic;
    internal void TraceRitual(CancellationToken token, string message)
    {
        lock (_gate)
        {
            if (_active is not { } run || run.Workflow.Id!="ritual-cast" ||
                run.Cancellation.Token!=token || token.IsCancellationRequested) return;
            if (RitualDiagnostic is { } subscribers)
                foreach (Action<string> subscriber in subscribers.GetInvocationList()) { try { subscriber(message); } catch { } }
        }
    }
    public event Action<Health.HealthRegion>? RitualAreaDetected;
    private Health.HealthRegion? _lastRitualArea;
    internal void ReportRitualArea(CancellationToken token, Health.HealthRegion area)
    {
        lock (_gate)
        {
            if (_active is not { } run || run.Workflow.Id != "ritual-cast" ||
                run.Cancellation.Token != token || token.IsCancellationRequested || _lastRitualArea == area) return;
            _lastRitualArea = area;
            if (RitualAreaDetected is { } subscribers)
                foreach (Action<Health.HealthRegion> subscriber in subscribers.GetInvocationList())
                    subscriber(area);
        }
    }
    private RitualPromptHighlight? _ritualPrompt;
    private void SetRitualPromptLocked(RitualPromptHighlight? prompt)
    {
        if (_ritualPrompt == prompt) return;
        _ritualPrompt = prompt;
        if (RitualPromptChanged is { } subscribers)
            foreach (Action<RitualPromptHighlight?> subscriber in subscribers.GetInvocationList()) { try { subscriber(prompt); } catch { } }
    }
    internal void ReportRitualPrompt(CancellationToken token,RitualPromptHighlight? prompt)
    {
        lock (_gate)
            if (_active is { } run && (run.Workflow.Id is "ritual-cast" or "charisma-autofill") && run.Cancellation.Token == token && !token.IsCancellationRequested)
                SetRitualPromptLocked(prompt);
    }
    public static MacroSupport GetSupport(string id) => id is "mantra-variants" or "mantra-roll-tech" or "mantra-slide-tech" ? new(false, "Removed from Merostrap") : MacroWorkflows.GetSupport(id);

    public void Start(MacroProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();
            if (_held.Count != 0) return;
            if (!_input.IsAvailable) { SetStatus("Unavailable: Windows input automation requires Windows."); return; }
            var issues = new List<string>();
            var workflows = new List<MacroWorkflow>();
            foreach (var (id, entry) in profile.Entries)
            {
                if (!entry.Enabled) continue;
                var support = GetSupport(id);
                if (!support.Supported) { issues.Add($"{id} unavailable: {support.Reason}"); continue; }
                try
                {
                    var workflow = MacroWorkflows.Create(id, new Dictionary<string, string>(entry.Values, StringComparer.Ordinal), _ritualSourceFactory, _webhookSender, _gankSender, _windowCapture, _charismaSourceFactory, _clipboardSetter);
                    if (workflow.Triggers.Contains("esc", StringComparer.Ordinal)) throw new ArgumentException("Esc is reserved for stopping macros. Choose another hotkey.");
                    workflows.Add(workflow with { Triggers = workflow.Triggers.Select(k => k == "primary" ? _input.PrimaryButton : k).ToArray() });
                }
                catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
                { issues.Add($"{id}: {e.Message}"); }
            }
            lock (_gate)
            {
                _workflows = workflows.OrderByDescending(w => w.FlowDetector is not null).ToList(); _issues = string.Join("; ", issues);
                Volatile.Write(ref _flowWorkflow, workflows.FirstOrDefault(w => w.FlowDetector is not null));
                _foreground = _input.IsForegroundRoblox; _automaticPaused = false; _ritualRecoveryRequested=false;
                SetStatusLocked(ReadyStatus());
                if (workflows.Count == 0) return;
                _session = new CancellationTokenSource();
                var session = _session;
                _inputHandler = e => OnInput(e, session);
                _input.Input += _inputHandler;
            }
            try
            {
                _input.Start(ShouldSuppress);
                var session = _session!;
                lock (_gate)
                {
                    if (workflows.Any(w => w.Id == "ritual-cast"))
                        foreach (var key in _input.SnapshotRitualKeysDown().Select(MacroKeys.Normalize))
                            if (key.Length == 1 && key[0] is >= 'a' and <= 'z' or >= '0' and <= '9' && !_held.Contains(key))
                                _physical.Add(key);
                    _started = true; _monitorStarted = true;
                }
                _ = WatchFocus(session);
            }
            catch (Exception e)
            {
                StopCore(); SetStatus("Error starting input: " + e.Message);
            }
        }
    }

    public void Stop() { lock (_lifecycle) StopCore(); }
    private void StopCore()
    {
        lock (_gate)
        {
            if (_inputHandler != null) _input.Input -= _inputHandler;
            _inputHandler = null;
            var session = _session; _session = null;
            session?.Cancel(); CancelActive();
            if (!_monitorStarted) session?.Dispose();
            _started = false; _monitorStarted = false;
            _physical.Clear(); _pendingFlowInputs.Clear(); _workflows.Clear(); _sprinting = false; _lastWPress = 0;
            Volatile.Write(ref _flowWorkflow, null);
            _lastRitualArea = null;
            _issues = ""; SetStatusLocked(_releaseError ?? "Off");
        }
        try { _input.Stop(); } catch (Exception e) { SetStatus("Error stopping input: " + e.Message); }
    }

    private bool ShouldSuppress(MacroInputEvent e)
    {
        if (e.IsInjected) return false;
        var key = MacroKeys.Normalize(e.Key);
        var flow = Volatile.Read(ref _flowWorkflow);
        var foreground = _input.IsForegroundRoblox;
        bool matched = false;
        // Gesture state has its own short lock: physical releases cannot be lost
        // while SendInput owns the engine gate. It never calls the input device.
        if (!foreground || key == "esc") flow?.FlowDetector?.Reset();
        else if (key != Volatile.Read(ref _interfaceKey)) matched = flow?.FlowDetector?.Observe(e, Environment.TickCount64) == true;
        // A low-level hook must never wait for a sender holding this gate across SendInput.
        if (!Monitor.TryEnter(_gate)) return false;
        try
        {
            if (_session is null || key == _interfaceKey) return false;
            if (!foreground || key == "esc") { ResetFlowGestures(); return false; }
            var canRun = _active == null || _active.Workflow.Mode is MacroRunMode.Hold or MacroRunMode.Automatic;
            if (matched && ReferenceEquals(flow, _flowWorkflow) &&
                (canRun || _active?.Workflow.Id == "auto-roll-cancel") && _pendingFlowInputs.Add(e)) return true;
            return e.IsDown && canRun && _workflows.Any(w => w.Suppress && w.Triggers.Contains(key, StringComparer.Ordinal));
        }
        finally { Monitor.Exit(_gate); }
    }

    private void OnInput(MacroInputEvent e, CancellationTokenSource originatingSession)
    {
        if (e.IsInjected) return;
        var key = MacroKeys.Normalize(e.Key);
        lock (_gate)
        {
            if (!ReferenceEquals(_session, originatingSession)) return;
            if (key == _interfaceKey) return;
            if (key == "esc" && e.IsDown && (_input.IsForegroundRoblox)) { _automaticPaused = true; _ritualRecoveryRequested=_workflows.Any(w=>w.Id=="ritual-cast" && w.Mode==MacroRunMode.Automatic); CancelActive(); SetStatusLocked(ReadyStatus()); return; }
            if (!_input.IsForegroundRoblox) { LoseFocus(); return; }
            var wasDown = _physical.Contains(key);
            if (e.IsDown) _physical.Add(key); else _physical.Remove(key);
            if (key == "esc" && e.IsDown) { _automaticPaused = true; _ritualRecoveryRequested=_workflows.Any(w=>w.Id=="ritual-cast" && w.Mode==MacroRunMode.Automatic); CancelActive(); SetStatusLocked(ReadyStatus()); return; }
            if (key == "w")
            {
                if (!e.IsDown) _sprinting = false;
                else if (!wasDown) { var now = Environment.TickCount64; _sprinting = _lastWPress != 0 && now - _lastWPress <= 600; _lastWPress = now; }
            }
            if (!e.IsDown && _active != null &&
                ((_active.Workflow.Mode == MacroRunMode.Hold && _active.Trigger == key) ||
                 ((_active.Workflow.Id == "golden-tongue" || _active.Workflow.Id.StartsWith("chat-message-", StringComparison.Ordinal)) && key == "w")))
            { CancelActive(); ResumeHeld(); SetStatusLocked(ReadyStatus()); }
            if (e.IsDown && wasDown) return;
            foreach (var workflow in _workflows)
            {
                string? text = null;
                if (workflow.FlowDetector is not null)
                {
                    if (!e.IsDown || !_pendingFlowInputs.Remove(e)) continue;
                    text = key;
                }
                else if (e.IsDown == workflow.OnRelease || !workflow.Triggers.Contains(key, StringComparer.Ordinal)) continue;
                if (workflow.OnRelease && !wasDown) continue;
                if (workflow.Id == "mantra-slide-tech" && !_sprinting) continue;
                if (workflow.Id == "dynamic-auto-uppercut" && _sprinting) continue;
                if(workflow.Id=="charisma-autofill" && _active?.Workflow.Id==workflow.Id) {
                    _automaticPaused=true;_ritualRecoveryRequested=false;CancelActive();SetStatusLocked(ReadyStatus());return;
                }
                if(workflow.Id=="charisma-autofill")_automaticPaused=false;
                if (_active != null)
                {
                    if (_active.Workflow.Id == workflow.Id && workflow.Mode == MacroRunMode.Toggle)
                    { CancelActive(); SetStatusLocked(ReadyStatus()); return; }
                    if (workflow.FlowDetector is not null || _active.Workflow.Mode == MacroRunMode.Automatic ||
                        (_active.Workflow.Id == "ritual-cast" && workflow.Id != "ritual-cast") ||
                        (_active.Workflow.Mode == MacroRunMode.Hold && _active.Workflow.Id != workflow.Id))
                        CancelActive();
                    else return;
                }
                if (_held.Count != 0) { SetStatusLocked(_releaseError ?? "Error: input has not been released."); return; }
                var run = new Run(workflow, key, CancellationTokenSource.CreateLinkedTokenSource(_session.Token));
                _active = run;
                SetStatusLocked("Running: " + workflow.Id + IssueSuffix());
                _ = Task.Run(() => Execute(run, text));
                return;
            }
        }
    }

    private async Task Execute(Run run, string? text)
    {
        string? error = null;
        try { await run.Workflow.Execute(new MacroActions(this, run.Cancellation.Token), text); }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested || !_input.IsForegroundRoblox) { }
        catch (Exception e) { error = "Error in " + run.Workflow.Id + ": " + e.Message; }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, run))
                {
                    if (run.Workflow.Id is "ritual-cast" or "charisma-autofill") SetRitualPromptLocked(null);
                    if (run.Workflow.Mode == MacroRunMode.Automatic && error is not null) { _automaticPaused = true; _ritualRecoveryRequested=false; }
                    var releaseError = ReleaseHeld(); _active = null;
                    SetStatusLocked(error ?? releaseError ?? (!run.Cancellation.IsCancellationRequested ? run.CompletionStatus : null) ?? ReadyStatus());
                    if (error is null && releaseError is null && !run.Cancellation.IsCancellationRequested && run.Workflow.Mode != MacroRunMode.Hold)
                        ResumeHeld();
                }
            }
            run.Cancellation.Dispose();
        }
    }

    private void ResumeHeld()
    {
        if (_active is not null || _session is null || _held.Count != 0 || !_input.IsForegroundRoblox) return;
        var workflow = _workflows.FirstOrDefault(w => w.Mode == MacroRunMode.Hold && w.Triggers.Any(_physical.Contains));
        if (workflow is null) return;
        var trigger = workflow.Triggers.First(_physical.Contains);
        var run = new Run(workflow, trigger, CancellationTokenSource.CreateLinkedTokenSource(_session.Token));
        _active = run;
        SetStatusLocked("Running: " + workflow.Id + IssueSuffix());
        _ = Task.Run(() => Execute(run, null));
    }

    private async Task WatchFocus(CancellationTokenSource session)
    {
        try
        {
            while (true)
            {
                await Task.Delay(25, session.Token);
                if (_input.Failure is { } failure) throw new InvalidOperationException("Native input hook stopped.", failure);
                lock (_gate)
                {
                    if (!ReferenceEquals(_session, session)) return;
                    var foreground = _input.IsForegroundRoblox;
                    if (!foreground) LoseFocus();
                    else if (!_foreground) { _foreground = true; _physical.Clear(); SetStatusLocked(ReadyStatus()); }
                    if (foreground && _active is null && _held.Count == 0 && _started &&
                        _workflows.FirstOrDefault(w => w.Mode == MacroRunMode.Automatic &&
                            (!_automaticPaused || _ritualRecoveryRequested && w.Id=="ritual-cast")) is { } automatic)
                    {
                        var run = new Run(automatic, "", CancellationTokenSource.CreateLinkedTokenSource(session.Token));
                        _active = run;
                        _ = Task.Run(() => Execute(run, null));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested) { }
        catch (Exception e)
        {
            lock (_lifecycle)
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_session, session)) return;
                    if (_inputHandler != null) _input.Input -= _inputHandler;
                    _inputHandler = null; _session = null; _started = false; _monitorStarted = false;
                    session.Cancel(); CancelActive(); SetStatusLocked("Error checking Roblox focus: " + e.Message);
                }
                try { _input.Stop(); } catch (Exception stopError) { SetStatus("Error stopping native input: " + stopError.Message); }
            }
        }
        finally { session.Dispose(); }
    }

    private void LoseFocus()
    {
        CancelActive(); _physical.Clear(); ResetFlowGestures(); _sprinting = false; _lastWPress = 0;
        if (_foreground) { _foreground = false; SetStatusLocked(ReadyStatus()); }
    }
    private void CancelActive()
    {
        SetRitualPromptLocked(null);
        _active?.Cancellation.Cancel(); _active = null;
        var error = ReleaseHeld(); if (error != null) SetStatusLocked(error);
    }
    private void ResetFlowGestures()
    {
        _pendingFlowInputs.Clear();
        foreach (var workflow in _workflows) workflow.FlowDetector?.Reset();
    }
    private string? ReleaseHeld()
    {
        string? error = null;
        foreach (var key in _held.ToArray())
        {
            try { ReleaseKey(key); }
            catch (Exception e) { error = "Error releasing " + key + ": " + e.Message; }
        }
        _releaseError = error;
        return error;
    }
    private string ReadyStatus() => _releaseError ?? ((_workflows.Count == 0 ? "No ready macros" : !_foreground ? "Waiting for foreground Roblox" : "Armed: " + _workflows.Count + " macros") +
        (_automaticPaused && _workflows.Any(w=>w.Mode==MacroRunMode.Automatic) ? " · Automatic macros paused; toggle macros off/on to resume" : "") + IssueSuffix());
    private string IssueSuffix() => _issues.Length == 0 ? "" : ". " + _issues;
    private void SetStatus(string status) { lock (_gate) SetStatusLocked(status); }
    private void SetStatusLocked(string status)
    {
        if (_status == status) return;
        _status = status;
        if (StatusChanged != null) foreach (Action<string> subscriber in StatusChanged.GetInvocationList()) { try { subscriber(status); } catch { } }
    }

    internal void Send(CancellationToken token, Action<IMacroInput> send)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_session == null || !_input.IsForegroundRoblox) throw new OperationCanceledException(token);
            send(_input);
        }
    }
    internal void ReportStatus(CancellationToken token, string status)
    {
        lock (_gate)
        {
            if (_active is { } run &&
                run.Cancellation.Token == token && !token.IsCancellationRequested && _input.IsForegroundRoblox)
                SetStatusLocked(status + IssueSuffix());
        }
    }
    internal void Complete(CancellationToken token,string status)
    {
        lock(_gate)
            if(_active is { } run && run.Cancellation.Token==token && !token.IsCancellationRequested)
                run.CompletionStatus=status;
    }
    internal void Key(CancellationToken token, string key, bool down)
    {
        Send(token, input =>
        {
            if (down) { _held.Add(key); input.Key(key, true); }
            else { input.Key(key, false); _held.Remove(key); }
        });
    }
    internal string PrimaryButton => _input.PrimaryButton;
    internal string? PrepareFlowForGeneratedFeint(CancellationToken token, string key)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (_active is not { } run || run.Cancellation.Token != token ||
                run.Workflow.Id != "auto-roll-cancel" || run.FlowPrepared || !_physical.Contains("ctrl") ||
                key != (_input.PrimaryButton == "mouse1" ? "mouse2" : "mouse1") || _flowWorkflow?.FlowStateKey is not { } flowKey)
                return null;
            run.FlowPrepared = true;
            return flowKey;
        }
    }
    internal (int Width, int Height) ScreenSize => _input.ScreenSize;
    public void Dispose()
    {
        lock (_lifecycle) { if (_disposed) return; StopCore(); _disposed = true; _input.Dispose(); }
    }
    private sealed record Run(MacroWorkflow Workflow, string Trigger, CancellationTokenSource Cancellation)
    {
        public string? CompletionStatus {get;set;}
        public bool FlowPrepared {get;set;}
    }
    internal bool RitualRecoveryPending(CancellationToken token)
    {
        lock(_gate)return _ritualRecoveryRequested && _automaticPaused &&
            _active is {} run && run.Workflow.Id=="ritual-cast" && run.Cancellation.Token==token && !token.IsCancellationRequested;
    }
    internal bool ResumeRitualAfterNewPanel(CancellationToken token)
    {
        lock(_gate) {
            if(!RitualRecoveryPending(token) || !_input.IsForegroundRoblox)return false;
            _ritualRecoveryRequested=false;_automaticPaused=false;
            SetStatusLocked("Ritual: recovered for the next ritual");return true;
        }
    }
    internal void BeginRitualTap(CancellationToken token, string key)
    {
        Send(token, input =>
        {
            if(_automaticPaused)throw new OperationCanceledException("Ritual input is paused.",token);
            // Windows combines physical and injected state. Release a physical
            // hold first so the prompt receives a new down edge.
            if (_physical.Contains(key)) input.Key(key, false);
            token.ThrowIfCancellationRequested();
            _ritualHeld.Add(key);
            _held.Add(key);
            input.Key(key, true);
        });
    }
    internal void EndRitualTap(CancellationToken token, string key) => Send(token, _ => ReleaseKey(key));
    private void ReleaseKey(string key)
    {
        // A second Up/Down would submit the prompt twice. Leave the already-down
        // state with the physical hold; the user's own release clears it.
        if (!(_ritualHeld.Contains(key) && _physical.Contains(key) && _input.IsForegroundRoblox))
            _input.Key(key, false);
        _ritualHeld.Remove(key);
        _held.Remove(key);
    }
}
