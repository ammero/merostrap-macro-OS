using Merostrap.Macros.Health;
namespace Merostrap.Macros;
public sealed record DodgeIconTrack(int Width,int Height,double Contrast,double Peak,byte[]? Reference=null);
public static class DodgeIconAnalyzer
{
    static byte[] Luma(HealthPixels p){var result=new byte[p.Width*p.Height];for(int i=0;i<result.Length;i++)result[i]=(byte)((p.Bgra[i*4]*29+p.Bgra[i*4+1]*150+p.Bgra[i*4+2]*77)/256);return result;}
    static (double Contrast,double Peak)? Measure(HealthPixels p)
    {
        if(p.Width is <6 or >96||p.Height is <6 or >96||p.Bgra.Length!=(long)p.Width*p.Height*4)return null;
        var values=new int[p.Width*p.Height];
        for(int i=0;i<values.Length;i++)values[i]=(p.Bgra[i*4]*29+p.Bgra[i*4+1]*150+p.Bgra[i*4+2]*77)/256;
        Array.Sort(values);int count=Math.Max(2,values.Length/10);
        double peak=values.AsSpan(values.Length-count).ToArray().Average();double low=values.Take(count).Average();
        return (peak-low,peak);
    }
    public static DodgeIconTrack? Calibrate(HealthPixels p)=>Measure(p) is {} m&&m.Contrast>=45&&m.Peak>=130?new(p.Width,p.Height,m.Contrast,m.Peak,Luma(p)):null;
    public static bool? Read(HealthPixels p,DodgeIconTrack track)
    {
        if(p.Width!=track.Width||p.Height!=track.Height||Measure(p) is not {} m||m.Contrast<25||m.Peak<track.Peak*.5||m.Contrast>track.Contrast*1.8)return null;
        if(track.Reference is not {} reference||reference.Length!=p.Width*p.Height)return null;
        var current=Luma(p);
        // The ornamental border stays bright during cooldown. Only the opaque
        // spiral inside it can confirm readiness; transparent corners do not vote.
        var interior=new List<int>();
        for(int y=0;y<p.Height;y++)for(int x=0;x<p.Width;x++) {
            double dx=(x+.5)/p.Width-.5,dy=(y+.5)/p.Height-.5;
            if(dx*dx+dy*dy<.105)interior.Add(y*p.Width+x);
        }
        if(interior.Count<4)return null;
        var ordered=interior.Select(i=>reference[i]).Order().ToArray();
        double baseline=ordered.Take(Math.Max(2,ordered.Length/3)).Average(v=>(double)v);
        double peak=ordered.TakeLast(Math.Max(2,ordered.Length/4)).Average(v=>(double)v);
        if(peak-baseline<30)return null;
        double threshold=baseline+(peak-baseline)*.65;
        var highlights=interior.Where(i=>reference[i]>=threshold).ToArray();
        var anchors=interior.Where(i=>reference[i]<=baseline+12).ToArray();
        if(highlights.Length<2||anchors.Length<2)return null;
        double anchorError=anchors.Average(i=>Math.Abs(current[i]-reference[i]));
        if(anchorError>24)return null;
        double expected=highlights.Average(i=>(double)reference[i])-baseline;
        double observed=highlights.Average(i=>(double)current[i])-anchors.Average(i=>(double)current[i]);
        double ratio=observed/expected;
        return ratio>=.90&&ratio<=1.25?true:ratio<=.75?false:null;
    }
}
