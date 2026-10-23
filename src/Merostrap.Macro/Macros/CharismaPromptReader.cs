using Merostrap.Macros.Health;
using Tesseract;
namespace Merostrap.Macros;

/// <summary>Local OCR of the centre quote and its input bar.</summary>
public sealed class CharismaPromptReader : IDisposable
{
    public static readonly string[] Phrases=[
        "Me-wow, is that the latest Felinor fashion?", "So, what's keeping you busy these days?",
        "Hey hivekin, can I bug you for a moment?", "So, how's work?", "Wow, this breeze is great, right?",
        "Sometimes I have really deep thoughts about life and stuff.", "Some weather we're having, huh?",
        "You ever been to a Canor restaurant? The food's pretty howlright."];
    private TesseractEngine? _engine;
    public string Diagnostic {get;private set;}="";
    private record Line(string Text,int X,int Y,int Width,int Height,float Confidence);
    public CharismaFrame Analyze(HealthPixels pixels)
    {
        if(pixels.Width<300 || pixels.Height<200 || pixels.Bgra.Length!=(long)pixels.Width*pixels.Height*4)return new("","",false);
        EnsureEngine();
        int left=pixels.Width/3,top=pixels.Height*35/100,width=pixels.Width/3,height=pixels.Height*16/100;
        var lines=Read(pixels,left,top,width,height,PageIteratorLevel.TextLine);
        Diagnostic=string.Join(" | ",lines.Select(l=>$"{l.Text}@{l.Y}+{l.Height}"));
        var recognized=lines.Select(l=>(Line:l,Phrase:RecognizePhrase(l.Text))).FirstOrDefault(l=>l.Phrase is not null);
        if(recognized.Phrase is null) {
            // A weak reading of the training instruction is uncertainty, not completion.
            bool menuVisible=lines.Any(l=>Similarity(string.Join("",CharismaText.Tokens(l.Text)),"trysomesmalltalkonsomeonenearby")>=.4);
            return new("","",!menuVisible);
        }
        var raw=recognized.Line;
        var progress=lines.Where(l=>l.Y>=raw.Y+raw.Height+2 && l.Y<raw.Y+Math.Max(30,raw.Height*5)).OrderBy(l=>l.Y).FirstOrDefault();
        int barWidth=pixels.Width/8,barHeight=Math.Max(12,raw.Height*2);
        var area=new HealthRegion(pixels.Width/2-barWidth/2,raw.Y+raw.Height+2,barWidth,barHeight);
        return new(recognized.Phrase,progress?.Text??"",true,area);
    }
    public static string? RecognizePhrase(string text)
    {
        var value=string.Join("",CharismaText.Tokens(text));
        var best=Phrases.Select(p=>(Phrase:p,Score:Similarity(value,string.Join("",CharismaText.Tokens(p))))).OrderByDescending(p=>p.Score).Take(2).ToArray();
        return best[0].Score>=.62 && best[0].Score-best[1].Score>=.18?best[0].Phrase:null;
    }
    private static double Similarity(string a,string b)
    {
        if(a.Length==0)return 0;var previous=Enumerable.Range(0,b.Length+1).ToArray();
        for(int i=1;i<=a.Length;i++){var row=new int[b.Length+1];row[0]=i;for(int j=1;j<=b.Length;j++)row[j]=Math.Min(Math.Min(row[j-1]+1,previous[j]+1),previous[j-1]+(a[i-1]==b[j-1]?0:1));previous=row;}
        return 1-(double)previous[b.Length]/Math.Max(a.Length,b.Length);
    }
    private List<Line> Read(HealthPixels pixels,int left,int top,int width,int height,PageIteratorLevel level)
    {
        const int scale=4,pad=12;int w=width*scale+pad*2,h=height*scale+pad*2,stride=(w*3+3)&~3;
        using var stream=new MemoryStream();using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true)) {
            writer.Write((ushort)0x4d42);writer.Write(54+stride*h);writer.Write(0);writer.Write(54);writer.Write(40);writer.Write(w);writer.Write(h);writer.Write((ushort)1);writer.Write((ushort)24);writer.Write(0);writer.Write(stride*h);for(int i=0;i<4;i++)writer.Write(0);
            var row=new byte[stride];
            for(int y=h-1;y>=0;y--){Array.Fill(row,(byte)255);if(y>=pad&&y<h-pad)for(int x=pad;x<w-pad;x++) {
                int i=((top+(y-pad)/scale)*pixels.Width+left+(x-pad)/scale)*4;int b=pixels.Bgra[i],g=pixels.Bgra[i+1],r=pixels.Bgra[i+2];
                byte c=(byte)(255-Math.Clamp((Math.Min(r,Math.Min(g,b))-125)*255/100,0,255));row[x*3]=row[x*3+1]=row[x*3+2]=c;
            }writer.Write(row);}
        }
        using var pix=Pix.LoadFromMemory(stream.ToArray());using var page=_engine!.Process(pix,PageSegMode.SparseText);using var iterator=page.GetIterator();iterator.Begin();var result=new List<Line>();
        do {var text=iterator.GetText(level)?.Trim();if(!string.IsNullOrWhiteSpace(text)&&iterator.TryGetBoundingBox(level,out var box))result.Add(new(text,left+(box.X1-pad)/scale,top+(box.Y1-pad)/scale,Math.Max(1,(box.Width+scale-1)/scale),Math.Max(1,(box.Height+scale-1)/scale),iterator.GetConfidence(level)));}while(iterator.Next(level));
        return result;
    }
    private void EnsureEngine(){if(_engine is not null)return;var root=HealthOcrResources.EnsureAvailable();TesseractEnviornment.CustomSearchPath=root;_engine=new(Path.Combine(root,"tessdata"),"eng",EngineMode.LstmOnly);_engine.SetVariable("debug_file","NUL");}
    public void Dispose(){_engine?.Dispose();_engine=null;}
}
