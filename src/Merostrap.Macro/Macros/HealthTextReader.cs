using System.Globalization;
using System.Text.RegularExpressions;
using Merostrap.Macros.Health;
using Tesseract;

namespace Merostrap.Macros;

/// <summary>Local OCR of the percentage followed by HEALTH; no image leaves the device.</summary>
public sealed class HealthTextReader : IDisposable
{
    private readonly object _gate = new();
    private TesseractEngine? _engine;
    private bool _disposed;
    private int _preferredThreshold = 145;
    public long OcrPasses { get; private set; }
    public Action<string>? Diagnostic {get;set;}
    private HealthPixels? _labelPixels;
    private HealthLabel? _cachedLabel;
    private long _labelAt;
    private static int PaleTextThreshold(HealthPixels p)
    {
        var histogram=new int[235];
        for(int i=0;i<p.Bgra.Length;i+=4){int lo=Math.Min(p.Bgra[i],Math.Min(p.Bgra[i+1],p.Bgra[i+2])),hi=Math.Max(p.Bgra[i],Math.Max(p.Bgra[i+1],p.Bgra[i+2]));if(lo>=115&&lo<235&&hi-lo<=40)histogram[lo]++;}
        int mode=Enumerable.Range(115,120).MaxBy(i=>histogram[i]);
        return histogram[mode]>=Math.Max(16,p.Width*p.Height/15)?mode+1:215;
    }
    private static int TextScale(HealthPixels p)=>Math.Clamp(96/Math.Max(1,p.Height),2,6);
    private HealthLabel? CachedLabel(HealthPixels p)
    {
        if(_labelPixels is not {} patch||_cachedLabel is not {} label||Environment.TickCount64-_labelAt>1500||patch.Width>p.Width||patch.Height>p.Height)return null;
        int centre=(p.Width-patch.Width)/2,margin=Math.Min(64,Math.Max(12,p.Width/16));
        for(int y=0;y<=p.Height-patch.Height;y++)for(int x=Math.Max(0,centre-margin);x<=Math.Min(p.Width-patch.Width,centre+margin);x++){
            int offset=(y*p.Width+x)*4;
            if(!p.Bgra.AsSpan(offset,4).SequenceEqual(patch.Bgra.AsSpan(0,4)))continue;
            int middle=(patch.Height/2*patch.Width+patch.Width/2)*4;
            if(!p.Bgra.AsSpan(((y+patch.Height/2)*p.Width+x+patch.Width/2)*4,4).SequenceEqual(patch.Bgra.AsSpan(middle,4)))continue;
            bool same=true;for(int row=0;row<patch.Height;row++)if(!p.Bgra.AsSpan(((y+row)*p.Width+x)*4,patch.Width*4).SequenceEqual(patch.Bgra.AsSpan(row*patch.Width*4,patch.Width*4))){same=false;break;}
            if(same)return label with{Bounds=new(x,y,patch.Width,patch.Height)};
        }
        return null;
    }
    private uint _nearFullFingerprint;
    private long _nearFullChecked;
    private double? _nearFullText;
    private void EnsureEngine()
    {
        if(_engine is not null)return;
        var resources=HealthOcrResources.EnsureAvailable();
        TesseractEnviornment.CustomSearchPath=resources;
        _engine=new TesseractEngine(Path.Combine(resources,"tessdata"),"eng",EngineMode.LstmOnly);
    }
    // Auto-calibration requires actual HEALTH text, not just a coloured rectangle.
    public bool ValidateDetectedBar(HealthPixels pixels)
    {
        lock(_gate)
        {
            if(_disposed)return false;
            var fill=HealthBarAnalyzer.AnalyzeSelection(pixels);
            if(fill.Percent is null)return false;
            EnsureEngine();
            var label=FindHealthLabel(pixels);
            // Detection needs a confident HEALTH label at the bar's centre.
            // Its digits may lag the fill or OCR may confuse 9 with 2; do not
            // reject the whole HUD for that while the live fill remains valid.
            if(label is not null && label.Bounds.Width>=20 && label.Bounds.Width<=pixels.Width*.6 &&
                Math.Abs(label.Bounds.X+label.Bounds.Width/2d-pixels.Width/2d)<=Math.Max(8,pixels.Width*.03))return true;
            return false;
        }
    }
    public HealthLabel? FindHealthLabel(HealthPixels pixels)
    {
        lock(_gate)
        {
            if(_disposed || pixels.Width is <30 or >4096 || pixels.Height is <4 or >300 || pixels.Bgra is null || pixels.Bgra.LongLength!=(long)pixels.Width*pixels.Height*4)return null;
            if(CachedLabel(pixels) is {} cached)return cached;
            EnsureEngine();
            foreach(var threshold in new[]{_preferredThreshold,145,110,180,PaleTextThreshold(pixels),215,240}.Distinct())
            {
                HealthRegion? bounds=null;
                var sample=Read(pixels,threshold,verifyFill:false,boundsRead:found=>bounds=found);
                if(sample.Percent is double percent && bounds is not null){
                    var label=new HealthLabel(percent,bounds);
                    if(bounds.Width is >=20 and <=600 && bounds.Height is >=4 and <=96){_labelPixels=HudBarDetector.Crop(pixels,bounds);_cachedLabel=label;_labelAt=Environment.TickCount64;}
                    return label;
                }
            }
            return null;
        }
    }
    private HealthSample ConfirmNearFull(HealthPixels pixels,HealthSample fill)
    {
        // Only the small near-full discrepancy needs OCR. Ordinary changes keep
        // using the fast fill reader; never turn genuine 99% damage into 100.
        uint fingerprint=2166136261;
        for(int y=0;y<pixels.Height;y++)for(int x=pixels.Width/3;x<pixels.Width*2/3;x++)
        {
            int i=(y*pixels.Width+x)*4;
            fingerprint=unchecked((fingerprint^pixels.Bgra[i])*16777619);
            fingerprint=unchecked((fingerprint^pixels.Bgra[i+1])*16777619);
            fingerprint=unchecked((fingerprint^pixels.Bgra[i+2])*16777619);
        }
        if(fingerprint!=_nearFullFingerprint || Environment.TickCount64-_nearFullChecked>=500)
        {
            EnsureEngine();
            _nearFullText=null;
            foreach(var threshold in new[]{_preferredThreshold,145,110,180,PaleTextThreshold(pixels),215,240}.Distinct())
            {
                var label=Read(pixels,threshold,verifyFill:false);
                if(label.Percent is not null){_nearFullText=label.Percent;break;}
            }
            _nearFullFingerprint=fingerprint;_nearFullChecked=Environment.TickCount64;
        }
        return _nearFullText==100 ? fill with {Percent=100,Status="Health 100% - text verified"} : fill;
    }
    public HealthSample Analyze(HealthPixels pixels)
    {
        lock (_gate)
        {
            if (_disposed) return new(null, "Health reader stopped.");
            if (pixels.Width is < 30 or > 4096 || pixels.Height is < 4 or > 300 || pixels.Bgra is null || pixels.Bgra.Length != (long)pixels.Width * pixels.Height * 4)
                return new(null, "Invalid health image.");
            // The same validated fill estimate was already accepted as an OCR
            // fallback. Use it immediately instead of waiting for text recognition.
            var fill = HealthBarAnalyzer.AnalyzeSelection(pixels);
            if (fill.Percent is not null)
            {
                if(fill.Percent is >=98 and <100 && (fill.WitherPercent??0)==0)
                {
                    try { return ConfirmNearFull(pixels,fill); }
                    catch(Exception) { return fill; } // Missing OCR resources must not stop fill tracking.
                }
                return fill;
            }
            EnsureEngine();
            HealthSample sample = new(null, "Health number is unclear.");
            foreach (var threshold in new[] { _preferredThreshold, 145, 110, 180,PaleTextThreshold(pixels),215,240 }.Distinct())
            {
                sample = Read(pixels, threshold);
                if (sample.Percent is not null) { _preferredThreshold = threshold; return sample; }
            }
            return sample;
        }
    }
    private HealthSample Read(HealthPixels pixels, int threshold, bool verifyFill = true, Action<HealthRegion>? boundsRead = null)
    {
            int scale=TextScale(pixels);
            using var image = Pix.LoadFromMemory(TextBitmap(pixels, threshold));
            OcrPasses++;
            using var page = _engine!.Process(image, threshold == 180 ? PageSegMode.SingleLine : PageSegMode.SparseText);
            using var lines = page.GetIterator();
            lines.Begin();
            HealthSample? result = null;
            do
            {
                Diagnostic?.Invoke($"OCR threshold={threshold}; text={lines.GetText(PageIteratorLevel.TextLine)}; confidence={lines.GetConfidence(PageIteratorLevel.TextLine)}");
                var sample = Parse(lines.GetText(PageIteratorLevel.TextLine) ?? "");
                if (sample.Percent is null) continue;
                if (result is not null) return new(null, "Multiple health numbers detected.");
                if (lines.GetConfidence(PageIteratorLevel.TextLine) < 65) return new(null, "Health number is unclear.");
                if (!lines.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds)) return new(null, "Health text location is unclear.");
                var top = Math.Clamp((bounds.Y1 - 16) / scale, 0, pixels.Height - 1);
                var bottom = Math.Clamp((bounds.Y2 - 16) / scale, top + 1, pixels.Height);
                var left=Math.Clamp((bounds.X1-16)/scale,0,pixels.Width-1);
                var right=Math.Clamp((bounds.X2-16+scale-1)/scale,left+1,pixels.Width);
                boundsRead?.Invoke(new(left,top,right-left,bottom-top));
                result = verifyFill ? VerifyFill(pixels, sample, top, bottom) : sample;
            } while (lines.Next(PageIteratorLevel.TextLine));
            return result ?? new(null, "Health number not detected. Include the percentage and HEALTH text.");
    }
    public static HealthSample Parse(string text)
    {
        var matches = Regex.Matches(text, @"(?<![\d.])(100|[1-9]?\d)\s*%\s*HEALTH\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (matches.Count != 1) return new(null, "Health number not detected. Include the percentage and HEALTH text.");
        var percent = double.Parse(matches[0].Groups[1].Value, CultureInfo.InvariantCulture);
        return new(percent, $"Health {percent:0}% · text verified");
    }
    private static HealthSample VerifyFill(HealthPixels pixels, HealthSample sample, int top, int bottom)
    {
        var first = pixels.Width; var last = -1;
        for (var x = 0; x < pixels.Width; x++)
        {
            var count = 0;
            for (var y = Math.Max(0, top - 2); y < Math.Min(pixels.Height, bottom + 2); y++)
            {
                var i = (y * pixels.Width + x) * 4;
                var b = pixels.Bgra[i]; var g = pixels.Bgra[i + 1]; var r = pixels.Bgra[i + 2];
                if (r >= 85 && r > g && g > b && r - b > r * 0.35 && (g - b) * 60.0 / (r - b) is >= 10 and <= 50) count++;
            }
            if (count >= 3) { first = Math.Min(first, x); last = x; }
        }
        if (last < first) return sample.Percent <= 5 ? sample : new(null, "Health fill not detected.");
        if (first > pixels.Width * 0.2) return new(null, "Select the whole health bar.");
        var width = pixels.Width - first * 2;
        var fill = Math.Clamp((last - first + 1) * 100.0 / width, 0, 100);
        return Math.Abs(fill - sample.Percent!.Value) <= 3 ? sample : new(null, "Health number and fill disagree.");
    }

    private static byte[] TextBitmap(HealthPixels pixels, int threshold)
    {
        int scale=TextScale(pixels);const int padding=16;
        var width = pixels.Width * scale + padding * 2;
        var height = pixels.Height * scale + padding * 2;
        var stride = (width * 3 + 3) & ~3;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4D42); writer.Write(54 + stride * height); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(width); writer.Write(height); writer.Write((ushort)1); writer.Write((ushort)24);
        writer.Write(0); writer.Write(stride * height); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        var row = new byte[stride];
        for (var y = height - 1; y >= 0; y--)
        {
            Array.Fill(row, (byte)255);
            for (var x = padding; x < width - padding && y >= padding && y < height - padding; x++)
            {
                var i = (((y - padding) / scale) * pixels.Width + (x - padding) / scale) * 4;
                var b = pixels.Bgra[i]; var g = pixels.Bgra[i + 1]; var r = pixels.Bgra[i + 2];
                var min = Math.Min(r, Math.Min(g, b)); var max = Math.Max(r, Math.Max(g, b));
                var value = (byte)(min >= threshold && max - min < 65 ? 0 : 255);
                row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = value;
            }
            writer.Write(row);
        }
        return stream.ToArray();
    }
    public void Dispose() { lock (_gate) { _disposed = true; _engine?.Dispose(); _engine = null; } }
}
