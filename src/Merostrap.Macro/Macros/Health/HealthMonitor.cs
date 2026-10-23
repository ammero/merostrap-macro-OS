// SPDX-License-Identifier: MPL-2.0
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Merostrap.Macros.Health;
/// <summary>Publishes screen-derived estimates; events arrive on a worker and never operate Discord.</summary>
public sealed class HealthMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly Func<bool> _foreground;
    private readonly Func<HealthRegion, HealthPixels> _capture;
    private readonly TimeSpan _interval;
    private readonly Func<HealthPixels, HealthSample> _analyze;
    private CancellationTokenSource? _session;
    private bool _disposed;
    public HealthMonitor(Func<bool>? isForegroundRoblox = null, Func<HealthRegion, HealthPixels>? capture = null, TimeSpan? interval = null, Func<HealthPixels, HealthSample>? analyze = null)
    {
        _analyze = analyze ?? HealthBarAnalyzer.Analyze;
        _foreground = isForegroundRoblox ?? WindowsHealthCapture.IsForegroundRoblox;
        _capture = capture ?? new WindowsHealthCapture().Capture;
        _interval = interval ?? TimeSpan.FromMilliseconds(100);
        if (_interval < TimeSpan.FromMilliseconds(20) || _interval > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(interval), "Health polling interval must be between 20 ms and 30 seconds.");
    }
    public bool IsRunning { get { lock (_gate) return _session != null; } }
    public event Action<HealthSample>? Updated;
    public void Start(HealthRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        region.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _session?.Cancel();
            var session = new CancellationTokenSource();
            _session = session;
            PublishLocked(new(null, "Waiting for health reading."));
            _ = Task.Run(() => Poll(region, session));
        }
    }
    public void Stop()
    {
        lock (_gate)
        {
            _session?.Cancel(); _session = null;
            PublishLocked(new(null, "Health monitor off."));
        }
    }
    private async Task Poll(HealthRegion region, CancellationTokenSource session)
    {
        try
        {
            while (true)
            {
                session.Token.ThrowIfCancellationRequested();
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                HealthSample sample;
                try
                {
                    if (!_foreground()) sample = Waiting();
                    else
                    {
                        await _captureGate.WaitAsync(session.Token);
                        try
                        {
                            session.Token.ThrowIfCancellationRequested();
                            if (!_foreground()) sample = Waiting();
                            else
                            {
                                var pixels = _capture(region);
                                session.Token.ThrowIfCancellationRequested();
                                sample = !_foreground() ? Waiting() : pixels.Width != region.Width || pixels.Height != region.Height
                                    ? new(null, "Health capture dimensions changed. Select the bar again.")
                                    : _analyze(pixels);
                                if (!_foreground()) sample = Waiting();
                            }
                        }
                        finally { _captureGate.Release(); }
                    }
                }
                catch (OperationCanceledException) when (session.IsCancellationRequested) { throw; }
                catch (Exception e) { sample = new(null, "Health capture error: " + e.GetBaseException().Message); }
                lock (_gate)
                {
                    if (!ReferenceEquals(_session, session) || session.IsCancellationRequested) return;
                    PublishLocked(sample);
                }
                var remaining = _interval - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                await Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1), session.Token);
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested) { }
        finally { session.Dispose(); }
    }
    private static HealthSample Waiting() => new(null, "Waiting for foreground Roblox.");
    private void PublishLocked(HealthSample sample)
    {
        if (Updated != null) foreach (Action<HealthSample> subscriber in Updated.GetInvocationList()) { try { subscriber(sample); } catch { } }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Stop(); _disposed = true;
            // CaptureGate has no kernel handle. In-flight native capture owns its cleanup and
            // releases this managed semaphore after Stop returns, so it must remain usable.
        }
    }
}
