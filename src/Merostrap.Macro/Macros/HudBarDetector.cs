using Merostrap.Macros.Health;

namespace Merostrap.Macros;

public sealed record DetectedParryBar(HealthRegion Parry, ParryTrack Track);

public sealed record DetectedHudBars(HealthRegion Health, HealthRegion Parry, ParryTrack Track);

/// <summary>Find the centred, ready gradient strip and orange health fill beneath it.</summary>
public static class HudBarDetector
{
    public static DetectedHudBars? Detect(HealthPixels p)
    {
        var parry = DetectParry(p);
        return parry is not null && FindHealth(p, parry.Parry) is { } health ? new(health,parry.Parry,parry.Track) : null;
    }
    public static DetectedParryBar? DetectParry(HealthPixels p)
    {
        if (p.Width < 160 || p.Width > 4096 || p.Height < 40 || p.Height > 1200 || p.Bgra.Length != (long)p.Width * p.Height * 4) return null;
        var candidates = new List<DetectedParryBar>();
        for (int y = 0; y < p.Height - 12; y++)
        {
            int start = -1, last = -1, gap = 0;
            for (int x = 0; x <= p.Width; x++)
            {
                bool coloured = x < p.Width && GradientColour(p, x, y);
                if (coloured) { if (start < 0) start = x; last = x; gap = 0; }
                else if (start >= 0 && (++gap > 3 || x == p.Width) &&
                    (x == p.Width || !ParryBarAnalyzer.InsideSpectralLabel(x, p.Width / 2d, p.Width - 2d * start)))
                {
                    int width = last - start + 1;
                    if (width >= Math.Clamp(p.Width / 24,60,120) && width < p.Width * .8 && Math.Abs((start + last) / 2d - p.Width / 2d) < Math.Max(8, width * .025))
                    {
                        var parry = new HealthRegion(start, Math.Max(0, y - 1), width, 4);
                        var track = ParryBarAnalyzer.Calibrate(Crop(p, parry));
                        if (track is not null)
                        {
                            int existing = candidates.FindIndex(c => Math.Abs(c.Parry.Y - parry.Y) < 5);
                            var candidate = new DetectedParryBar(parry, track);
                            if (existing < 0) candidates.Add(candidate);
                            else if (parry.Width > candidates[existing].Parry.Width) candidates[existing] = candidate;
                        }
                    }
                    start = -1; last = -1; gap = 0;
                }
            }
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>Finds a centred health fill independently of parry readiness.</summary>
    public static HealthRegion? DetectHealth(HealthPixels p) => FindHealthCandidate(p,null);
    public static HealthRegion? DetectHealthVerified(HealthPixels p, Func<HealthPixels,bool> verify) => FindHealthCandidate(p,verify);
    public static HealthRegion? DetectHealthByLabel(HealthPixels p, Func<HealthPixels,HealthLabel?> locate, Func<HealthPixels,bool> verify) => FindHealthCandidate(p,verify,locate);
    public static HealthRegion? DetectHealthInHud(HealthPixels p, Func<HealthPixels,HealthLabel?> locate, Func<HealthPixels,bool> verify,Action<string>? trace=null) => FindHealthCandidate(p,verify,locate,true,trace:trace) ?? FindHealthCandidate(p,verify,locate,true,true,trace) ?? FindHealthCandidate(p,verify,locate,true,false,trace,true);
    private static HealthRegion? FindHealthCandidate(HealthPixels p, Func<HealthPixels,bool>? verify, Func<HealthPixels,HealthLabel?>? locate = null, bool hudOnly = false,bool widened=false,Action<string>? trace=null,bool textContext=false)
    {
        if (p.Width is < 160 or > 4096 || p.Height is < 40 or > 1200 || p.Bgra is null || p.Bgra.LongLength != (long)p.Width*p.Height*4) return null;
        var bands = new List<(int Top,int Bottom,int Left)>();
        // Deepwoken health sits in the lower central HUD, above the hotbar.
        // Preserve full-frame coordinates while excluding unrelated scenery.
        int scanTop=hudOnly ? Math.Max(0,p.Height-(int)Math.Ceiling(p.Width*.18)) : 0;
        int scanLeft=hudOnly ? p.Width/10 : 0, scanRight=hudOnly ? p.Width-p.Width/10 : p.Width;
        for (int y=scanTop;y<p.Height;y++)
        {
            // Inspect separate horizontal runs so coloured scenery elsewhere on
            // the same scanline cannot move the health bar's left edge.
            int first=-1,last=-1,count=0,gap=0;
            for(int x=scanLeft;x<=scanRight;x++)
            {
                int i=(y*p.Width+Math.Min(x,p.Width-1))*4;
                bool live=x<scanRight && HealthBarAnalyzer.IsLiveHealthColour(p.Bgra[i+2],p.Bgra[i+1],p.Bgra[i]);
                if(live) {if(first<0)first=x;last=x;count++;gap=0;continue;}
                if(first<0 || (++gap<=3 && x<scanRight))continue;
                int left=first;
                while(left>scanLeft)
                {
                    int j=(y*p.Width+left-1)*4;
                    if(!HealthBarAnalyzer.IsTemporaryHealthColour(p.Bgra[j+2],p.Bgra[j+1],p.Bgra[j]))break;
                    left--;
                }
                int width=p.Width-2*left;
                if(count>=12 && left>=2 && width>=120 && width<=p.Width*.8 && (last<left+width+3 || locate is not null))
                {
                    // Ornate frame teeth shift the fill edge several pixels between rows.
                    // Keep those rows together so the HEALTH text is not clipped.
                    int index=bands.FindLastIndex(b=>y-b.Bottom<=3 && Math.Abs(left-b.Left)<=Math.Max(3,width/80));
                    if(index<0)bands.Add((y,y,left));
                    else {var band=bands[index];bands[index]=(band.Top,y,Math.Min(left,band.Left));}
                }
                first=-1;last=-1;count=0;gap=0;
            }
        }
        var candidates=new List<HealthRegion>();
        foreach(var band in hudOnly ? bands.OrderBy(b=>Math.Abs((b.Top+b.Bottom)/2d-(p.Height-p.Width*.07))).ThenBy(b=>Math.Abs(b.Left-p.Width*.375)) : bands.AsEnumerable())
        {
            int width=p.Width-2*band.Left, height=band.Bottom-band.Top+1;
            if(locate is not null&&widened)width=Math.Max(width,Math.Min(p.Width-band.Left-4,(int)(p.Width*.6)));
            if(height<(textContext?1:4) || height>Math.Min(100,width/8) || band.Top<2 || band.Bottom+2>=p.Height)continue;
            int context=textContext?Math.Clamp(width/40,4,18):2;
            int regionTop=Math.Max(0,band.Top-context),regionBottom=Math.Min(p.Height,band.Bottom+context+1);
            var region=new HealthRegion(band.Left-2,regionTop,width+4,regionBottom-regionTop);
            if(region.X+region.Width>p.Width)continue;
            var crop=Crop(p,region);
            if(locate is not null)
            {
                if(hudOnly && !HasTextInk(crop,widened))continue;
                                // Read only the centre of the candidate, where the HUD label is.
                int labelLeft=hudOnly&&!widened ? crop.Width/4 : 0;
                var labelImage=hudOnly ? Crop(crop,new(labelLeft,0,crop.Width-2*labelLeft,crop.Height)) : crop;
                var label=locate(labelImage);if(label is null)continue;
                label=label with {Bounds=label.Bounds with {X=label.Bounds.X+labelLeft}};
                // HEALTH text marks the actual HUD centre; the HUD need not be
                // exactly centred in the window or use a full parry strip.
                double centre=region.X+label.Bounds.X+label.Bounds.Width/2d;
                int correctedWidth=(int)Math.Round(2*(centre-band.Left));
                if(correctedWidth<120 || correctedWidth>p.Width*.8 || band.Left+correctedWidth+2>p.Width)continue;
                                if(hudOnly)
                {
                    int textTop=Math.Max(region.Y,region.Y+label.Bounds.Y-2);
                    int textBottom=Math.Min(region.Y+region.Height,region.Y+label.Bounds.Y+label.Bounds.Height+2);
                    var textBand=region with {Y=textTop,Height=textBottom-textTop};
                    var framed=FindLabelFrame(p,textBand,centre,label.Bounds.Width);
                    trace?.Invoke($"Health band {region}; text={textBand}; frame={framed}");
                    if(framed is null)continue;
                    region=framed;
                }
                else region=region with {Width=correctedWidth+4};
                crop=Crop(p,region);

            }

            trace?.Invoke($"Health crop {region}; sample={HealthBarAnalyzer.AnalyzeSelection(crop)}");
            if(HealthBarAnalyzer.AnalyzeSelection(crop).Percent is null || (verify is not null && !verify(crop)))continue;
            // The game has one health HUD. Once its centred label and fill agree,
            // unrelated candidates must not delay the confirmed result.
            if(hudOnly)return region;
            // Ornate frame rows can propose several overlapping crops of one bar.
            int duplicate=candidates.FindIndex(old=>
                Math.Abs((old.X+old.Width/2d)-(region.X+region.Width/2d))<=Math.Max(8,region.Width/100) &&
                Math.Abs((old.Y+old.Height/2d)-(region.Y+region.Height/2d))<=Math.Max(6,region.Height/3) &&
                Math.Min(old.X+old.Width,region.X+region.Width)-Math.Max(old.X,region.X)>Math.Min(old.Width,region.Width)*.8);
            if(duplicate<0)candidates.Add(region);
            else if((long)region.Width*region.Height>(long)candidates[duplicate].Width*candidates[duplicate].Height)candidates[duplicate]=region;
        }
        return candidates.Count==1 ? candidates[0] : null;
    }

    private static HealthRegion? FindLabelFrame(HealthPixels p,HealthRegion region,double centre,int labelWidth)
    {
        int minimum=Math.Max(60,labelWidth*2), limit=Math.Min((int)(p.Width*.4),labelWidth*10);
        int required=Math.Max(4,(int)Math.Ceiling(region.Height*.4));
        int Votes(int x)
        {
            if(x<0 || x>=p.Width)return 0;
            int count=0;
            for(int y=region.Y;y<region.Y+region.Height;y++)
            {
                int i=(y*p.Width+x)*4;
                int min=Math.Min(p.Bgra[i],Math.Min(p.Bgra[i+1],p.Bgra[i+2]));
                int max=Math.Max(p.Bgra[i],Math.Max(p.Bgra[i+1],p.Bgra[i+2]));
                if(min<110 || max-min>35)continue;
                // HUD frame highlights sit beside its outline or coloured fill.
                bool outlined=false;
                for(int dx=-3;dx<=3;dx++){
                    int nx=x+dx;if(nx<0||nx>=p.Width)continue;
                    int j=(y*p.Width+nx)*4;
                    int nlo=Math.Min(p.Bgra[j],Math.Min(p.Bgra[j+1],p.Bgra[j+2])),nhi=Math.Max(p.Bgra[j],Math.Max(p.Bgra[j+1],p.Bgra[j+2]));
                    if(nhi<100||nhi-nlo>60){outlined=true;break;}
                }
                if(outlined)count++;
            }
            return count;
        }
        int best=int.MinValue,left=-1,right=-1;
        int distanceScale=Math.Max(4,(int)(Math.Abs(centre-region.X)*.05));
        for(int x=Math.Max(0,(int)centre-limit);x<centre-minimum;x++)
        {
            int a=Votes(x);if(a<required)continue;
            int mirror=(int)Math.Round(2*centre-x);
            for(int r=mirror-5;r<=mirror+5;r++)
            {
                int b=Votes(r),score=a+b-Math.Abs(x-region.X)/distanceScale;
                if(b<required || score<=best)continue;
                best=score;left=x;right=r;
            }
        }
        if(left<3 || right+3>=p.Width)return null;
        // Sample inside the frame; its blue-grey highlights are not health fill.
        return right-left>30 ? new(left+5,region.Y,right-left-9,region.Height) : null;
    }
    private static bool HasFrameEdge(HealthPixels p,bool right)
    {
        // Both health themes share the neutral ornate frame. Blue scenery can
        // match Depths fill, but it must not define the bar's endpoints.
        int strip=Math.Min(10,p.Width/8), rows=0;
        for(int y=0;y<p.Height;y++)
        {
            for(int x=right ? p.Width-strip : 0;x<(right ? p.Width : strip);x++)
            {
                int i=(y*p.Width+x)*4;
                int min=Math.Min(p.Bgra[i],Math.Min(p.Bgra[i+1],p.Bgra[i+2]));
                int max=Math.Max(p.Bgra[i],Math.Max(p.Bgra[i+1],p.Bgra[i+2]));
                if(min>=90 && max-min<=35){rows++;break;}
            }
        }
        return rows>=Math.Max(3,p.Height/3);
    }
    private static bool HasTextInk(HealthPixels p,bool widened=false)
    {
        // Letter strokes create many separate bright runs; a pale fill or frame
        // creates one long run. This is only an OCR prefilter, never verification.
        foreach(int threshold in new[]{145,215})
        for(int y=1;y<p.Height-1;y++)
        {
            int runs=0; bool previous=false;
            for(int x=widened?Math.Max(2,p.Width/20):p.Width/3;x<(widened?p.Width-Math.Max(2,p.Width/20):p.Width*2/3);x++)
            {
                int i=(y*p.Width+x)*4;
                int min=Math.Min(p.Bgra[i],Math.Min(p.Bgra[i+1],p.Bgra[i+2]));
                int max=Math.Max(p.Bgra[i],Math.Max(p.Bgra[i+1],p.Bgra[i+2]));
                bool ink=min>=threshold && max-min<65;
                if(ink && !previous && ++runs>=6)return true;
                previous=ink;
            }
        }
        return false;
    }
    private static HealthRegion? FindHealth(HealthPixels p, HealthRegion parry)
    {
        int top = -1, bottom = -1, left = p.Width;
        int centre = parry.X + parry.Width / 2;
        int minX = Math.Max(0, parry.X - parry.Width / 15);
        int maxX = Math.Min(p.Width - 1, parry.X + parry.Width + parry.Width / 15);
        int endY = Math.Min(p.Height - 1, parry.Y + Math.Max(35, parry.Width / 8));
        for (int y = parry.Y + 5; y <= endY; y++)
        {
            int count = 0, first = -1;
            for (int x = minX; x <= maxX; x++)
            {
                int i = (y * p.Width + x) * 4;
                int b = p.Bgra[i], g = p.Bgra[i + 1], r = p.Bgra[i + 2];
                if (HealthBarAnalyzer.IsLiveHealthColour((byte)r, (byte)g, (byte)b))
                { count++; if (first < 0) first = x; }
            }
            // Temporary health can cover the usual leftmost live-colour pixels.
            while (first > minX)
            {
                var i = (y * p.Width + first - 1) * 4;
                if (!HealthBarAnalyzer.IsTemporaryHealthColour(p.Bgra[i + 2], p.Bgra[i + 1], p.Bgra[i])) break;
                first--;
            }
            if (count >= Math.Max(12, parry.Width / 20) && first >= minX && Math.Abs(first - parry.X) < parry.Width / 15)
            { if (top < 0) top = y; bottom = y; left = Math.Min(left, first); }
            else if (top >= 0 && y - bottom > 3) break;
        }
        if (top < 0 || bottom - top < 3 || bottom - top > parry.Width / 8) return null;
        int width = 2 * (centre - left);
        if (width < parry.Width * .9 || width > parry.Width * 1.15 || left < 2 || left + width + 2 > p.Width) return null;
        var region = new HealthRegion(left - 2, Math.Max(0, top - 2), width + 4, bottom - top + 5);
        if (region.Y + region.Height > p.Height || HealthBarAnalyzer.AnalyzeSelection(Crop(p, region)).Percent is null) return null;
        return region;
    }

    private static bool GradientColour(HealthPixels p, int x, int y)
    {
        int i = (y * p.Width + x) * 4;
        int b = p.Bgra[i], g = p.Bgra[i + 1], r = p.Bgra[i + 2];
        return (r > g + 12 && b > g + 12) || (g > r + 12 && g > b + 8) || (b > r + 12 && b >= g - 12);
    }
    public static HealthPixels Crop(HealthPixels source, HealthRegion region)
    {
        if (region.X < 0 || region.Y < 0 || region.Width < 1 || region.Height < 1 ||
            (long)region.X + region.Width > source.Width || (long)region.Y + region.Height > source.Height) throw new ArgumentOutOfRangeException(nameof(region));
        var bytes = new byte[checked(region.Width * region.Height * 4)];
        for (int y = 0; y < region.Height; y++)
            Array.Copy(source.Bgra, ((region.Y + y) * source.Width + region.X) * 4, bytes, y * region.Width * 4, region.Width * 4);
        return new(region.Width, region.Height, bytes);
    }
}









