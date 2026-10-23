// SPDX-License-Identifier: MPL-2.0
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Merostrap.Macros;

/// <summary>Native physical input hooks and scan-code output. No Python or background process is required.</summary>
public sealed class WindowsMacroInput : IMacroInput
{
    public IReadOnlySet<string>? GlobalKeys { get; init; }
    private readonly object _lifecycle = new();
    private HookSession? _session;
    private bool _disposed;
    public event Action<MacroInputEvent>? Input;
    public bool IsAvailable => OperatingSystem.IsWindows();
    public Exception? Failure => _session?.Error;
    public string PrimaryButton => IsAvailable && GetSystemMetrics(23) != 0 ? "mouse2" : "mouse1";
    public (int Width, int Height) ScreenSize => IsAvailable ? (GetSystemMetrics(0), GetSystemMetrics(1)) : (0, 0);
    public bool IsForegroundRoblox => CheckForegroundRoblox();
    public (int X,int Y,int Width,int Height)? RobloxScreenBounds => RobloxDisplay.InputBounds;
    public (int X,int Y,int Width,int Height) ScreenBoundsForPoint(int x,int y) => RobloxDisplay.BoundsForPoint(x,y) ?? throw new InvalidOperationException("The saved point is on a disconnected display. Pin the point again.");
    [ThreadStatic] private static char[]? _processImageBuffer;
    internal static bool CheckForegroundRoblox()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var processId);
        // Query only this process, rather than constructing Process/input objects on
        // every key event and every health sample. Never cache foreground identity.
        var process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return false;
        try
        {
            var buffer = _processImageBuffer ??= new char[1024];
            var size = (uint)buffer.Length;
            if (!QueryFullProcessImageName(process, 0, buffer, ref size))
            {
                if (Marshal.GetLastWin32Error() != 122 || buffer.Length >= 32768) return false;
                buffer = _processImageBuffer = new char[32768];
                size = (uint)buffer.Length;
                if (!QueryFullProcessImageName(process, 0, buffer, ref size)) return false;
            }
            var name = System.IO.Path.GetFileName(buffer.AsSpan(0, checked((int)size)));
            bool roblox = name.Equals("RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase) ||
                          name.Equals("RobloxPlayer.exe", StringComparison.OrdinalIgnoreCase);
            if (!roblox || GetForegroundWindow() != window) return false;
            GetWindowThreadProcessId(window, out var currentProcessId);
            return currentProcessId == processId;
        }
        finally { CloseHandle(process); }
    }
    public void Start(Func<MacroInputEvent, bool> suppress)
    {
        ArgumentNullException.ThrowIfNull(suppress);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsAvailable) throw new PlatformNotSupportedException("Windows input automation requires Windows.");
            Stop();
            var session = new HookSession(this, suppress);
            _session = session;
            session.Thread.Start();
            if (!session.Ready.Wait(TimeSpan.FromSeconds(2))) { Stop(); throw new TimeoutException("Windows input hooks did not start within two seconds."); }
            if (session.Error != null) { Stop(); throw new InvalidOperationException("Could not install Windows input hooks.", session.Error); }
        }
    }
    public IReadOnlyCollection<string> SnapshotRitualKeysDown()
    {
        if (!IsAvailable) return Array.Empty<string>();
        var down = new List<string>();
        foreach (var character in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")
            if ((GetAsyncKeyState(character) & 0x8000) != 0)
                down.Add(char.ToLowerInvariant(character).ToString());
        return down;
    }
    public void Stop()
    {
        lock (_lifecycle)
        {
            var session = _session; _session = null;
            if (session == null) return;
            Volatile.Write(ref session.Stopping, true);
            // Unhook synchronously, then let the message thread finish independently.
            session.Unhook();
            if (session.ThreadId != 0) PostThreadMessage(session.ThreadId, 0x0012, UIntPtr.Zero, IntPtr.Zero);
        }
    }
    public void Key(string key, bool down)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException();
        key = MacroKeys.Normalize(key);
        if (!MacroKeys.TryVirtualKey(key, out var code)) throw new ArgumentException("Unknown input key: " + key, nameof(key));
        NativeInput input;
        if (key.StartsWith("mouse", StringComparison.Ordinal))
        {
            var flags = key switch { "mouse1" => down ? 0x0002u : 0x0004u, "mouse2" => down ? 0x0008u : 0x0010u, "mouse3" => down ? 0x0020u : 0x0040u, _ => down ? 0x0080u : 0x0100u };
            input = new NativeInput { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = key == "mouse4" ? 1u : key == "mouse5" ? 2u : 0u } } };
        }
        else
        {
            var scan = MapVirtualKey(code, 0);
            if (scan == 0) throw new InvalidOperationException("Windows cannot map input key: " + key);
            var extended = code is >= 33 and <= 46 or 111 || key == "numpad enter";
            input = new NativeInput { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = (ushort)scan, Flags = 0x0008u | (down ? 0u : 0x0002u) | (extended ? 0x0001u : 0u) } } };
        }
        Send(input);
    }
    public void Move(int x, int y)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException();
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        if (x < left || x >= (long)left + width || y < top || y >= (long)top + height)
            throw new ArgumentOutOfRangeException(nameof(x), "Coordinates are outside the current desktop.");
        Send(new NativeInput {Type=0,Data=new InputUnion {Mouse=new MouseInput {
            X=(int)((long)(x-left)*65535/(width-1)),Y=(int)((long)(y-top)*65535/(height-1)),Flags=0x0001|0x8000|0x4000}}});
    }
    public void Scroll(int delta)
    {
        if (!IsAvailable) throw new PlatformNotSupportedException();
        if (delta is 0 or < -1200 or > 1200) throw new ArgumentOutOfRangeException(nameof(delta));
        Send(new NativeInput { Type = 0, Data = new InputUnion { Mouse = new MouseInput { MouseData = unchecked((uint)delta), Flags = 0x0800 } } });
    }
    public void Character(char character)
    {
        // Both halves are one SendInput transaction; cancellation cannot leave a Unicode key held.
        var inputs = new[]
        {
            new NativeInput { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 0x0004 } } },
            new NativeInput { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 0x0004 | 0x0002 } } }
        };
        if (!IsAvailable) throw new PlatformNotSupportedException();
        if (SendInput(2, inputs, Marshal.SizeOf<NativeInput>()) != 2) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows blocked text input.");
    }
    private static void Send(NativeInput input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows blocked macro input. Roblox and Merostrap must use compatible privilege levels.");
    }
    public void Dispose() { lock (_lifecycle) { if (_disposed) return; Stop(); _disposed = true; } }

    private sealed class HookSession
    {
        private readonly WindowsMacroInput _owner;
        private readonly Func<MacroInputEvent, bool> _suppress;
        private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);
        private readonly MacroPhysicalState _physicalState = new();
        private long _sequence;
        private readonly object _hooks = new();
        private readonly HookCallback _keyboardCallback;
        private readonly HookCallback _mouseCallback;
        private readonly MacroInputDispatcher _dispatcher;
        private IntPtr _keyboard;
        private IntPtr _mouse;
        private int _generation;
        public int Generation => Volatile.Read(ref _generation);
        private UIntPtr _renewalTimer;
        public Thread Thread { get; }
        public ManualResetEventSlim Ready { get; } = new(false);
        public Exception? Error { get; private set; }
        public uint ThreadId;
        public bool Stopping;
        public HookSession(WindowsMacroInput owner, Func<MacroInputEvent, bool> suppress)
        {
            _owner = owner; _suppress = suppress;
            _keyboardCallback = Keyboard; _mouseCallback = Mouse;
            // Capture the subscribers for this session so a stopped hook cannot invoke a replacement session.
            var receive = owner.Input;
            _dispatcher = new MacroInputDispatcher(input => { if (!Volatile.Read(ref Stopping)) receive?.Invoke(input); }, e => Error = e);
            Thread = new Thread(Pump) { IsBackground = true, Name = "Merostrap macro input" };
        }
        private void Pump()
        {
            try
            {
                ThreadId = GetCurrentThreadId();
                PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // Create this thread's message queue before reporting ready.
                RenewHooks();
                // Windows can silently remove low-level hooks after a callback timeout.
                // Renew on the hook thread even when no input arrives. Retain blocked
                // key and dispatcher state so renewal never strands a swallowed key-up.
                _renewalTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 30000, IntPtr.Zero);
                if (_renewalTimer == UIntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                Ready.Set();
                while (!Volatile.Read(ref Stopping))
                {
                    var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                    if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (result == 0) break;
                    if (message.Id == 0x0113 && message.WParam == _renewalTimer) { RenewHooks(); continue; }
                    TranslateMessage(ref message); DispatchMessage(ref message);
                }
                if (!Volatile.Read(ref Stopping)) Error = new InvalidOperationException("Windows input hook message loop exited.");
            }
            catch (Exception e) { Error = e; }
            finally
            {
                if (_renewalTimer != UIntPtr.Zero) KillTimer(IntPtr.Zero, _renewalTimer);
                Unhook(); Ready.Set();
            }
        }
        private void RenewHooks()
        {
            lock (_hooks)
            {
                if (Volatile.Read(ref Stopping)) return;
                var keyboard = SetWindowsHookEx(13, _keyboardCallback, GetModuleHandle(null), 0);
                if (keyboard == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var mouse = SetWindowsHookEx(14, _mouseCallback, GetModuleHandle(null), 0);
                if (mouse == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    UnhookWindowsHookEx(keyboard);
                    throw new Win32Exception(error);
                }
                if (_keyboard != IntPtr.Zero) UnhookWindowsHookEx(_keyboard);
                if (_mouse != IntPtr.Zero) UnhookWindowsHookEx(_mouse);
                _keyboard = keyboard; _mouse = mouse;
                Interlocked.Increment(ref _generation);
            }
            foreach (var key in _physicalState.MissingReleases(code => (GetAsyncKeyState(code) & 0x8000) != 0))
            {
                if (Volatile.Read(ref Stopping)) break;
                Publish(new(key, false));
            }
        }
        public void Unhook()
        {
            lock (_hooks)
            {
                _dispatcher.Dispose();
                if (_keyboard != IntPtr.Zero) { UnhookWindowsHookEx(_keyboard); _keyboard = IntPtr.Zero; }
                if (_mouse != IntPtr.Zero) { UnhookWindowsHookEx(_mouse); _mouse = IntPtr.Zero; }
            }
        }
        private IntPtr Keyboard(int code, UIntPtr message, IntPtr data)
        {
            if (code >= 0 && !Volatile.Read(ref Stopping))
            {
                var input = Marshal.PtrToStructure<KeyboardHook>(data);
                var kind = message.ToUInt32();
                if ((input.Flags & 0x10) == 0 && kind is 0x0100 or 0x0101 or 0x0104 or 0x0105)
                {
                    var key = MacroKeys.FromKeyboard(input.VirtualKey, input.Scan, (input.Flags & 1) != 0);
                    if (key.Length > 0 && Publish(new(key, kind is 0x0100 or 0x0104), checked((int)input.VirtualKey))) return (IntPtr)1;
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }
        private IntPtr Mouse(int code, UIntPtr message, IntPtr data)
        {
            if (code >= 0 && !Volatile.Read(ref Stopping))
            {
                var input = Marshal.PtrToStructure<MouseHook>(data);
                if ((input.Flags & 1) == 0)
                {
                    var kind = message.ToUInt32();
                    var key = kind switch { 0x0201 or 0x0202 => "mouse1", 0x0204 or 0x0205 => "mouse2", 0x0207 or 0x0208 => "mouse3", 0x020B or 0x020C => (input.MouseData >> 16) == 1 ? "mouse4" : "mouse5", _ => "" };
                    if (key.Length > 0 && Publish(new(key, kind is 0x0201 or 0x0204 or 0x0207 or 0x020B))) return (IntPtr)1;
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }
        private bool Publish(MacroInputEvent input, int? virtualKey = null)
        {
            try
            {
                input = input with { Sequence = ++_sequence };
                _physicalState.Observe(input, virtualKey);
                var foreground = _owner.IsForegroundRoblox;
                if (!foreground) _blocked.Clear();
                // Observe every physical edge, including releases and repeated blocked downs.
                var requested = _suppress(input);
                var blocked = foreground && (input.IsDown ? _blocked.Contains(input.Key) || requested : _blocked.Remove(input.Key));
                if (blocked && input.IsDown) _blocked.Add(input.Key);
                _dispatcher.Publish(input, ShouldDispatchPhysicalInput(input, foreground, _owner.GlobalKeys));
                return blocked;
            }
            catch (Exception e) { Error = e; return false; } // Exceptions must never cross a native callback boundary.
        }
    }

    internal static bool ShouldDispatchPhysicalInput(MacroInputEvent input, bool foreground, IReadOnlySet<string>? globalKeys) =>
        !input.IsInjected && (globalKeys is null ? foreground || input.Key == "esc" : globalKeys.Contains(input.Key));

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr HookCallback(int code, UIntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardHook { public uint VirtualKey, Scan, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHook { public Point Point; public uint MouseData, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Point; public uint Private; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] name, ref uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookCallback callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, UIntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll", SetLastError = true)] private static extern UIntPtr SetTimer(IntPtr window, UIntPtr id, uint milliseconds, IntPtr callback);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(IntPtr window, UIntPtr id);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}

/// <summary>Hook-thread state used to reconcile releases missed while Windows removed a hook.</summary>
internal sealed class MacroPhysicalState
{
    private readonly Dictionary<string, int> _down = new(StringComparer.Ordinal);
    public void Observe(MacroInputEvent input, int? virtualKey = null)
    {
        if (input.IsInjected) return;
        if (!input.IsDown) { _down.Remove(input.Key); return; }
        // Retain the actual VK: Num Lock changes a numpad key's VK while
        // FromKeyboard deliberately keeps the same scan-code-based name.
        if (virtualKey is { } native) _down[input.Key] = native;
        else if (MacroKeys.TryVirtualKey(input.Key, out var code)) _down[input.Key] = code;
    }
    public string[] MissingReleases(Func<int, bool> isDown) => _down.Where(pair => !isDown(pair.Value)).Select(pair => pair.Key).ToArray();
}

/// <summary>Physical monitor coordinates; never assumes the laptop panel is the game display.</summary>
public sealed record MacroDisplayOption(string Id,string Label)
{
    public override string ToString()=>Label;
}
public static class RobloxDisplay
{
    private static string _selectedDevice="";
    public static string SelectedDevice {get=>Volatile.Read(ref _selectedDevice);set=>Volatile.Write(ref _selectedDevice,value??"");}
    public static IReadOnlyList<MacroDisplayOption> Options()
    {
        var result=new List<MacroDisplayOption>{new("","Follow Roblox")};
        foreach(var display in Displays())result.Add(new(display.Device,$"Display {display.Device.Replace(@"\\.\DISPLAY","")} - {display.Bounds.Width} x {display.Bounds.Height}"));
        return result;
    }
    public static (int X,int Y,int Width,int Height)? TargetBounds => SelectedDevice.Length==0 ? CurrentBounds : Displays().Where(d=>d.Device==SelectedDevice).Select(d=>((int,int,int,int)?)d.Bounds).FirstOrDefault();
    public static (int X,int Y,int Width,int Height)? InputBounds
    {
        get {var actual=CurrentBounds;var target=TargetBounds;return actual==target ? target : null;}
    }
    private static List<(string Device,(int X,int Y,int Width,int Height) Bounds)> Displays()
    {
        var result=new List<(string,(int,int,int,int))>();
        if(!OperatingSystem.IsWindows())return result;
        MonitorEnum callback=(monitor,_,_,_)=>
        {
            var info=new MonitorInfo{Size=(uint)Marshal.SizeOf<MonitorInfo>()};
            if(GetMonitorInfo(monitor,ref info))result.Add((info.Device,(info.Monitor.Left,info.Monitor.Top,info.Monitor.Right-info.Monitor.Left,info.Monitor.Bottom-info.Monitor.Top)));
            return true;
        };
        EnumDisplayMonitors(0,0,callback,0);GC.KeepAlive(callback);return result;
    }
    public static (int X,int Y,int Width,int Height)? CurrentBounds
    {
        get
        {
            if(!WindowsMacroInput.CheckForegroundRoblox())return null;
            var window=GetForegroundWindow();
            var bounds=Bounds(MonitorFromWindow(window,2));
            return GetForegroundWindow()==window ? bounds : null;
        }
    }
    public static (int X,int Y,int Width,int Height)? BoundsForPoint(int x,int y)
        => OperatingSystem.IsWindows() ? Bounds(MonitorFromPoint(new NativePoint{X=x,Y=y},0)) : null;
    public static (int X,int Y) MapPoint((int X,int Y) point,(int X,int Y,int Width,int Height) source,(int X,int Y,int Width,int Height) target)
    {
        if(source.Width<=1 || source.Height<=1 || target.Width<=1 || target.Height<=1)throw new ArgumentException("Monitor dimensions unavailable.");
        if(point.X<source.X || point.Y<source.Y || (long)point.X>= (long)source.X+source.Width || (long)point.Y>= (long)source.Y+source.Height)
            throw new ArgumentException("The saved point is outside its display. Pin the point again.");
        if(source==target)return point;
        double x=Math.Clamp((point.X-(double)source.X)/(source.Width-1),0,1);
        double y=Math.Clamp((point.Y-(double)source.Y)/(source.Height-1),0,1);
        return (target.X+(int)Math.Round(x*(target.Width-1)),target.Y+(int)Math.Round(y*(target.Height-1)));
    }
    private static (int X,int Y,int Width,int Height)? Bounds(nint monitor)
    {
        var info=new MonitorInfo{Size=(uint)Marshal.SizeOf<MonitorInfo>()};
        return monitor!=0 && GetMonitorInfo(monitor,ref info) ? (info.Monitor.Left,info.Monitor.Top,info.Monitor.Right-info.Monitor.Left,info.Monitor.Bottom-info.Monitor.Top) : null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct MonitorInfo{public uint Size;public NativeRect Monitor,Work;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Device;}
    private delegate bool MonitorEnum(nint monitor,nint dc,nint rect,nint data);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool EnumDisplayMonitors(nint dc,nint clip,MonitorEnum callback,nint data);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern nint MonitorFromWindow(nint window,uint flags);
    [DllImport("user32.dll")]private static extern nint MonitorFromPoint(NativePoint point,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
}
