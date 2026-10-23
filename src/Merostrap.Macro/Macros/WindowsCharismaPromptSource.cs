using Merostrap.Macros.Health;
namespace Merostrap.Macros;
public sealed class WindowsCharismaPromptSource : ICharismaPromptSource
{
    private readonly WindowsHealthCapture _capture=new();private readonly CharismaPromptReader _reader=new();
    public CharismaFrame Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var window=WindowsHealthCapture.ForegroundRobloxWindow();var area=WindowsHealthCapture.ForegroundRobloxClientArea();
        if(window==nint.Zero || area is null)return new("","",false);
        var pixels=new HealthPixels(area.Width,area.Height,new byte[checked(area.Width*area.Height*4)]);
        // Only copy the centre UI; the rest of the in-memory canvas stays blank.
        int left=area.Width/3,top=area.Height*35/100,width=area.Width/3,height=area.Height*16/100;
        for(int y=0;y<height;y+=300) {
            token.ThrowIfCancellationRequested();if(window!=WindowsHealthCapture.ForegroundRobloxWindow())return new("","",false);
            int row=height-y<4?height-4:y;
            var part=_capture.Capture(new(area.X+left,area.Y+top+row,width,Math.Min(300,height-row)));
            for(int line=0;line<part.Height;line++)Array.Copy(part.Bgra,line*width*4,pixels.Bgra,((top+row+line)*area.Width+left)*4,width*4);
        }
        var frame=_reader.Analyze(pixels);token.ThrowIfCancellationRequested();
        if(window!=WindowsHealthCapture.ForegroundRobloxWindow() || area!=WindowsHealthCapture.ForegroundRobloxClientArea())return new("","",false);
        return frame with {InputArea=frame.InputArea is {} input ? input with {X=input.X+area.X,Y=input.Y+area.Y}:null};
    }
    public void Dispose()=>_reader.Dispose();
}
