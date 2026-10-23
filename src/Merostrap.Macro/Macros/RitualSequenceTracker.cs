namespace Merostrap.Macros;

/// <summary>Confirms the next leading prompt and waits for visible acknowledgement before another input.</summary>
public sealed class RitualSequenceTracker
{
    private readonly bool _fastMode;
    private readonly int _retryInterval;
    public RitualSequenceTracker(bool fastMode = false, int retryInterval = 100)
    {
        if(retryInterval<50)throw new ArgumentOutOfRangeException(nameof(retryInterval));
        _fastMode=fastMode;
        _retryInterval=retryInterval;
    }
    private long _lastObservedAt, _lastSentAt;
    private int _retries;
    private float _pendingPeak;
    private bool _retryReady;
    private RitualGlyph? _pending, _candidate;
    private long _candidateSince;
    private RitualGlyph[] _sentSequence = [];
    private RitualGlyph[] _latest = [];
    private readonly List<(RitualGlyph Glyph,float Ceiling)> _retired = [];
    private bool _ready;
    private long? _absentSince;
    public string Status { get; private set; } = "reading prompts";
    internal RitualGlyph? PendingGlyph => _pending;
    internal RitualGlyph? LeadingGlyph => _latest.Length>0 ? _latest[0] : null;
    internal bool WaitingForAdvance { get; private set; }
    internal bool HasCastState => _pending is not null || _sentSequence.Length>0 || _retired.Count>0;

    public void Rebase(Health.HealthRegion previous, Health.HealthRegion current)
    {
        previous.Validate();current.Validate();
        // Glyph coordinates are relative to the calibrated interior. Translate
        // the old queue into the newly detected scale without forgetting input
        // already sent during this cast.
        double sx=(double)current.Width/previous.Width,sy=(double)current.Height/previous.Height;
        RitualGlyph Map(RitualGlyph g) {
            int x=(int)Math.Round(g.X*sx),y=(int)Math.Round(g.Y*sy);
            return g with { X=x,Y=y,Width=Math.Max(1,(int)Math.Round((g.X+g.Width)*sx)-x),
                Height=Math.Max(1,(int)Math.Round((g.Y+g.Height)*sy)-y) };
        }
        if(_pending is {} pending)_pending=Map(pending);
        _sentSequence=_sentSequence.Select(Map).ToArray();
        _latest=_latest.Select(Map).ToArray();
        for(int i=0;i<_retired.Count;i++)_retired[i]=(Map(_retired[i].Glyph),_retired[i].Ceiling);
        ResetConfirmation();
    }

    public void ResetForNewCast()
    {
        _pending=null;_sentSequence=[];_latest=[];_retired.Clear();_retries=0;_pendingPeak=0;
        ResetConfirmation();
    }

    public void ResetConfirmation()
    {
        _candidate = null;
        _ready = false;
        _retryReady = false;
        WaitingForAdvance = false;
        Status = "confirming prompt";
        _absentSince = null;
    }

    public char? Observe(RitualFrame frame, long timestamp)
    {
        _lastObservedAt=timestamp;
        _retryReady=false;
        WaitingForAdvance=false;
        _ready = false;
        Status = "confirming prompt";
        if (!frame.Reliable || frame.Glyphs is null || frame.Glyphs.Count > 12 ||
            frame.Glyphs.Any(g => g.Width is < 1 or > 128 || g.Height is < 1 or > 128))
        { _candidate = null; _absentSince = null; Status = "waiting for a clear ritual frame"; return null; }
        if (frame.Glyphs.Count == 0)
        {
            _candidate = null;
            Status = frame.PromptAbsent ? "waiting for a ritual frame" : "waiting for prompts";
            if (frame.PromptAbsent) {
                _absentSince ??= timestamp;
                // Several fresh outline captures prove the panel has closed.
                // Keeping the old cast for a full second can carry its pending
                // letter, retry limit and fading ghosts into the next cast.
                if(timestamp-_absentSince>=150) {
                    ResetForNewCast();
                    Status = "waiting for a ritual frame";
                }
            } else _absentSince=null;
            return null;
        }
        _absentSince=null;
        _latest = frame.Glyphs.OrderBy(g => g.X).ThenBy(g => g.Y).ToArray();
        // Keep consumed fading glyphs out of the active queue across captures.
        // Only their own dim, nearby image may be retired; a bright same-letter
        // successor must remain a separate prompt.
        for(int i=0;i<_retired.Count;i++) {
            if(_latest.Length==0)break;
            var (ghost,ceiling)=_retired[i];var current=_latest[0];
            if(current.Character==ghost.Character && current.InkBrightness<=ceiling &&
               current.X<=ghost.X+3 && current.X>=ghost.X-Math.Max(6,ghost.Width) &&
               Math.Abs(current.Y-ghost.Y)<=3 && Math.Abs(current.Width-ghost.Width)<=3 && Math.Abs(current.Height-ghost.Height)<=3) {
                _retired[i]=(current,ceiling);_latest=_latest.Skip(1).ToArray();
            } else _retired.RemoveAt(i--);
        }
        if(_latest.Length==0){_candidate=null;Status="waiting for the next prompt";return null;}
        var leading = _latest[0];
        if(_pending is {} visible && leading.Character==visible.Character &&
           Math.Abs(leading.Y-visible.Y)<=Math.Max(4,visible.Height/2) && Math.Abs(leading.X-visible.X)<=visible.Width*3)
            _pendingPeak=Math.Max(_pendingPeak,leading.InkBrightness);
        if(_fastMode && _pending is {} fading && _sentSequence.Length>1 && _latest.Length>1 &&
           leading.Character==fading.Character && leading.Confidence is >=65 and <=100 &&
           leading.InkBrightness<=_pendingPeak*.9f && leading.X<fading.X-2 &&
           Math.Abs(leading.Y-fading.Y)<=3) {
            int shift=leading.X-fading.X;var next=_latest[1];var expected=_sentSequence[1];
            if(IsCharacter(expected.Character) && expected.Confidence is >=65 and <=100 &&
               next.Character==expected.Character && next.Confidence is >=65 and <=100 &&
               next.InkBrightness>=expected.InkBrightness*.95f &&
               Math.Abs(next.X-expected.X-shift)<=3 && Math.Abs(next.Y-expected.Y)<=3 &&
               Math.Abs(next.Width-expected.Width)<=2 && Math.Abs(next.Height-expected.Height)<=2) {
                _retired.Add((leading,_pendingPeak*.9f));
                _latest=_latest.Skip(1).ToArray();leading=_latest[0];
                _pending=null;_candidate=null;_retries=0;
            }
        }
        if (!IsCharacter(leading.Character) || !(leading.Confidence >= 65 && leading.Confidence <= 100))
        { _candidate = null; Status = "unreadable leading character"; return null; }
        if (_pending is { } sent)
        {
            // Read the current leading glyph independently of incoming trailing
            // prompts. A changed letter acknowledges removal of the previous one.
            // Repeated letters require a shifted known queue with a distinct
            // anchor character, the known successor's distinct row, or complete
            // removal with every remaining glyph at its known fixed position.
            bool fixedPositionRemoval = _sentSequence.Length > 1 &&
                _latest.Length == _sentSequence.Length - 1 &&
                _sentSequence[1].X - sent.X > Math.Max(12, sent.Width * 1.5) &&
                _latest.Zip(_sentSequence.Skip(1)).All(pair =>
                    IsCharacter(pair.Second.Character) && pair.Second.Confidence is >= 65 and <= 100 &&
                    pair.First.Character == pair.Second.Character && pair.First.Confidence is >= 65 and <= 100 &&
                    Math.Abs(pair.First.X - pair.Second.X) <= 3 && Math.Abs(pair.First.Y - pair.Second.Y) <= 3 &&
                    Math.Abs(pair.First.Width - pair.Second.Width) <= 2 && Math.Abs(pair.First.Height - pair.Second.Height) <= 2);
            // A completed row can be replaced before an empty frame is captured.
            // Its last letter may also be the first letter of the new row. A
            // whole, clearly spaced queue appearing to the LEFT of that final
            // singleton proves a new leading prompt; growth or movement to the
            // right alone must still never replay an unresolved key.
            bool freshRow = _sentSequence.Length == 1 && _latest.Length >= 3 &&
                sent.X - leading.X > Math.Max(24, sent.Width * 2) &&
                _latest.All(g=>IsCharacter(g.Character) && g.Confidence is >=65 and <=100) &&
                _latest.Zip(_latest.Skip(1)).All(pair=>pair.Second.X-pair.First.X > Math.Max(12,pair.First.Width*1.5));
            // Follow the known successor while the queue slides. Its position
            // must still be closer to its own old slot than the consumed slot;
            // otherwise a repeated outgoing letter could masquerade as the next.
            // Confirm a second known anchor, without requiring an incoming tail
            // (which may be clipped or still unreadable) to match the old queue.
            bool movingSuccessor = false;
            if (_fastMode && _sentSequence.Length > 1 &&
                _sentSequence[1].Character == leading.Character &&
                _sentSequence[1].Confidence is >=65 and <=100)
            {
                var successor=_sentSequence[1];
                int spacing=successor.X-sent.X, shift=leading.X-successor.X;
                bool Matches(RitualGlyph actual,RitualGlyph expected) =>
                    IsCharacter(expected.Character) && expected.Confidence is >=65 and <=100 &&
                    actual.Character==expected.Character && actual.Confidence is >=65 and <=100 &&
                    Math.Abs(actual.X-expected.X-shift)<=3 && Math.Abs(actual.Y-expected.Y)<=3 &&
                    Math.Abs(actual.Width-expected.Width)<=2 && Math.Abs(actual.Height-expected.Height)<=2;
                movingSuccessor=spacing>Math.Max(12,sent.Width*1.5) &&
                    shift<=3 && shift>=-spacing/2 && Matches(leading,successor) &&
                    ((_sentSequence.Length>=3 && _latest.Length>=2 && Matches(_latest[1],_sentSequence[2])) ||
                     (_sentSequence.Length==2 && _latest.Length==1));
            }
            bool acknowledged = movingSuccessor || leading.Character != sent.Character || fixedPositionRemoval || freshRow ||
                (_sentSequence.Length >= 3 && _latest.Length >= _sentSequence.Length - 1 &&
                 _sentSequence.Skip(1).All(g=>IsCharacter(g.Character) && g.Confidence is >=65 and <=100) &&
                 _sentSequence.Skip(1).Any(g=>g.Character!=sent.Character) &&
                 _latest.Take(_sentSequence.Length-1).All(g=>IsCharacter(g.Character) && g.Confidence is >=65 and <=100) &&
                 _latest.Take(_sentSequence.Length-1).Select(g=>g.Character).SequenceEqual(_sentSequence.Skip(1).Select(g=>g.Character))) ||
                (_sentSequence.Length > 1 && leading.Character == _sentSequence[1].Character &&
                 IsCharacter(_sentSequence[1].Character) && _sentSequence[1].Confidence is >= 65 and <= 100 &&
                 Math.Abs(_sentSequence[1].Y - sent.Y) > Math.Max(6, sent.Height * 0.7) &&
                 Math.Abs(leading.Y - _sentSequence[1].Y) <= 6);
            if (!acknowledged)
            {
                WaitingForAdvance=true;
                if(!_fastMode || timestamp-_lastSentAt<_retryInterval || _retries>=2)
                {
                    _candidate=null;
                    Status=_fastMode && _retries>=2 ? $"retry limit reached for {sent.Character}" : $"waiting for {sent.Character} to advance";
                    return null;
                }
                // Reconfirm the current, visible leading character. Never recover
                // from a timer alone or replay a missing/uncertain prompt.
                _retryReady=true;
            }
        }
        if (_candidate is not { } previous || previous.Character != leading.Character ||
            ((!_fastMode || _retryReady) && (Math.Abs(previous.Y - leading.Y) > 2 || Math.Abs(previous.X - leading.X) > 2 ||
            Math.Abs(previous.Width-leading.Width)>2 || Math.Abs(previous.Height-leading.Height)>2)))
        { _candidate = leading; _candidateSince = timestamp; return null; }
        // Compare to the original candidate, not the previous sample: repeated
        // small movements are still motion. Span more than one 30/60 Hz display
        // frame so a paused animation frame cannot authorize an early key press.
        if (timestamp - _candidateSince < (_fastMode && !_retryReady ? 8 : 40)) return null;
        _ready = true;
        Status = _retryReady ? $"retrying {leading.Character}" : "reading prompts";
        return leading.Character;
    }

    public void ConfirmSent()
    {
        if (!_ready || _candidate is null) throw new InvalidOperationException("No confirmed ritual character is ready.");
        _retries=_retryReady ? _retries+1 : 0;
        _lastSentAt=_lastObservedAt;
        _pending = _latest[0];
        _pendingPeak=_pending.Value.InkBrightness;
        _sentSequence = _latest.ToArray();
        _candidate = null;
        _ready = false;
    }

    private static bool IsCharacter(char? c) => c is >= 'A' and <= 'Z' or >= '0' and <= '9';
}
