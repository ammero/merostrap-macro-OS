using Merostrap.Macros.Health;
namespace Merostrap.Macros;
public sealed record DetectedDodgeIcon(HealthRegion Region, DodgeIconTrack Track, double Confidence);
public static class DodgeIconDetector
{
    // Ready dodge spiral sampled from the supplied gameplay clip; luma keeps colours/theme independent.
    private static readonly byte[] Reference=Convert.FromBase64String("XFlWYVdfV1pgXF96fHt7e3tnXHZ8fIJ5eWRhdX2EiIR/XUZeaGdZR16LX0NOY3qvsnpgTnpOZF51plVUqXRgfGVRZ4qSQWGdiGZsalFfdalUc8ZzYXtgTk1hfaxRVWRNckpmXUNdampcRFyDVkM=");
    private static readonly byte[][] References=[Reference,Convert.FromBase64String("3d7f4SQkJN/g4OHa3aGhJCQkoaHh4dkkoa7m5uauoSTj2CQk5kZGRuYkJOLaJCTmRuZG5iQk4tvaua5GRkauueHg2iQkJK6uNSQkJOLd3CQkubm5JCTb3+Dd29na2drZ2tzeJCQkJCQkJCQkJCQ=")];
    public static DetectedDodgeIcon? DetectInHud(HealthPixels pixels,Action<string>? trace=null)
    {
        if(pixels.Width<160||pixels.Height<40||pixels.Width>8192||pixels.Height>4320||pixels.Bgra.Length!=(long)pixels.Width*pixels.Height*4)return null;
        var anchors=new List<HealthRegion>();
        bool Orange(int x,int y){int i=(y*pixels.Width+x)*4;int b=pixels.Bgra[i],g=pixels.Bgra[i+1],r=pixels.Bgra[i+2];return r>90&&g>40&&r>g*1.2&&g>b*1.3;}
        int left=pixels.Width*15/100,right=pixels.Width*85/100,gapLimit=Math.Max(12,pixels.Width/25);
        for(int y=pixels.Height/2;y<pixels.Height;y+=2) {
            int start=-1,last=-1,gap=0;
            for(int x=left;x<=right;x++) {
                if(x<right&&Orange(x,y)){if(start<0)start=x;last=x;gap=0;}
                else if(start>=0&&(++gap>gapLimit||x==right)) {
                    int prefix=start;
                    while(prefix>left){int i=(y*pixels.Width+prefix-1)*4;if(!HealthBarAnalyzer.IsTemporaryHealthColour(pixels.Bgra[i+2],pixels.Bgra[i+1],pixels.Bgra[i]))break;prefix--;}
                    if(start-prefix>=3)start=prefix;
                    int width=last-start+1;
                    if(width>=Math.Max(60,pixels.Width/12)&&width<pixels.Width*.65&&
                        !anchors.Any(a=>Math.Abs(a.Y-y)<Math.Max(12,pixels.Height/40)&&Math.Abs(a.X-start)<20&&Math.Abs(a.Width-width)<30)) {
                        anchors.Add(new(start,y,width,4));
                        if(anchors.Count>=16)break;
                    }
                    start=-1;gap=0;
                }
            }
            if(anchors.Count>=16)break;
        }
        trace?.Invoke($"Dodge scan {pixels.Width}x{pixels.Height}; anchors={anchors.Count}");
        DetectedDodgeIcon? best=null;
        foreach(var anchor in anchors)if(Detect(pixels,anchor,trace) is {} icon&&(best is null||icon.Confidence>best.Confidence))best=icon;
        return best;
    }
    public static DetectedDodgeIcon? Detect(HealthPixels pixels, HealthRegion health,Action<string>? trace=null)
    {
        if(pixels.Width<160||pixels.Height<40||pixels.Bgra.Length!=(long)pixels.Width*pixels.Height*4||health.Width<60)return null;
        int left=Math.Max(0,health.X+(int)(health.Width*.30)),right=Math.Min(pixels.Width,health.X+(int)(health.Width*.57));
        int top=Math.Max(0,health.Y+health.Height),bottom=Math.Min(pixels.Height,top+(int)(health.Width*.12));
        double scale=health.Width/304d;double best=.78;HealthRegion? region=null;
        int min=Math.Max(6,(int)Math.Floor(9*scale)),max=Math.Min(96,(int)Math.Ceiling(14*scale));
        foreach(var reference in References)
        for(int w=min;w<=max;w++)for(int h=Math.Max(6,(int)(w*10d/11)-1);h<=Math.Min(96,(int)(w*10d/11)+2);h++)
        {
        if(!ReferenceEquals(reference,Reference) && w<14)continue;
        int nx=Math.Min(11,w),ny=Math.Min(10,h),count=nx*ny;
        var pattern=new double[count];
        var mask=new bool[count];
        for(int py=0;py<ny;py++)for(int px=0;px<nx;px++)pattern[py*nx+px]=reference[Math.Min(9,(int)((py+.5)*10/ny))*11+Math.Min(10,(int)((px+.5)*11/nx))];
        int samples=0;double mean=0;
        for(int py=0;py<ny;py++)for(int px=0;px<nx;px++) {
            // The ready spiral is opaque. Its outer frame and transparent corners are excluded.
            double dx=(px+.5)/nx-.5,dy=(py+.5)/ny-.5;
            int i=py*nx+px;mask[i]=ReferenceEquals(reference,Reference)||dx*dx+dy*dy<.105;
            if(mask[i]){mean+=pattern[i];samples++;}
        }
        mean/=samples;double energy=0;for(int i=0;i<count;i++)if(mask[i]){pattern[i]-=mean;energy+=pattern[i]*pattern[i];}
        for(int y=top;y+h<=bottom;y++)for(int x=left;x+w<=right;x++) {
            double sum=0,squares=0,dot=0;
            for(int ty=0;ty<ny;ty++)for(int tx=0;tx<nx;tx++) {
                if(!mask[ty*nx+tx])continue;
                int sx=x+Math.Min(w-1,(int)((tx+.5)*w/nx)),sy=y+Math.Min(h-1,(int)((ty+.5)*h/ny));
                int i=(sy*pixels.Width+sx)*4;
                double value=(pixels.Bgra[i]*29+pixels.Bgra[i+1]*150+pixels.Bgra[i+2]*77)/256d;
                sum+=value;squares+=value*value;dot+=pattern[ty*nx+tx]*value;
            }
            double variance=squares-sum*sum/samples;
            if(variance<samples*12*12)continue;
            double score=dot/Math.Sqrt(energy*variance);
            if(score>best){best=score;region=new(x,y,w,h);}
        }
        }
        trace?.Invoke($"Anchor {health}; best={best:F3}; icon={region}");
        if(region is null || best < (region.Width < 10 ? .78 : .90))return null;
        var data=new byte[region.Width*region.Height*4];
        for(int y=0;y<region.Height;y++)Array.Copy(pixels.Bgra,((region.Y+y)*pixels.Width+region.X)*4,data,y*region.Width*4,region.Width*4);
        var track=DodgeIconAnalyzer.Calibrate(new(region.Width,region.Height,data));
        return track is null?null:new(region,track,best);
    }
}



