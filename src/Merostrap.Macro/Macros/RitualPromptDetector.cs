using Merostrap.Macros.Health;
namespace Merostrap.Macros;

/// <summary>Finds the outlined prompt frame and its visible glyphs without reading a heading.</summary>
public static class RitualPromptDetector
{
    public enum OutlineState { Absent, Visible, Uncertain }
    public static RitualFrame GateRecognition(Func<RitualFrame> recognize, OutlineState outline) =>
        GateRecognition(outline==OutlineState.Visible ? recognize() : new([]),outline);
    public static RitualFrame GateRecognition(RitualFrame frame, OutlineState outline)
    {
        // With no ritual outline, recognized shapes belong to scenery. They must
        // neither authorize input nor prevent resetting the previous cast.
        if (outline == OutlineState.Absent) return new([], true, true);
        return frame with { Reliable = frame.Reliable && outline == OutlineState.Visible, PromptAbsent = false };
    }
    private readonly record struct Line(int Left, int Right, int Y);
    public static HealthRegion? Detect(HealthPixels pixels, bool requireGlyphs = true) => DetectCore(pixels,requireGlyphs,out _);
    public static OutlineState ReadOutline(HealthPixels pixels)
    {
        var frame=DetectCore(pixels,false,out bool uncertain);
        return frame is not null ? OutlineState.Visible : uncertain ? OutlineState.Uncertain : OutlineState.Absent;
    }
    public static OutlineState ReadOutline(HealthPixels pixels,HealthRegion expectedInterior)
    {
        var found=DetectCore(pixels,false,out bool uncertain);
        if(found is null)return uncertain ? OutlineState.Uncertain : OutlineState.Absent;
        // A different rectangle inside the saved area is not the calibrated ritual panel.
        return Math.Abs(found.X-expectedInterior.X)<=3 && Math.Abs(found.Y-expectedInterior.Y)<=3 &&
            Math.Abs(found.Width-expectedInterior.Width)<=6 && Math.Abs(found.Height-expectedInterior.Height)<=6
            ? OutlineState.Visible : OutlineState.Uncertain;
    }
    private static HealthRegion? DetectCore(HealthPixels pixels, bool requireGlyphs, out bool uncertain)
    {
        uncertain=true;
        if (pixels is null || pixels.Width is < 160 or > 4096 || pixels.Height is < 40 or > 2160 || pixels.Bgra is null || pixels.Bgra.Length != (long)pixels.Width*pixels.Height*4) return null;
        int w=pixels.Width,h=pixels.Height;
        int Bright(int x,int y) { int p=(y*w+x)*4;return (pixels.Bgra[p]+pixels.Bgra[p+1]+pixels.Bgra[p+2])/3; }
        bool Edge(int x,int y,bool horizontal)
        {
            int p=(y*w+x)*4,b=pixels.Bgra[p],g=pixels.Bgra[p+1],r=pixels.Bgra[p+2];
            if(Math.Min(b,Math.Min(g,r))<65 || Math.Max(b,Math.Max(g,r))-Math.Min(b,Math.Min(g,r))>45)return false;
            int center=Bright(x,y);
            return horizontal ? Math.Abs(center-Bright(x,y-2))>=15 || Math.Abs(center-Bright(x,y+2))>=15 : Math.Abs(center-Bright(x-2,y))>=15 || Math.Abs(center-Bright(x+2,y))>=15;
        }
        var lines=new List<Line>();
        for(int y=2;y<h-2;y++)
        {
            int start=-1,last=-1,count=0;
            for(int x=2;x<w-2;x++)
            {
                if(Edge(x,y,true)) { if(start<0)start=x;last=x;count++; }
                if(start>=0 && (x-last>6 || x==w-3))
                {
                    int length=last-start+1;
                    if(length is >=120 and <=1100 && count>=length*.75)lines.Add(new(start,last,y));
                    start=-1;count=0;
                }
            }
        }
        if(lines.Count>2048)return null;
        var regions=new List<HealthRegion>();
        int pairs=0,candidates=0;
        using var reader=new RitualPromptReader();
        foreach(var top in lines)
        foreach(var bottom in lines)
        {
            if(++pairs>262144)return null;
            int height=bottom.Y-top.Y,width=top.Right-top.Left;
            if(height is <30 or >288 || width<height*2 || width>height*10 || Math.Abs(top.Left-bottom.Left)>5 || Math.Abs(top.Right-bottom.Right)>5)continue;
            if(++candidates>256)return null;
            int leftCount=0,rightCount=0,leftGap=0,rightGap=0,maxLeftGap=0,maxRightGap=0;
            for(int y=top.Y+3;y<bottom.Y-2;y++)
            {
                if(Enumerable.Range(Math.Max(2,top.Left-2),5).Any(x=>x<w-2 && Edge(x,y,false))) { leftCount++;leftGap=0; }
                else maxLeftGap=Math.Max(maxLeftGap,++leftGap);
                if(Enumerable.Range(Math.Max(2,top.Right-2),5).Any(x=>x<w-2 && Edge(x,y,false))) { rightCount++;rightGap=0; }
                else maxRightGap=Math.Max(maxRightGap,++rightGap);
            }
            if(leftCount<height*.55 || rightCount<height*.55 || maxLeftGap>8 || maxRightGap>8)continue;
            var region=new HealthRegion(top.Left+3,top.Y+3,width-5,height-5);
            if(regions.Any(old=>Math.Abs(old.X-region.X)<=6 && Math.Abs(old.Y-region.Y)<=6 && Math.Abs(old.Width-region.Width)<=10 && Math.Abs(old.Height-region.Height)<=10))continue;
            regions.Add(region);
        }
        // Grid separators form nested rectangles inside a single continuous
        // frame. Select its outer boundary before reading the complete queue.
        var outer=regions.Where(inner=>!regions.Any(other=>other!=inner &&
            other.X<=inner.X+3 && other.Y<=inner.Y+3 &&
            other.X+other.Width>=inner.X+inner.Width-3 && other.Y+other.Height>=inner.Y+inner.Height-3 &&
            (other.Width>inner.Width+6 || other.Height>inner.Height+6))).ToArray();
        if(requireGlyphs) outer=outer.Where(region=>
        {
            var frame=reader.Analyze(HudCrop(pixels,region));
            return frame.Reliable && frame.Glyphs.Count is >=1 and <=12 && frame.Glyphs.All(g=>g.Character is not null);
        }).ToArray();
        if(outer.Length>1)return null;
        uncertain=false;
        return outer.Length==1?outer[0]:null;
    }
    public static HealthPixels HudCrop(HealthPixels source,HealthRegion region)
    {
        region.Validate();
        if(region.X<0 || region.Y<0 || (long)region.X+region.Width>source.Width || (long)region.Y+region.Height>source.Height)throw new ArgumentException("Prompt area is outside the image.");
        var bytes=new byte[region.Width*region.Height*4];
        for(int y=0;y<region.Height;y++)Array.Copy(source.Bgra,((region.Y+y)*source.Width+region.X)*4,bytes,y*region.Width*4,region.Width*4);
        return new(region.Width,region.Height,bytes);
    }
}
