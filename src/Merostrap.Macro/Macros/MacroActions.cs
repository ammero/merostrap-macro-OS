// SPDX-License-Identifier: MPL-2.0
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Merostrap.Macros;

internal sealed class MacroActions(MacroEngine engine, CancellationToken token)
{
    public CancellationToken Token => token;
    public string Primary => engine.PrimaryButton;
    public string Secondary => Primary == "mouse1" ? "mouse2" : "mouse1";
    public Task Wait(int milliseconds) => Task.Delay(milliseconds, token);
    public void Down(string key) => engine.Key(token, key, true);
    public void Up(string key) => engine.Key(token, key, false);
    public bool RitualRecoveryPending => engine.RitualRecoveryPending(token);
    public bool ResumeRitualAfterNewPanel() => engine.ResumeRitualAfterNewPanel(token);
    public void BeginRitualTap(string key) => engine.BeginRitualTap(token, key);
    public void EndRitualTap(string key) => engine.EndRitualTap(token, key);
    public async Task Tap(string key, int milliseconds = 10)
    {
        if (engine.PrepareFlowForGeneratedFeint(token, key) is { } flowKey) await Tap(flowKey);
        Down(key); await Wait(milliseconds); Up(key);
    }
    public Task Click(int milliseconds = 10) => Tap(Primary, milliseconds);
    public void Move((int X, int Y) point) => engine.Send(token, input => input.Move(point.X, point.Y));
    public void MoveOnRobloxScreen((int X,int Y) point) => engine.Send(token,input =>
    {
        var target=input.RobloxScreenBounds ?? throw new InvalidOperationException("Move Roblox to the selected display, or choose Follow Roblox in Settings.");
        var source=input.ScreenBoundsForPoint(point.X,point.Y);
        var mapped=RobloxDisplay.MapPoint(point,source,target);
        input.Move(mapped.X,mapped.Y);
    });
    public void Scroll(int delta) => engine.Send(token, input => input.Scroll(delta));
    public void CheckForeground() => engine.Send(token, _ => { });
    public void ReportStatus(string status) => engine.ReportStatus(token, status);
    public void Complete(string status) => engine.Complete(token,status);
    public void ReportRitualPrompt(RitualPromptHighlight? prompt) => engine.ReportRitualPrompt(token,prompt);
    public void TraceRitual(string message) => engine.TraceRitual(token,message);
    public void ReportRitualArea(Health.HealthRegion area) => engine.ReportRitualArea(token, area);
    public void Center() { var (width, height) = engine.ScreenSize; Move((width / 2, height / 2)); }
    public async Task Text(string content)
    {
        foreach (var character in content) { engine.Send(token, input => input.Character(character)); await Wait(5); }
    }
    public async Task Chat(string content) { await Wait(50); await Tap("/"); await Wait(50); await Text(content); await Wait(50); await Tap("enter"); }
}
