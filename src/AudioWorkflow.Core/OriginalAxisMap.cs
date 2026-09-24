namespace AudioWorkflow;

/// <summary>Immutable, pre-indexed display map. No JSON, disk access or linear scan on a playback tick.</summary>
public sealed class OriginalAxisMap
{
    private readonly SourceSpan[] _spans;
    private readonly long[] _starts;
    private readonly long[] _offsets;
    public int Rate { get; }
    public long EditedFrames { get; }
    public IReadOnlyList<SourceSpan> Spans => _spans;
    public OriginalAxisMap(IEnumerable<SourceSpan> spans,int rate)
    {
        if(rate<=0) throw new ArgumentOutOfRangeException(nameof(rate));
        _spans=spans.ToArray(); Rate=rate; EditedFrames=TimelineMap.Validate(_spans);
        _starts=new long[_spans.Length];_offsets=new long[_spans.Length];
        long offset=0;
        for(int i=0;i<_spans.Length;i++) { _starts[i]=_spans[i].Start;_offsets[i]=offset;offset+=_spans[i].End-_spans[i].Start; }
    }
    public double ToOriginal(double seconds)
    {
        var frame=Math.Clamp((long)Math.Round(Math.Max(0,seconds)*Rate),0,EditedFrames);
        int i=Array.BinarySearch(_offsets,frame);if(i<0)i=~i-1;
        return (double)(_spans[i].Start+frame-_offsets[i])/Rate;
    }
    public double ToEdited(double seconds,out bool removed)
    {
        var frame=Math.Max(0,(long)Math.Round(seconds*Rate));
        int i=Array.BinarySearch(_starts,frame);if(i<0)i=~i-1;
        if(i<0) { removed=true;return 0; }
        removed=frame>=_spans[i].End;
        return (double)(_offsets[i]+Math.Min(frame-_spans[i].Start,_spans[i].End-_spans[i].Start))/Rate;
    }
}
