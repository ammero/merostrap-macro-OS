namespace Merostrap.Macros;
/// <summary>A screen-confirmed dodge starts an estimated timer; only the icon can confirm ready.</summary>
public sealed class DodgeCooldown
{
    bool? previous;long? start;double duration=1750;bool known;
    public double CooldownSeconds {get=>duration/1000;set{if(double.IsFinite(value))duration=Math.Clamp(value,.25,10)*1000;}}
    public void Observe(bool? ready,long now)
    {
        if(ready is null){previous=null;start=null;known=false;return;}
        known=true;
        if(ready==false&&previous==true)start=now;
        if(ready==true&&start is long began){double measured=now-began;if(measured is >=250 and <=10000)duration=measured;start=null;}
        previous=ready;
    }
    public double Advance(long now)=>!known?0:previous==true?100:start is long began?Math.Clamp((now-began)*100/duration,0,95):0;
    public void Reset(){previous=null;start=null;known=false;}
}

