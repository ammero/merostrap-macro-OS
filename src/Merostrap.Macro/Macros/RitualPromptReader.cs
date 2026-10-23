using Merostrap.Macros.Health;
using Tesseract;
namespace Merostrap.Macros;
/// <summary>Recognizes isolated bright glyphs of any hue locally. Ambiguous glyphs retain their bounds.</summary>
public sealed class RitualPromptReader : IDisposable
{
    private readonly object _gate = new();
    private TesseractEngine? _engine;
    private bool _disposed;
    internal int OcrEvaluationCount { get; private set; }
    private readonly Dictionary<string, (char Character, double Score, double Margin)> _shapes = new();
    private readonly Dictionary<string, (char? Character, float Confidence)> _ocrVotes = new();
    public RitualFrame Analyze(HealthPixels pixels)
    {
        var frame=AnalyzeFrame(pixels);
        // Measure the original pixels, including when recognition used a binary
        // recovery mask. Hue is irrelevant; compare each glyph to its own peak.
        if(!frame.Reliable || frame.Glyphs.Count==0)return frame;
        return frame with { Glyphs=frame.Glyphs.Select(g=> {
            Span<int> brightest=stackalloc int[5];brightest.Clear();
            for(int y=g.Y;y<g.Y+g.Height;y++)for(int x=g.X;x<g.X+g.Width;x++) {
                int i=(y*pixels.Width+x)*4;
                int value=Math.Max(pixels.Bgra[i],Math.Max(pixels.Bgra[i+1],pixels.Bgra[i+2]));
                if(value<=brightest[0])continue;
                brightest[0]=value;brightest.Sort();
            }
            return g with { InkBrightness=(brightest[0]+brightest[1]+brightest[2]+brightest[3]+brightest[4])/5f };
        }).ToArray() };
    }
    private RitualFrame AnalyzeFrame(HealthPixels pixels)
    {
        lock (_gate)
        {
            if (_disposed || pixels is null || pixels.Width is < 30 or > 4096 || pixels.Height is < 4 or > 300 || pixels.Bgra is null || pixels.Bgra.Length != (long)pixels.Width*pixels.Height*4) return new([], false);
            var primary=AnalyzeCore(pixels,false);
            if(primary.Reliable && primary.Glyphs.Count > 0 && primary.Glyphs.All(g=>g.Character is not null)) return primary;
            var recovered=AnalyzeCore(pixels,true);
            // Weak antialiased strokes may join pieces of the same bright glyph.
            // Accept recovery only when the entire recovered frame is unambiguous,
            // no original component disappears, and no confident vote changes.
            if(recovered.Reliable && recovered.Glyphs.Count>0 && recovered.Glyphs.All(g=>g.Character is not null) &&
               primary.Glyphs.All(original=>recovered.Glyphs.Any(g=>
                   g.X<=original.X && g.Y<=original.Y && g.X+g.Width>=original.X+original.Width &&
                   g.Y+g.Height>=original.Y+original.Height &&
                   (original.Character is null || original.Character==g.Character)))) return recovered;
            // Low-contrast glyphs are not required to be white or saturated.
            // Try hue-independent masks only after the normal fast path fails.
            foreach (int threshold in new[] { 130, 100, 70 })
            {
                var bytes = new byte[pixels.Bgra.Length];
                for (int i = 0; i < bytes.Length; i += 4)
                {
                    byte ink = Math.Max(pixels.Bgra[i], Math.Max(pixels.Bgra[i + 1], pixels.Bgra[i + 2])) >= threshold ? (byte)255 : (byte)0;
                    bytes[i] = bytes[i + 1] = bytes[i + 2] = ink;
                }
                var candidate = AnalyzeCore(new(pixels.Width, pixels.Height, bytes), false);
                if (candidate.Reliable && candidate.Glyphs.Count > 0 && candidate.Glyphs.All(g => g.Character is not null) &&
                    primary.Glyphs.All(original => candidate.Glyphs.Any(g =>
                        g.X <= original.X && g.Y <= original.Y && g.X + g.Width >= original.X + original.Width &&
                        g.Y + g.Height >= original.Y + original.Height && (original.Character is null || original.Character == g.Character))))
                    return candidate;
            }
            return primary;
        }
    }
    private RitualFrame AnalyzeCore(HealthPixels pixels,bool recoverInk)
    {
            var mask = new bool[pixels.Width*pixels.Height];
            for (int i=0;i<mask.Length;i++) { int b=pixels.Bgra[i*4],g=pixels.Bgra[i*4+1],r=pixels.Bgra[i*4+2]; mask[i]=IsInk(r,g,b,recoverInk); }
            // Thin scene edges can run through a glyph and connect it to the
            // entire crop. Remove only hairline pixels on a column whose nearby
            // ink spans almost the full panel; retain wider letter strokes.
            var sceneColumns=new bool[pixels.Width];
            for(int x=6;x<pixels.Width-6;x++) {
                int count=0,first=pixels.Height,last=-1,gap=0,maxGap=0;
                for(int y=0;y<pixels.Height;y++) {
                    bool ink=false;for(int dx=-6;dx<=6;dx++)ink|=mask[y*pixels.Width+x+dx];
                    if(ink){count++;first=Math.Min(first,y);last=y;gap=0;}else maxGap=Math.Max(maxGap,++gap);
                }
                sceneColumns[x]=pixels.Height>=60 && count>=pixels.Height*.9 && first<=3 && last>=pixels.Height-4 && maxGap<=3;
            }
            var sceneBand=sceneColumns.ToArray();
            for(int x=0;x<pixels.Width;x++)if(sceneColumns[x])
                for(int dx=-6;dx<=6;dx++)if(x+dx>=0 && x+dx<pixels.Width)sceneBand[x+dx]=true;
            var remove=new List<int>();
            for(int y=0;y<pixels.Height;y++)for(int x=2;x<pixels.Width-2;x++) {
                int at=y*pixels.Width+x;
                if(!mask[at] || !sceneBand[x])continue;
                // Only inspect the five adjacent pixels: a run containing
                // any three contiguous pixels is a letter stroke to preserve.
                if(!(mask[at-2] && mask[at-1]) && !(mask[at-1] && mask[at+1]) &&
                   !(mask[at+1] && mask[at+2]))remove.Add(at);
            }
            foreach(int at in remove)mask[at]=false;
            var seen = new bool[mask.Length]; var glyphs = new List<RitualGlyph>();
            for(int i=0;i<mask.Length;i++)
            {
                if (!mask[i] || seen[i]) continue;
                var stack=new Stack<int>();stack.Push(i);seen[i]=true;int left=i%pixels.Width,right=left,top=i/pixels.Width,bottom=top,count=0,bright=0;
                while(stack.Count>0){int p=stack.Pop(),x=p%pixels.Width,y=p/pixels.Width;left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);count++;int pi=p*4;if(IsCoreInk(pixels.Bgra[pi+2],pixels.Bgra[pi+1],pixels.Bgra[pi],recoverInk))bright++;
                    for(int dy=-2;dy<=2;dy++)for(int dx=-2;dx<=2;dx++){int nx=x+dx,ny=y+dy;if(nx<0||ny<0||nx>=pixels.Width||ny>=pixels.Height)continue;int q=ny*pixels.Width+nx;if(mask[q]&&!seen[q]){seen[q]=true;stack.Push(q);}} }
                int w=right-left+1,h=bottom-top+1;
                if(bright<5 || count<5 || h<5)continue;
                // Calibration may include the thin outer frame. It is UI decoration,
                // not a clipped leading glyph. Keep smaller clipped components so
                // an unreadable letter still blocks input rather than being skipped.
                bool outerFrame = left<=3 && top<=3 && right>=pixels.Width-4 && bottom>=pixels.Height-4 &&
                    w>=pixels.Width*.9 && h>=pixels.Height*.9 && count<(long)w*h*.2;
                // A hairline spanning the complete two-row area is a rule;
                // proximity to the left edge alone must never discard I or 1.
                bool sideRule = h>=Math.Max(60,pixels.Height*.95) && w<=Math.Max(2,pixels.Height*.03) &&
                    top<=1 && bottom>=pixels.Height-2;
                if(outerFrame || sideRule)continue;
                char? character=null;float confidence=0;
                if(bright>=5 && h>=8 && w>=2 && w<=h*1.6 && h<=pixels.Height*.9 && left>0 && top>0 && right<pixels.Width-1 && bottom<pixels.Height-1)
                {
                    try {
                        var shape=MatchShape(pixels,left,top,w,h,recoverInk);
                        if(shape.Score<=.135 && shape.Margin>=.025){character=shape.Character;confidence=(float)((1-shape.Score)*100);}
                        else if(shape.Score<=.14 && shape.Margin>=.015){
                            EnsureEngine();var first=Recognize(pixels,left,top,w,h,recoverInk);
                            var second=Recognize(pixels,Math.Max(0,left-2),Math.Max(0,top-2),Math.Min(pixels.Width,left+w+2)-Math.Max(0,left-2),Math.Min(pixels.Height,top+h+2)-Math.Max(0,top-2),recoverInk);
                            confidence=Math.Min(first.Confidence,second.Confidence);
                            if(first.Character==second.Character && first.Character==shape.Character)character=first.Character;
                        }
                    } catch { return new([],false); }
                }
                glyphs.Add(new(character,left,top,w,h,confidence));
            }
            // Reassemble near-touching wide/narrow pieces before authorizing
            // them: a broken M can otherwise be confidently read as N then I.
            for(int a=0;a<glyphs.Count;a++)for(int b=a+1;b<glyphs.Count;b++) {
                var first=glyphs[a];var second=glyphs[b];
                if(first.X>second.X)(first,second)=(second,first);
                int gap=second.X-first.X-first.Width,height=Math.Max(first.Height,second.Height);
                if(gap<0 || gap>Math.Max(2,Math.Min(first.Height,second.Height)*.2) ||
                   Math.Abs(first.Y-second.Y)>3 || Math.Abs(first.Height-second.Height)>3 ||
                   Math.Min(first.Width,second.Width)>height*.55 || Math.Max(first.Width,second.Width)<height*.65 ||
                   second.X+second.Width-first.X>height*1.6)continue;
                int top=Math.Min(first.Y,second.Y),width=second.X+second.Width-first.X;
                height=Math.Max(first.Y+first.Height,second.Y+second.Height)-top;
                char? value=null;float confidence=0;
                if(height>=8 && width>=2 && height<=pixels.Height*.9 && first.X>0 && top>0 &&
                   first.X+width<pixels.Width && top+height<pixels.Height) {
                    var shape=MatchShape(pixels,first.X,top,width,height,recoverInk);
                    if(shape.Score<=.135 && shape.Margin>=.025) { value=shape.Character;confidence=(float)((1-shape.Score)*100); }
                }
                glyphs[a]=new(value,first.X,top,width,height,confidence);
                glyphs.RemoveAt(b--);
            }
            // Overlapping boxes indicate merged/fragmented glyphs, so the frame cannot authorize typing.
            bool reliable=glyphs.Count<=32 && !glyphs.Any(a=>glyphs.Any(b=>a!=b && a.X<b.X+b.Width && b.X<a.X+a.Width && a.Y<b.Y+b.Height && b.Y<a.Y+a.Height));
            return new(glyphs.OrderBy(g=>g.X).ThenBy(g=>g.Y).ToArray(), reliable);
    }
    private static bool IsInk(int r,int g,int b,bool recoverInk=false) { int max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)); return recoverInk ? min>=100 && max-min<=35 || max>=70 && max-min>20 : min>=130 && max-min<=35 || max>=160 && max-min>35; }
    private static bool IsCoreInk(int r,int g,int b,bool recoverInk=false) { int max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)); return max>=195 || max>=160 && max-min>35; }
    private sealed record Template(string Character,double Aspect,string Mask,string Font,int Size);
    private static readonly Lazy<(char Character,double Aspect,byte[] Mask)[]> Templates=new(()=> {
        using var stream=typeof(RitualPromptReader).Assembly.GetManifestResourceStream("Merostrap.Resources.Ritual.serif-glyphs.json") ?? throw new IOException("Missing ritual glyph templates.");
        using var screenshots=typeof(RitualPromptReader).Assembly.GetManifestResourceStream("Merostrap.Resources.Ritual.screenshot-glyphs.json") ?? throw new IOException("Missing ritual screenshot templates.");
        return System.Text.Json.JsonSerializer.Deserialize<Template[]>(screenshots)!
            .Concat(System.Text.Json.JsonSerializer.Deserialize<Template[]>(stream)!)
            .Select(t=>(t.Character[0],t.Aspect,Convert.FromBase64String(t.Mask))).ToArray();
    });
    private (char Character,double Score,double Margin) MatchShape(HealthPixels pixels,int left,int top,int width,int height,bool recoverInk)
    {
        var normalized=new byte[16*24];
        double Sample(int x,int y){x=Math.Clamp(x,0,width-1);y=Math.Clamp(y,0,height-1);int i=((top+y)*pixels.Width+left+x)*4;int b=pixels.Bgra[i],g=pixels.Bgra[i+1],r=pixels.Bgra[i+2];return IsInk(r,g,b,recoverInk)?255:0;}
        for(int y=0;y<24;y++)for(int x=0;x<16;x++){double sx=(x+.5)*width/16-.5,sy=(y+.5)*height/24-.5;int ix=(int)Math.Floor(sx),iy=(int)Math.Floor(sy);double fx=sx-ix,fy=sy-iy;normalized[y*16+x]=(byte)((Sample(ix,iy)*(1-fx)+Sample(ix+1,iy)*fx)*(1-fy)+(Sample(ix,iy+1)*(1-fx)+Sample(ix+1,iy+1)*fx)*fy);}
        var cacheKey = width + ":" + height + ":" + Convert.ToHexString(normalized);
        if (_shapes.TryGetValue(cacheKey, out var cached)) return cached;
        var scores=new Dictionary<char,double>();
        foreach(var template in Templates.Value){double error=0;for(int i=0;i<normalized.Length;i++){double d=(normalized[i]-template.Mask[i])/255.0;error+=d*d;}error=error/normalized.Length+Math.Abs(Math.Log((double)width/height/template.Aspect))*.15;if(!scores.TryGetValue(template.Character,out double old)||error<old)scores[template.Character]=error;}
        var best=scores.OrderBy(p=>p.Value).Take(2).ToArray();
        var result = (best[0].Key,best[0].Value,best[1].Value-best[0].Value);
        if (_shapes.Count >= 512) _shapes.Clear();
        _shapes[cacheKey] = result;
        return result;
    }
    private (char? Character,float Confidence) Recognize(HealthPixels pixels,int left,int top,int w,int h,bool recoverInk)
    {
        // OCR consumes only this binary mask. Repeating it for an unchanged
        // glyph (including an ambiguous one) cannot add visual confirmation.
        // Keep exact dimensions and every ink pixel; a changed shape misses.
        var ink = new byte[(checked(w*h)+7)/8];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            int i=((top+y)*pixels.Width+left+x)*4;
            if(IsInk(pixels.Bgra[i+2],pixels.Bgra[i+1],pixels.Bgra[i],recoverInk))
            { int bit=y*w+x;ink[bit/8]|=(byte)(1<<(bit%8)); }
        }
        var key=w+":"+h+":"+Convert.ToBase64String(ink);
        if(_ocrVotes.TryGetValue(key,out var cached))return cached;
        using var pix=Pix.LoadFromMemory(Bitmap(pixels,left,top,w,h,recoverInk));
        OcrEvaluationCount++;
        using var page=_engine!.Process(pix,PageSegMode.SingleLine);
        using var iterator=page.GetIterator();iterator.Begin();var votes=new List<(char Character,float Confidence)>();
        do {var text=iterator.GetText(PageIteratorLevel.Symbol)?.Trim();if(text?.Length==1 && char.IsAsciiLetterOrDigit(text[0]) && !char.IsLower(text[0]))votes.Add((text[0],iterator.GetConfidence(PageIteratorLevel.Symbol)));}while(iterator.Next(PageIteratorLevel.Symbol));
        var groups=votes.GroupBy(v=>v.Character).OrderByDescending(g=>g.Count()).ToArray();
        (char? Character,float Confidence) result=(null,0);
        if(groups.Length>0) {
            var best=groups[0];float confidence=best.Average(v=>v.Confidence);
            result=(best.Count()>=4 && confidence>=85?best.Key:null,confidence);
        }
        if(_ocrVotes.Count>=512)_ocrVotes.Clear();
        _ocrVotes[key]=result;
        return result;
    }
    private void EnsureEngine(){if(_engine is not null)return;var root=HealthOcrResources.EnsureAvailable();TesseractEnviornment.CustomSearchPath=root;_engine=new(Path.Combine(root,"tessdata"),"eng",EngineMode.LstmOnly);_engine.SetVariable("tessedit_char_whitelist","ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");}
    private static byte[] Bitmap(HealthPixels pixels,int left,int top,int w,int h,bool recoverInk)
    {
        const int scale=4,pad=16;int width=(w*scale+pad)*5+pad,height=h*scale+pad*2,stride=(width*3+3)&~3;using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);
        writer.Write((ushort)0x4d42);writer.Write(54+stride*height);writer.Write(0);writer.Write(54);writer.Write(40);writer.Write(width);writer.Write(height);writer.Write((ushort)1);writer.Write((ushort)24);writer.Write(0);writer.Write(stride*height);for(int i=0;i<4;i++)writer.Write(0);
        var row=new byte[stride];for(int y=height-1;y>=0;y--){Array.Fill(row,(byte)255);if(y>=pad&&y<height-pad)for(int x=pad;x<width-pad;x++){if((x-pad)%(w*scale+pad)>=w*scale)continue;int i=((top+(y-pad)/scale)*pixels.Width+left+((x-pad)%(w*scale+pad))/scale)*4;int b=pixels.Bgra[i],g=pixels.Bgra[i+1],r=pixels.Bgra[i+2];byte value=IsInk(r,g,b,recoverInk)?(byte)0:(byte)255;row[x*3]=row[x*3+1]=row[x*3+2]=value;}writer.Write(row);}return stream.ToArray();
    }
    public void Dispose(){lock(_gate){_disposed=true;_engine?.Dispose();_engine=null;}}
}


