using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
namespace Merostrap.Macros;

public sealed record GankWindowOption(string Id,string Label);
internal static class GankWindowCapture
{
    private static readonly SemaphoreSlim CaptureGate=new(1,1);
    public static IReadOnlyList<GankWindowOption> List()
    {
        var result=new List<GankWindowOption>{new("","No screenshot")};
        if (!OperatingSystem.IsWindows()) return result;
        EnumWindows((handle,_)=> {
            if (!IsWindowVisible(handle) || IsIconic(handle)) return true;
            var title=new StringBuilder(512);GetWindowText(handle,title,title.Capacity);
            if (title.Length==0) return true;
            GetWindowThreadProcessId(handle,out var pid);
            if (pid==Environment.ProcessId) return true;
            try { using var process=Process.GetProcessById((int)pid);result.Add(new($"{pid}:{handle.ToInt64()}:{process.StartTime.ToUniversalTime().Ticks}",$"{title} ({process.ProcessName})")); } catch {}
            return true;
        },0);
        return result.Take(1).Concat(result.Skip(1).OrderBy(w=>w.Label,StringComparer.OrdinalIgnoreCase)).ToArray();
    }
    public static async Task<byte[]> CaptureAsync(string id,CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Window screenshots require Windows.");
        if (!await CaptureGate.WaitAsync(0,token)) throw new InvalidOperationException("A window capture is still busy. Try again later.");
        var task=Task.Run(()=> { try { token.ThrowIfCancellationRequested();return Capture(id,token); } finally { CaptureGate.Release(); } });
        try { return await task.WaitAsync(TimeSpan.FromSeconds(3),token); }
        catch (TimeoutException) { throw new InvalidOperationException("Selected window did not respond to capture."); }
    }
    private static nint Resolve(string id)
    {
        var parts=id.Split(':');
        if (parts.Length!=3 || !int.TryParse(parts[0],out var pid) || !long.TryParse(parts[1],out var value) || !long.TryParse(parts[2],out var started)) throw new InvalidOperationException("Select a screenshot window again.");
        var handle=new nint(value);GetWindowThreadProcessId(handle,out var owner);
        try { using var process=Process.GetProcessById(pid);if(owner!=pid || process.StartTime.ToUniversalTime().Ticks!=started || !IsWindowVisible(handle) || IsIconic(handle)) throw new InvalidOperationException(); }
        catch { throw new InvalidOperationException("Selected window is closed or minimized. Select it again."); }
        return handle;
    }
    private static byte[] Capture(string id,CancellationToken token)
    {
        var handle=Resolve(id);
        if (!GetWindowRect(handle,out var rect)) throw new InvalidOperationException("Cannot read the selected window size.");
        int width=rect.Right-rect.Left,height=rect.Bottom-rect.Top;
        if (width<=0 || height<=0 || (long)width*height>16000000) throw new InvalidOperationException("Selected window size cannot be captured.");
        var screen=GetDC(0);nint memory=0,bitmap=0,previous=0;
        try
        {
            memory=CreateCompatibleDC(screen);
            var info=new BitmapInfo {Header=new(){Size=(uint)Marshal.SizeOf<BitmapHeader>(),Width=width,Height=-height,Planes=1,BitCount=32}};
            bitmap=CreateDIBSection(screen,ref info,0,out var pixels,0,0);
            if (screen==0 || memory==0 || bitmap==0 || pixels==0) throw new InvalidOperationException("Could not allocate a screenshot.");
            previous=SelectObject(memory,bitmap);
            if (!PrintWindow(handle,memory,2)) throw new InvalidOperationException("Selected window does not support screenshots.");
            token.ThrowIfCancellationRequested();Resolve(id);
            var bytes=new byte[checked(width*height*4)];Marshal.Copy(pixels,bytes,0,bytes.Length);
            bool hasImage=false;for(int i=0;i<bytes.Length;i+=4) if(bytes[i]!=0 || bytes[i+1]!=0 || bytes[i+2]!=0){hasImage=true;break;}
            if(!hasImage) throw new InvalidOperationException("Selected window returned a blank screenshot. No alert was sent.");
            using var image=new Bitmap(PixelFormat.Bgra8888,AlphaFormat.Opaque,pixels,new PixelSize(width,height),new Vector(96,96),width*4);
            using var output=new MemoryStream();image.Save(output, PngBitmapEncoderOptions.Default);token.ThrowIfCancellationRequested();return output.ToArray();
        }
        finally {if(previous!=0 && previous!=-1)SelectObject(memory,previous);if(bitmap!=0)DeleteObject(bitmap);if(memory!=0)DeleteDC(memory);if(screen!=0)ReleaseDC(0,screen);}
    }
    private delegate bool WindowCallback(nint window,nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader {public uint Size;public int Width,Height;public ushort Planes,BitCount;public uint Compression,ImageSize;public int XPixelsPerMeter,YPixelsPerMeter;public uint ColorsUsed,ImportantColors;}
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo {public BitmapHeader Header;public uint Color;}
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback,nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(nint window,StringBuilder text,int length);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window,out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window,nint dc,uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window,nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc,ref BitmapInfo info,uint usage,out nint pixels,nint section,uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc,nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
