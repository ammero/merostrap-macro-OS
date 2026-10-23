namespace Merostrap.Macros;

/// <summary>Owned and released by the UI thread for the whole macro session.</summary>
public sealed class MacroSessionLease : IDisposable
{
    private static readonly HashSet<string> Owned = [];
    private readonly string _name;
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _open;
    private readonly EventWaitHandle _stop;
    private readonly EventWaitHandle _start;
    private readonly EventWaitHandle _guiKeyUpdate;
    public bool IsOwner { get; private set; }
    public MacroSessionLease(string name = "Local\\Merostrap.OpenMacro.Session")
    {
        _name = name;
        _mutex = new Mutex(false, name);
        _open = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Open");
        _stop = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Stop");
        _start = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Start");
        _guiKeyUpdate = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".GuiKeyUpdate");
    }
    public bool TryAcquire()
    {
        if (IsOwner) return true;
        lock (Owned)
        {
            if (Owned.Contains(_name)) return false;
            try { IsOwner = _mutex.WaitOne(0); }
            catch (AbandonedMutexException) { IsOwner = true; }
            if (IsOwner) Owned.Add(_name);
            return IsOwner;
        }
    }
    public void RequestOpen() => _open.Set();
    public bool TakeOpenRequest() => IsOwner && _open.WaitOne(0);
    public void RequestStop() => _stop.Set();
    public bool TakeStopRequest() => IsOwner && _stop.WaitOne(0);
    public void RequestStart() => _start.Set();
    public bool TakeStartRequest() => IsOwner && _start.WaitOne(0);
    public void RequestGuiKeyUpdate() => _guiKeyUpdate.Set();
    public bool TakeGuiKeyUpdate() => IsOwner && _guiKeyUpdate.WaitOne(0);
    public void Dispose()
    {
        lock (Owned)
        {
            if (IsOwner) { _mutex.ReleaseMutex(); Owned.Remove(_name); IsOwner = false; }
        }
        _guiKeyUpdate.Dispose(); _start.Dispose(); _stop.Dispose(); _open.Dispose(); _mutex.Dispose();
    }
}
