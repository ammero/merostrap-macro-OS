using Merostrap.Macros.Health;
namespace Merostrap.Macros;
public sealed record FlowStateCooldownSample(double? ReadyPercent,string Status,HealthRegion? Slot=null);
public sealed class FlowStateCooldownReader : IDisposable
{
    private HealthRegion? _slot;
    private int _width,_height;
    public string Diagnostic {get;private set;}="";
    public FlowStateCooldownSample Analyze(HealthPixels hotbar)
    {
        if(hotbar.Bgra.Length!=(long)hotbar.Width*hotbar.Height*4)return new(null,"Hotbar unavailable");
        if(_width!=hotbar.Width || _height!=hotbar.Height){_slot=null;_width=hotbar.Width;_height=hotbar.Height;}
        if(_slot is {} previous && Score(hotbar,previous)<.65)_slot=null;
        _slot??=Detect(hotbar);
        if(_slot is not {} slot)return new(null,"Looking for Flow State");
        var rows=new List<double>();int brightest=0,darkest=255;
        for(int y=slot.Y+3;y<slot.Y+slot.Height-3;y++)
        {
            var values=new List<int>();
            for(int x=slot.X+2;x<slot.X+slot.Width-2;x++)
            {
                int i=(y*hotbar.Width+x)*4;int value=Math.Min(hotbar.Bgra[i],Math.Min(hotbar.Bgra[i+1],hotbar.Bgra[i+2]));
                values.Add(value);brightest=Math.Max(brightest,value);darkest=Math.Min(darkest,value);
            }
            values.Sort();rows.Add(values[(int)((values.Count-1)*.95)]);
        }
        if(rows.Count<8 || brightest<145 || brightest-darkest<35){_slot=null;return new(null,"Hotbar unavailable");}
        // Paper backgrounds slope from light at the top to darker at the bottom.
        // Fit one horizontal boundary; title/icon strokes cannot create extra bars.
        int edge=0;double best=double.MaxValue;
        for(int boundary=0;boundary<=rows.Count;boundary++)
        {
            double error=0;
            for(int y=0;y<rows.Count;y++)
            {
                double paper=225-36.0*y/Math.Max(1,rows.Count-1);
                double expected=y<boundary?paper:paper*.8;
                error+=Math.Pow(rows[y]-expected,2);
            }
            if(error<best){best=error;edge=boundary;}
        }
        double percent=100.0*edge/rows.Count;
        if(percent>=95)percent=100; // Allow one bottom border pixel at this card size.
        return new(percent,percent>=99?"Ready":"Cooling down",slot);
    }
    private static readonly double[] Template = LoadTemplate();
    private static double[] LoadTemplate()
    {
        var assembly=typeof(FlowStateCooldownReader).Assembly;
        using var stream=assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n=>n.EndsWith("flow-state-card.json",StringComparison.Ordinal)))!;
        var values=System.Text.Json.JsonSerializer.Deserialize<double[]>(stream)!;
        for(int y=0;y<12;y++)
        {
            var mean=values.Skip(y*10).Take(10).Average();
            for(int x=0;x<10;x++)values[y*10+x]-=mean;
        }
        return values;
    }
    private static double Score(HealthPixels pixels,HealthRegion slot)
    {
        double cross=0,a=0,b=0;
        Span<double> row=stackalloc double[10];
        for(int y=0;y<12;y++)
        {
            double mean=0;
            for(int x=0;x<10;x++)
            {
                int px=slot.X+(int)Math.Round((2+x*29d/9)*slot.Width/34),py=slot.Y+(int)Math.Round((3+y*27d/11)*slot.Height/34);
                int i=(py*pixels.Width+px)*4;
                row[x]=Math.Min(pixels.Bgra[i],Math.Min(pixels.Bgra[i+1],pixels.Bgra[i+2]));mean+=row[x]/10;
            }
            for(int x=0;x<10;x++){double v=row[x]-mean,t=Template[y*10+x];cross+=v*t;a+=v*v;b+=t*t;}
        }
        return a<100?0:cross/Math.Sqrt(a*b);
    }
    private HealthRegion? Detect(HealthPixels pixels)
    {
        // Match the card artwork and title, excluding the number above the card.
        // Row-centering removes the horizontal cooldown shade from recognition.
        double best=.78;HealthRegion? found=null;
        int max=Math.Min(70,pixels.Height-4);
        for(int size=28;size<=max;size+=2)
        for(int y=0;y+size<pixels.Height;y+=2)
        for(int x=0;x+size<pixels.Width;x+=2)
        {
            var candidate=new HealthRegion(x,y,size,size);double score=Score(pixels,candidate);
            if(score>best){best=score;found=candidate;}
        }
        if(found is {} f)
        {
            for(int size=Math.Max(28,f.Width-1);size<=f.Width+1;size++)
            for(int y=Math.Max(0,f.Y-1);y<=f.Y+1;y++)
            for(int x=Math.Max(0,f.X-1);x<=f.X+1;x++)
            {
                if(x+size>=pixels.Width||y+size>=pixels.Height)continue;
                var candidate=new HealthRegion(x,y,size,size);double score=Score(pixels,candidate);
                if(score>best){best=score;found=candidate;}
            }
        }
        Diagnostic=$"Card match {best:0.000}";
        return found;
    }
    public void Dispose() { }
}
