using System;
using System.Collections.Generic;

namespace Merostrap.Macros;

// Sequence identifies the same physical edge in the synchronous hook and asynchronous dispatcher.
public readonly record struct MacroInputEvent(string Key, bool IsDown, bool IsInjected = false, long Sequence = 0);
public readonly record struct MacroSupport(bool Supported, string Reason);

/// <summary>The device boundary. Hooks publish physical events; automation is sent through the methods below.</summary>
public interface IMacroInput : IDisposable
{
    event Action<MacroInputEvent>? Input;
    bool IsAvailable { get; }
    bool IsForegroundRoblox { get; }
    Exception? Failure => null;
    string PrimaryButton { get; }
    (int Width, int Height) ScreenSize { get; }
    (int X,int Y,int Width,int Height)? RobloxScreenBounds => (0,0,ScreenSize.Width,ScreenSize.Height);
    (int X,int Y,int Width,int Height) ScreenBoundsForPoint(int x,int y) => (0,0,ScreenSize.Width,ScreenSize.Height);
    void Start(Func<MacroInputEvent, bool> suppress);
    /// <summary>Startup-only A–Z/0–9 snapshot, before automation owns any keys.</summary>
    IReadOnlyCollection<string> SnapshotRitualKeysDown() => Array.Empty<string>();
    void Stop();
    void Key(string key, bool down);
    void Move(int x, int y);
    void Scroll(int delta) => throw new NotSupportedException("Mouse wheel input is unavailable.");
    void Character(char character);
}
