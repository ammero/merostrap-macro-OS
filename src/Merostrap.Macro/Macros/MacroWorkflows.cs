// Adapted workflow sequences from DeepWarden by dan-ie, commit 6e41a224995ec7473bffbd55254ddf69dfb70c57.
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// License: https://polyformproject.org/licenses/noncommercial/1.0.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Merostrap.Macros;

internal enum MacroRunMode { Once, Toggle, Hold, Automatic }
internal sealed record MacroWorkflow(string Id, string[] Triggers, Func<MacroActions, string?, Task> Execute,
    MacroRunMode Mode = MacroRunMode.Once, bool OnRelease = false, bool Suppress = false,
    SilentheartFlowDetector? FlowDetector = null, string? FlowStateKey = null);

internal static class MacroWorkflows
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "right-mouse-parry", "auto-buy", "auto-drop-notes", "auto-eat", "auto-feint", "auto-log", "auto-roll-cancel", "auto-sell",
        "tacet-toggle", "trash-talk", "mantra-variants", "bell-stack-dodge", "bell-stack-parry", "flash-map",
        "golden-tongue", "hold-m1", "mantra-roll-tech", "mantra-slide-tech", "mb-all", "motif-swap",
        "roll-crit", "roll-m1", "roll-parry", "ankle-cutter", "mayhem", "relentless-hunt", "rising-star", "silentheart-flow-state",
        "auto-agility", "charisma-autofill", "auto-fortitude", "auto-willpower", "uppercut-assassinate",
        "auto-uppercut", "dynamic-auto-uppercut", "auto-pender", "ritual-cast", "gank-pinger"
    };
    public static MacroSupport GetSupport(string id)
    {
        return Supported.Contains(id) || IsChat(id) ? new(true, "") : new(false, "Unknown macro.");
    }
    private static bool IsChat(string id) => id.StartsWith("chat-message-", StringComparison.Ordinal) &&
        int.TryParse(id.AsSpan(13), out var index) && index is >= 1 and <= 10;

    public static MacroWorkflow Create(string id, Dictionary<string, string> values, Func<IRitualPromptSource>? ritualSourceFactory = null, Func<Uri, string, CancellationToken, Task>? webhookSender = null, Func<GankAlert,CancellationToken,Task>? gankSender = null, Func<string,CancellationToken,Task<byte[]>>? windowCapture = null, Func<ICharismaPromptSource>? charismaSourceFactory = null, Func<string,CancellationToken,Task>? clipboardSetter = null)
    {
        foreach (var field in MacroCatalog.All.FirstOrDefault(d => d.Id == id)?.Fields ?? []) values.TryAdd(field.Key, field.DefaultValue);
        string Value(string key) => values.TryGetValue(key, out var value) ? value.Trim() : "";
        string Key(string field) { var keys = MacroKeys.Parse(Value(field), field); if (keys.Length != 1) throw new ArgumentException(field + " requires one key."); return keys[0]; }
        bool Bool(string key) => bool.TryParse(Value(key), out var value) ? value : throw new ArgumentException(key + " must be true or false.");
        string Text(string key) { var text = Value(key); if (text.Length is 0 or > 2000 || text.Any(char.IsControl)) throw new ArgumentException(key + " must contain 1–2000 characters on one line."); return text; }
        int Repetitions() => int.TryParse(Value("repetitions"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count is >= 1 and <= 1000 ? count : throw new ArgumentException("repetitions must be between 1 and 1000.");
        (int X, int Y) Point(string key)
        {
            var parts = Value(key).Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y) || x is < -32768 or > 32767 || y is < -32768 or > 32767)
                throw new ArgumentException(key + " requires screen coordinates: X Y.");
            return (x, y);
        }
        MacroWorkflow Bound(Func<MacroActions, string?, Task> execute, MacroRunMode mode = MacroRunMode.Once) => new(id, MacroKeys.Parse(Value("keybind"), "keybind"), execute, mode);

        if (IsChat(id) || id == "golden-tongue")
        {
            var content = Text("content"); var sprint = Bool("autosprint");
            return Bound(async (a, _) => { await a.Chat(content); if (sprint) { await a.Tap("w"); await a.Wait(50); a.Down("w"); await a.Wait(Timeout.Infinite); } }, sprint ? MacroRunMode.Toggle : MacroRunMode.Once);
        }
        switch (id)
        {
            case "silentheart-flow-state":
                var flowKey = Key("flowstatekeybind");
                var dodgeKey = Key("dodgekeybind"); var crouchKey = Key("crouchkeybind");
                var jumpKey = Key("jumpkeybind"); var feintKey = Key("feintkeybind");
                if (flowKey is "esc" or "f8" or "f9" or "ctrl" or "w" or "mouse1" ||
                    new[] { dodgeKey, crouchKey, jumpKey, feintKey }.Contains(flowKey))
                    throw new ArgumentException("Flow State needs a separate toolbar key, such as 4.");
                return new(id, ["mouse1", feintKey], async (a, attack) => {
                    if (attack is null) return;
                    await a.Tap(flowKey); await a.Tap(attack);
                }, FlowDetector: new(dodgeKey, crouchKey, jumpKey, feintKey), FlowStateKey: flowKey);
            case "gank-pinger":
                var alert=GankWebhook.Create(values);
                return Bound(async (a, _) => {
                    a.CheckForeground();
                    var outgoing=alert;
                    if (alert.WindowId.Length>0)
                    {
                        if (windowCapture is null) throw new InvalidOperationException("Window capture is unavailable.");
                        a.ReportStatus("Gank Pinger: capturing window");
                        var png=await windowCapture(alert.WindowId,a.Token);
                        if (png.Length==0) throw new InvalidOperationException("Selected window could not be captured.");
                        outgoing=alert with {Screenshot=png};
                    }
                    a.CheckForeground();a.ReportStatus("Gank Pinger: sending");
                    if (gankSender is not null) await gankSender(outgoing,a.Token);
                    else if (outgoing.Authorization.Length==0 && outgoing.Screenshot is null && webhookSender is not null) await webhookSender(outgoing.Endpoint,outgoing.Message,a.Token);
                    else await GankWebhook.SendAlertAsync(outgoing,a.Token);
                    a.Complete("Gank Pinger: message sent");
                });
            case "ritual-cast":
                Health.HealthRegion? savedRegion = null;
                if (Value("startcoordinates").Length > 0 || Value("endcoordinates").Length > 0)
                {
                    try
                    {
                        var topLeft = Point("startcoordinates"); var bottomRight = Point("endcoordinates");
                        savedRegion = new Health.HealthRegion(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
                        savedRegion.Validate();
                    }
                    catch (ArgumentException) { savedRegion = null; }
                }
                if (!int.TryParse(Value("ping_ms"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ritualPing) || ritualPing is < 0 or > 1000)
                    throw new ArgumentException("ping_ms must be between 0 and 1000 milliseconds.");
                if (ritualSourceFactory is null) throw new ArgumentException("Ritual recognition is not configured for this session.");
                // Keep acknowledgement across interruptions; resuming must not replay a sent key.
                var fastRitual = Bool("fast_mode");
                var tracker = new RitualSequenceTracker(fastRitual,Math.Clamp(ritualPing+100,250,600));
                var region = savedRegion;
                var trackedRegion=region;
                (nint Window,int X,int Y,int Width,int Height,uint Dpi)? observedViewport=null;
                bool viewportObserved=false;
                return new(id, [], async (a, _) =>
                {
                    using var source = ritualSourceFactory();
                    tracker.ResetConfirmation();
                    // Preserve the viewport alongside the tracker across focus
                    // interruptions; a new source must not bless old calibration
                    // after Roblox moved or resized while capture was stopped.
                    if(!viewportObserved) { observedViewport=source.Viewport;viewportObserved=true; }
                    long nextRitualInput = 0;
                    Task? releasePulse = null;
                    static RitualPromptHighlight Indicator(Health.HealthRegion panel, RitualGlyph glyph, bool sent)
                    {
                        // The overlay adds its own transparent padding around
                        // these full glyph bounds and requests capture exclusion.
                        return new(new(panel.X+glyph.X,panel.Y+glyph.Y,glyph.Width,glyph.Height),sent);
                    }
                    async Task ReleasePulse(string key)
                    {
                        // Fast taps span a 60 Hz game frame without adding network latency
                        // to local key duration. Acknowledgement still gates the next input.
                        await a.Wait(fastRitual ? 24 : 50 + Math.Min(ritualPing / 10, 100));
                        a.EndRitualTap(key);
                        a.TraceRitual("input-up="+key);
                    }
                    a.CheckForeground();
                    long nextDetection = 0;
                    long? recoveryAbsentSince=null;
                    bool recoveryArmed=false;
                    long? unclearSince = null;
                    string lastRitualReason = "";
                    long ritualReasonSince = Environment.TickCount64;
                    bool sentAny = false;
                    long? ritualAbsentSince = null;
                    long lastDiagnosticAt = 0;
                    a.ReportStatus("Ritual: reading prompts");
                    try
                    {
                        while (true)
                        {
                            if (releasePulse is { IsCompleted: true } released)
                            {
                                await released;
                                releasePulse = null;
                                nextRitualInput = Environment.TickCount64 + (fastRitual ? 0 : 5);
                            }
                            a.CheckForeground();
                            var beforeRead = Environment.TickCount64;
                            var viewport=source.Viewport;
                            if(viewport!=observedViewport) {
                                observedViewport=viewport;region=null;nextDetection=0;unclearSince=null;
                                tracker.ResetConfirmation();a.ReportRitualPrompt(null);
                                a.TraceRitual("viewport-changed; recalibrating prompt area");
                            }
                            if ((!a.RitualRecoveryPending || region is null || recoveryArmed) && (region is null || unclearSince is { } since && beforeRead - since >= 250) && beforeRead >= nextDetection)
                            {
                                a.ReportStatus("Ritual: waiting for prompt");
                                var detected = source.DetectRegion(a.Token);
                                a.CheckForeground();
                                if(source.Viewport!=viewport) { await a.Wait(4);continue; }
                                nextDetection = Environment.TickCount64 + 200;
                                if (detected is not null)
                                {
                                    detected.Validate();
                                    if (detected != region) {
                                        if(trackedRegion is not null)tracker.Rebase(trackedRegion,detected);
                                        region = detected;trackedRegion=detected;tracker.ResetConfirmation();
                                    }
                                    a.ReportRitualArea(detected);
                                }
                                else if(region is null) {
                                    // No calibration exists after a display change.
                                    // A failed glyph scan alone cannot close a cast;
                                    // the source must prove that no panel outline is
                                    // present in the current client scan.
                                    bool absent=source.DetectionConfirmsAbsence;
                                    long scannedAt=Environment.TickCount64;
                                    tracker.Observe(new([],absent,absent),scannedAt);
                                    if(a.RitualRecoveryPending) {
                                        if(absent) {
                                            recoveryAbsentSince??=scannedAt;
                                            if(scannedAt-recoveryAbsentSince>=1000)recoveryArmed=true;
                                        } else if(!recoveryArmed)recoveryAbsentSince=null;
                                    }
                                    if(absent && (tracker.HasCastState || a.RitualRecoveryPending && !recoveryArmed))
                                        nextDetection=scannedAt+50;
                                }
                            }
                            if (region is null) { await a.Wait(50); continue; }
                            var frame = source.Read(region);
                            a.CheckForeground();
                            if(source.Viewport!=viewport) { await a.Wait(4);continue; }
                            var now = Environment.TickCount64;
                            if (!frame.Reliable || frame.PromptAbsent) unclearSince ??= now;
                            else unclearSince = null;
                            if(a.RitualRecoveryPending) {
                                a.ReportRitualPrompt(null);
                                if(frame.Reliable && frame.PromptAbsent) {
                                    recoveryAbsentSince??=now;
                                    if(now-recoveryAbsentSince>=1000)recoveryArmed=true;
                                } else if(!recoveryArmed)recoveryAbsentSince=null;
                                var first=frame.Glyphs.OrderBy(g=>g.X).ThenBy(g=>g.Y).FirstOrDefault();
                                if(recoveryArmed && frame.Reliable && !frame.PromptAbsent &&
                                   first.Character is not null && first.Confidence is >=65 and <=100 && source.IsPromptVisible(region) &&
                                   a.ResumeRitualAfterNewPanel()) {
                                    tracker.ResetForNewCast();recoveryAbsentSince=null;recoveryArmed=false;
                                    a.TraceRitual("recovered-after-escape; area="+region);
                                } else {
                                    a.ReportStatus(recoveryArmed ? "Ritual: waiting for the next ritual" : "Ritual: paused until this panel closes");
                                    await a.Wait(50);continue;
                                }
                            }
                            if (frame.Reliable && frame.PromptAbsent)
                            {
                                ritualAbsentSince ??= now;
                                a.ReportRitualPrompt(null);
                                // Absence resets the tracker only after sustained evidence.
                                // Keep watching for the next cast without sending the last key again.
                                tracker.Observe(frame, now);
                                if (sentAny && now - ritualAbsentSince >= 1000)
                                { sentAny = false; a.ReportStatus("Ritual: waiting for next ritual"); }
                                await a.Wait(50); continue;
                            }
                            ritualAbsentSince = null;
                            var character = tracker.Observe(frame, now);
                            if (now-lastDiagnosticAt>=1000)
                            {
                                lastDiagnosticAt=now;
                                a.TraceRitual($"status={tracker.Status}; ready={character?.ToString() ?? "none"}; pending={tracker.PendingGlyph}; reliable={frame.Reliable}; glyphs={string.Join(';',frame.Glyphs)}");
                            }
                            RitualGlyph? leadingGlyph = tracker.LeadingGlyph;
                            RitualPromptHighlight? mark = null;
                            if (frame.Reliable && leadingGlyph is { Character: not null, Confidence: >=65 and <=100 } glyph &&
                                glyph.X>=0 && glyph.Y>=0 && glyph.Width>0 && glyph.Height>0 &&
                                glyph.X+glyph.Width<=region.Width && glyph.Y+glyph.Height<=region.Height)
                            {
                                var sent = tracker.WaitingForAdvance && tracker.PendingGlyph is { } previous && previous.Character==glyph.Character &&
                                    (fastRitual || Math.Abs(previous.X-glyph.X)<=2 && Math.Abs(previous.Y-glyph.Y)<=2);
                                mark = Indicator(region, glyph, sent);
                            }
                            a.ReportRitualPrompt(mark);
                            if (lastRitualReason != tracker.Status) { lastRitualReason = tracker.Status; ritualReasonSince = now; }
                            if (now - ritualReasonSince >= 500) a.ReportStatus("Ritual: " + lastRitualReason);
                            if (character is { } c && releasePulse is null && now >= nextRitualInput)
                            {
                                // Recognition may take longer than the panel remains open.
                                // Reconfirm the current letter, then do a cheap fresh outline
                                // capture immediately before sending any key (including retries).
                                var fresh=source.Read(region);
                                a.CheckForeground();
                                var verified=tracker.Observe(fresh,Environment.TickCount64);
                                if(verified!=c || !source.IsPromptVisible(region) || source.Viewport!=viewport)
                                {
                                    a.ReportRitualPrompt(null);
                                    await a.Wait(4);
                                    continue;
                                }
                                leadingGlyph=tracker.LeadingGlyph;
                                mark=leadingGlyph is { } verifiedGlyph ? Indicator(region, verifiedGlyph, true) : null;
                                var ritualKey=char.ToLowerInvariant(c).ToString();
                                a.BeginRitualTap(ritualKey);
                                tracker.ConfirmSent();
                                // Start release timing before diagnostics or overlay work.
                                releasePulse = ReleasePulse(ritualKey);
                                sentAny = true;
                                a.TraceRitual("input-down="+ritualKey+"; glyph="+leadingGlyph);
                                if (mark is not null) a.ReportRitualPrompt(mark with { Sent=true });
                                a.ReportStatus("Ritual: reading prompts");
                            }
                            await a.Wait(4);
                        }
                    }
                    finally
                    {
                        // Observe release failures and cancellation; the engine owns
                        // final key cleanup when a run is canceled or loses focus.
                        if (releasePulse is not null) await releasePulse;
                    }
                }, MacroRunMode.Automatic);
            case "auto-pender":
                var firstPoint = Point("firstcoordinates"); var secondPoint = Point("secondcoordinates");
                if (!int.TryParse(Value("interval_ms"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var clickDelay) || clickDelay is < 20 or > 5000)
                    throw new ArgumentException("interval_ms must be between 20 and 5000 milliseconds.");
                return Bound(async (a, _) =>
                {
                    while (true)
                    {
                        a.MoveOnRobloxScreen(firstPoint); await a.Click(); await a.Wait(clickDelay);
                        a.MoveOnRobloxScreen(secondPoint); await a.Click(); await a.Wait(clickDelay);
                    }
                }, MacroRunMode.Toggle);
            case "right-mouse-parry": return new(id, ["mouse2"], (a, _) => { a.Down("f"); a.Up("f"); return Task.CompletedTask; });
            case "roll-parry": return Bound(async (a, _) => { await a.Tap("f"); await a.Tap("q"); });
            case "roll-m1":
                var highPing = Bool("highPing");
                return Bound(async (a, _) =>
                {
                    a.Down("mouse1"); await a.Wait(highPing ? 61 : 29);
                    a.Down("q"); await a.Wait(highPing ? 43 : 0);
                    a.Down("mouse2"); await a.Wait(highPing ? 35 : 82);
                    a.Up("mouse1"); await a.Wait(highPing ? 54 : 28);
                    a.Up(highPing ? "q" : "mouse2"); await a.Wait(highPing ? 98 : 29);
                    a.Up(highPing ? "mouse2" : "q");
                });
            case "roll-crit": return new(id, ["r"], async (a, _) => { await a.Tap("r"); await a.Wait(10); await a.Tap("q"); }, Suppress: true);
            case "auto-feint": return new(id, ["f"], async (a, _) => await a.Tap(a.Secondary, 50));
            case "auto-roll-cancel": return new(id, ["q"], async (a, _) => { await a.Wait(50); await a.Tap(a.Secondary, 50); });
            case "bell-stack-dodge": return new(id, ["c"], async (a, _) => { await a.Wait(50); await a.Tap("q"); });
            case "bell-stack-parry": return new(id, ["c"], async (a, _) => { await a.Tap("f"); await a.Wait(100); await a.Tap("c"); }, Suppress: true);
            case "flash-map": return new(id, ["m"], async (a, _) => { await a.Wait(200); await a.Tap("m"); }, OnRelease: true);
            case "mb-all": return Bound(async (a, _) => await a.Chat("mb all"));
            case "tacet-toggle":
                var uncrouch = Bool("uncrouch");
                return Bound(async (a, _) => { await a.Wait(50); await a.Tap("ctrl"); await a.Wait(50); await a.Tap("t"); if (uncrouch) { await a.Wait(250); await a.Tap("ctrl"); } });
            case "motif-swap":
                var motif = Key("motifnum"); var weapon = Key("weaponnum");
                return Bound(async (a, _) => { await a.Tap(motif); await a.Wait(100); await a.Click(); await a.Wait(100); await a.Tap(weapon); });
            case "trash-talk":
                var path = Value("txtpath");
                if (path.Length == 0) throw new ArgumentException("txtpath requires a text file.");
                return Bound(async (a, _) => { var lines = (await File.ReadAllLinesAsync(path, a.Token)).Where(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 2000 && !s.Any(char.IsControl)).ToArray(); if (lines.Length == 0) throw new InvalidOperationException("Text file contains no usable messages."); await a.Chat(lines[Random.Shared.Next(lines.Length)]); });
            case "mantra-variants": return new(id, MacroKeys.Parse(Value("keybinds"), "keybinds"), async (a, _) => { for (var i = 0; i < 3; i++) { await a.Wait(100); await a.Tap("f"); } });
            case "mantra-roll-tech": return new(id, MacroKeys.Parse(Value("keybinds"), "keybinds"), async (a, _) => { await a.Wait(50); await a.Tap("q"); });
            case "mantra-slide-tech": return new(id, MacroKeys.Parse(Value("keybinds"), "keybinds"), async (a, _) => await a.Tap("ctrl"));
            case "hold-m1":
                var attack = Key("keyToPress");
                return new(id, ["primary"], async (a, _) => { a.Down(attack); await a.Wait(Timeout.Infinite); }, MacroRunMode.Hold);
            case "auto-uppercut": return new(id, ["ctrl"], async (a, _) => { while (true) { await a.Click(); await a.Wait(10); } }, MacroRunMode.Hold);
            case "dynamic-auto-uppercut":
                var roll = Bool("toRoll");
                return new(id, ["ctrl"], async (a, _) => { await a.Tap("mouse2"); if (roll) { await a.Wait(30); await a.Tap("q"); } });
            case "uppercut-assassinate":
                var dustLunge = Bool("dustLunge");
                return Bound(async (a, _) => { a.Down("ctrl"); await a.Click(); await a.Wait(50); await a.Click(); a.Up("ctrl"); await a.Wait(500); if (!dustLunge) await a.Chat("mb all"); await a.Wait(150); await a.Tap("ctrl"); await a.Wait(200); await a.Click(); });
            case "ankle-cutter":
                var ankleFlow = Key("flowstatekeybind");
                return Bound(async (a, _) => { await a.Tap("w"); await a.Wait(50); a.Down("w"); await a.Wait(50); a.Down("ctrl"); await a.Wait(75); await a.Tap(ankleFlow); a.Down(a.Primary); await a.Wait(50); a.Up("ctrl"); a.Up("w"); await a.Wait(25); a.Up(a.Primary); await a.Wait(250); });
            case "mayhem":
                var mayhemFlow = Key("flowstatekeybind");
                return Bound(async (a, _) => { await a.Tap(mayhemFlow); a.Down("d"); await a.Tap("q"); a.Up("d"); await a.Wait(100); for (var i = 0; i < 5; i++) { await a.Click(); await a.Wait(50); } await a.Wait(250); });
            case "relentless-hunt":
                var huntFlow = Key("flowstatekeybind");
                return Bound(async (a, _) => { await a.Tap("space"); await a.Wait(50); for (var i = 0; i < 4; i++) { await a.Click(); await a.Wait(50); } await a.Tap(huntFlow); for (var i = 0; i < 5; i++) { await a.Click(); await a.Wait(50); } await a.Wait(280); });
            case "rising-star":
                var starFlow = Key("flowstatekeybind");
                return Bound(async (a, _) => { await a.Tap(starFlow); a.Down("ctrl"); for (var i = 0; i < 8; i++) { await a.Wait(1); await a.Tap(a.Secondary, 1); } a.Up("ctrl"); await a.Wait(250); });
            case "auto-agility": return Bound(async (a, _) => { await a.Tap("w"); while (true) { await a.Click(); await a.Wait(50); a.Down("w"); for (var i = 0; i < 11; i++) { await a.Tap("q"); await a.Wait(2500); } a.Up("w"); await a.Wait(1000); } }, MacroRunMode.Toggle);
            case "auto-fortitude": return Bound(async (a, _) => { while (true) { await a.Click(); await a.Wait(25000); await a.Tap("e"); await a.Wait(30000); await a.Tap("e"); await a.Wait(500); } }, MacroRunMode.Toggle);
            case "auto-willpower": return Bound(async (a, _) => { while (true) { await a.Click(); await a.Wait(45000); } }, MacroRunMode.Toggle);
            case "charisma-autofill":
                if(MacroKeys.Parse(Value("keybind"),"keybind").Any(k=>k is "f8" or "f9"))throw new ArgumentException("F8 and F9 are reserved for macro session controls.");
                if(charismaSourceFactory is null || clipboardSetter is null)throw new ArgumentException("Charisma recognition is not configured for this session.");
                return Bound(async (a,_)=>{
                    using var source=charismaSourceFactory();
                    string observed="";long stableSince=0;bool submitted=false;long? absentSince=null;
                    while(true) {
                        a.CheckForeground();var frame=source.Read(a.Token);a.CheckForeground();var now=Environment.TickCount64;
                        if(submitted) {
                            if(frame.Reliable && frame.Quote.Length==0) {
                                absentSince??=now;
                                if(now-absentSince>=200) {
                                    a.ReportRitualPrompt(null);await a.Wait(150);await a.Tap("mouse1",10);
                                    submitted=false;observed="";absentSince=null;a.ReportStatus("Charisma: waiting for next quote");await a.Wait(200);
                                }
                            }else absentSince=null;
                            await a.Wait(30);continue;
                        }
                        if(!frame.Reliable || frame.Quote.Length==0 || frame.InputArea is not {} area) {
                            observed="";a.ReportRitualPrompt(null);await a.Wait(100);continue;
                        }
                        var signature=frame.Quote+"|"+area;
                        a.ReportRitualPrompt(new(area,false));
                        if(signature!=observed){observed=signature;stableSince=now;await a.Wait(25);continue;}
                        if(now-stableSince<25){await a.Wait(8);continue;}
                        a.ReportStatus("Charisma: pasting phrase");
                        await clipboardSetter(frame.Quote,a.Token);a.CheckForeground();
                        a.Move((area.X+area.Width/2,area.Y+area.Height/2));await a.Wait(8);await a.Tap("mouse1",10);await a.Wait(20);
                        a.Down("ctrl");await a.Tap("a");a.Up("ctrl");await a.Wait(10);
                        a.Down("ctrl");await a.Tap("v");a.Up("ctrl");
                        a.ReportRitualPrompt(new(area,true));await a.Wait(60);await a.Tap("enter");
                        submitted=true;a.ReportStatus("Charisma: waiting for completion");
                        await a.Wait(100);
                    }
                },MacroRunMode.Automatic);
            case "auto-log":
                var logPoint = Point("coordinates");
                return Bound(async (a, _) => { a.Move(logPoint); await a.Click(); await a.Tap("shift"); a.Move(logPoint); await a.Click(); });
            case "auto-eat":
                var box = Point("boxcoordinates"); var food = Point("foodcoordinates"); var name = Text("foodname");
                return Bound(async (a, _) => { await a.Tap("tab"); a.Move(box); await a.Click(); await a.Text(name); a.Move(food); await a.Click(); a.Center(); await a.Click(); });
            case "auto-sell":
                var item = Point("itemcoordinates"); var confirmation = Point("confirmationcoordinates"); var sellCount = Repetitions();
                return Bound(async (a, _) => { for (var i = 0; i < sellCount; i++) { a.Move(item); await a.Wait(20); await a.Click(); a.Move(confirmation); await a.Wait(20); await a.Click(); await a.Wait(150); await a.Click(); await a.Wait(150); await a.Click(); await a.Wait(250); } });
            case "auto-drop-notes":
                var note = Point("notecoordinates"); var start = Point("startcoordinates"); var end = Point("endcoordinates"); var confirm = Point("confirmationcoordinates"); var dropCount = Repetitions();
                return Bound(async (a, _) => { for (var i = 0; i < dropCount; i++) { a.Move(note); await a.Wait(20); await a.Click(); a.Move(start); await a.Wait(20); a.Down(a.Primary); await a.Wait(10); a.Move(end); a.Up(a.Primary); await a.Wait(100); a.Move(confirm); await a.Wait(100); await a.Click(); } });
            case "auto-buy":
                var buyStart = Point("startcoordinates"); var buyEnd = Point("endcoordinates"); var submit = Point("submitcoordinates");
                return Bound(async (a, _) => { for (var i = 0; i < 5; i++) { await a.Tap("e"); a.Move(buyStart); await a.Wait(100); a.Down(a.Primary); await a.Wait(75); a.Move(buyEnd); a.Up(a.Primary); await a.Wait(75); await a.Click(); await a.Wait(75); a.Move(submit); await a.Wait(75); await a.Click(); await a.Wait(250); } });
            default: throw new ArgumentException("Unknown macro.");
        }
    }
}
