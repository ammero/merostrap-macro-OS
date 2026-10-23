// SPDX-License-Identifier: MPL-2.0
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Merostrap.Macros;

/// <summary>Keeps native hook callbacks short and preserves the order of physical events.</summary>
internal sealed class MacroInputDispatcher : IDisposable
{
    private readonly Channel<MacroInputEvent> _events = Channel.CreateUnbounded<MacroInputEvent>(new() { SingleReader = true, SingleWriter = true });
    private int _stopped;
    public MacroInputDispatcher(Action<MacroInputEvent> receive, Action<Exception> fault)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var input in _events.Reader.ReadAllAsync())
                {
                    if (Volatile.Read(ref _stopped) != 0) break;
                    receive(input);
                }
            }
            catch (Exception e) { try { fault(e); } catch { } }
        });
    }
    public void Publish(MacroInputEvent input, bool isForegroundRoblox = true) { if (isForegroundRoblox && Volatile.Read(ref _stopped) == 0) _events.Writer.TryWrite(input); }
    public void Dispose() { Interlocked.Exchange(ref _stopped, 1); _events.Writer.TryComplete(); }
}
