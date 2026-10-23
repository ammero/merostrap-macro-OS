using System.ComponentModel;
using System.Runtime.InteropServices;
namespace Merostrap.Macros;
/// <summary>Copies only the recognized training phrase; called immediately before guarded Ctrl+V.</summary>
public static class WindowsClipboardText
{
    public static Task CopyAsync(string text,CancellationToken token) => Task.Run(()=>Copy(text,token),token);
    private static void Copy(string text,CancellationToken token)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        if(text.Length is <1 or >500)throw new ArgumentException("Invalid training phrase.");
        var owner=CreateWindowEx(0,"STATIC","",0,0,0,0,0,new nint(-3),nint.Zero,nint.Zero,nint.Zero);
        if(owner==nint.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot create clipboard owner.");
        try {
        for(int attempt=0;attempt<10;attempt++) {
            token.ThrowIfCancellationRequested();
            if(!OpenClipboard(owner)){if(token.WaitHandle.WaitOne(20))token.ThrowIfCancellationRequested();continue;}
            nint memory=nint.Zero;
            try {
                token.ThrowIfCancellationRequested();var bytes=System.Text.Encoding.Unicode.GetBytes(text+"\0");
                memory=GlobalAlloc(2,(nuint)bytes.Length);if(memory==nint.Zero)throw new Win32Exception();
                var pointer=GlobalLock(memory);if(pointer==nint.Zero)throw new Win32Exception();
                try {Marshal.Copy(bytes,0,pointer,bytes.Length);}finally {GlobalUnlock(memory);}
                if(!EmptyClipboard() || SetClipboardData(13,memory)==nint.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"Could not copy the charisma phrase.");
                memory=nint.Zero;return;
            }finally {if(memory!=nint.Zero)GlobalFree(memory);CloseClipboard();}
        }
        throw new InvalidOperationException("Clipboard is busy; Auto Charisma paused.");
        }finally {DestroyWindow(owner);}
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint CreateWindowEx(uint extended,string className,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool OpenClipboard(nint window);
    [DllImport("user32.dll")]private static extern bool CloseClipboard();
    [DllImport("user32.dll",SetLastError=true)]private static extern bool EmptyClipboard();
    [DllImport("user32.dll",SetLastError=true)]private static extern nint SetClipboardData(uint format,nint memory);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern nint GlobalAlloc(uint flags,nuint bytes);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")]private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")]private static extern nint GlobalFree(nint memory);
}
