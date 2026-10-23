using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace Merostrap.Macros;

public interface IDiscordDeafenState
{
    Task<bool?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Reads Discord's local switch; explicit deafen requests operate its accessibility pattern without focusing the window.</summary>
public sealed class DiscordDeafenState : IDiscordDeafenState
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string ReadStatus { get; private set; } = "Not checked";
    public bool? ForegroundPreserved { get; private set; }

    public async Task<bool?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !await _gate.WaitAsync(0, cancellationToken)) return null;
        var read = Task.Run(() =>
        {
            try { return ReadNative(); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { ReadStatus = "Accessibility unavailable: " + ex.GetType().Name; return (bool?)null; }
            finally { _gate.Release(); }
        }, CancellationToken.None);
        try { return await read.WaitAsync(TimeSpan.FromMilliseconds(700), cancellationToken); }
        catch (TimeoutException) { ReadStatus = "Discord state read timed out"; return null; }
    }

    public async Task<bool?> EnsureDeafenedAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !await _gate.WaitAsync(0, cancellationToken)) return null;
        try
        {
            // Await the mutation itself: a timeout must never leave an unobserved late toggle.
            var result = await Task.Run(() => ReadNative(true, cancellationToken), CancellationToken.None);
            if (result is true or null) return result;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(150, cancellationToken);
                result = await Task.Run(() => ReadNative(), CancellationToken.None);
                if (result == true) return true;
            }
            return result;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { ReadStatus = "Discord state change could not be confirmed"; return null; }
        finally { _gate.Release(); }
    }

    public static bool? RequestDeafened(Func<bool?> read, Action turnOn)
    {
        var current = read();
        if (current != false) return current;
        turnOn();
        return read();
    }

    private bool? ReadNative(bool ensureDeafened = false, CancellationToken cancellationToken = default)
    {
        var handles = new List<nint>();
        foreach (var name in new[] { "Discord", "DiscordPTB", "DiscordCanary" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                    if (process.MainWindowHandle != 0) handles.Add(process.MainWindowHandle);
        if (handles.Count != 1) { ReadStatus = $"Discord window count: {handles.Count}"; return null; }

        using var focus = ensureDeafened ? new DiscordFocusGuard(handles[0], value => ForegroundPreserved = value) : null;

        var automation = (IUIAutomation)new CUIAutomation8();
        IUIAutomationElement? root = null;
        IUIAutomationElementArray? matches = null;
        IUIAutomationCondition? deafen = null, undeafen = null, condition = null;
        try
        {
            var timed = (IUIAutomation2)automation;
            timed.ConnectionTimeout = 250;
            timed.TransactionTimeout = 250;
            root = automation.ElementFromHandle(handles[0]);
            deafen = automation.CreatePropertyCondition(30005, "Deafen");
            undeafen = automation.CreatePropertyCondition(30005, "Undeafen");
            condition = automation.CreateOrCondition(deafen, undeafen);
            matches = root.FindAll(TreeScope.TreeScope_Descendants, condition);
            if (matches.Length != 1) { ReadStatus = "Discord deafen control unavailable"; return null; }
            var element = matches.GetElement(0);
            object? pattern = null;
            try
            {
                pattern = element.GetCurrentPattern(10015);
                ReadStatus = pattern is IUIAutomationTogglePattern ? "Toggle state read" : "Toggle state not exposed"; // UIA_TogglePatternId
                if (pattern is not IUIAutomationTogglePattern toggle) return null;
                bool? Read() => toggle.CurrentToggleState switch
                {
                    ToggleState.ToggleState_On => true,
                    ToggleState.ToggleState_Off => false,
                    _ => null
                };
                return ensureDeafened ? RequestDeafened(Read, () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { toggle.Toggle(); }
                    finally { focus?.Restore(); }
                }) : Read();
            }
            finally { Release(pattern); Release(element); }
        }
        finally { Release(matches); Release(condition); Release(undeafen); Release(deafen); Release(root); Release(automation); }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}

/// <summary>Undo focus/minimize changes made by Discord's accessibility action.</summary>
internal sealed class DiscordFocusGuard : IDisposable
{
    private readonly nint _discord, _foreground;
    private readonly bool _minimized;
    private readonly Action<bool> _report;
    public DiscordFocusGuard(nint discord, Action<bool> report)
    {
        _discord = discord; _report = report;
        _foreground = GetForegroundWindow(); _minimized = IsIconic(discord);
    }
    public static bool ShouldRestore(nint original, nint current, nint discord) =>
        original != 0 && original != discord && current == discord;
    public void Restore()
    {
        var current = GetForegroundWindow();
        // Do not interrupt a user who switched to some unrelated window during the action.
        if (ShouldRestore(_foreground, current, _discord) && IsWindow(_foreground))
            SetForegroundWindow(_foreground);
        if (_minimized && !IsIconic(_discord)) ShowWindow(_discord, 7); // SW_SHOWMINNOACTIVE
    }
    public void Dispose()
    {
        Restore();
        _report(GetForegroundWindow() == _foreground);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
}
