namespace AudioWorkflow;

/// <summary>Half-open retained source frame ranges, ordered on the edited timeline.</summary>
public sealed record SourceSpan(long Start, long End);

public static class TimelineMap
{
    public static SourceSpan[] Delete(IReadOnlyList<SourceSpan> spans, long start, long end)
    {
        var length = Validate(spans);
        if (start < 0 || end <= start || end > length || end - start == length)
            throw new InvalidDataException("Invalid timeline deletion.");
        var result = new List<SourceSpan>();
        long cursor = 0;
        foreach (var span in spans)
        {
            var next = checked(cursor + span.End - span.Start);
            if (cursor < start) Add(span.Start, span.Start + Math.Min(start - cursor, span.End - span.Start));
            if (next > end) Add(span.Start + Math.Max(0, end - cursor), span.End);
            cursor = next;
        }
        return result.ToArray();
        void Add(long a, long b) { if (b > a) result.Add(new(a, b)); }
    }

    public static long Validate(IReadOnlyList<SourceSpan> spans)
    {
        long total = 0, previousEnd = 0;
        foreach (var span in spans)
        {
            if (span.Start < previousEnd || span.End <= span.Start) throw new InvalidDataException("Invalid source timeline map.");
            total = checked(total + span.End - span.Start);
            previousEnd = span.End;
        }
        if (total == 0) throw new InvalidDataException("Empty source timeline map.");
        return total;
    }

    public static long ToOriginal(IReadOnlyList<SourceSpan> spans, long edited)
    {
        var total = Validate(spans);
        if (edited < 0 || edited > total) throw new ArgumentOutOfRangeException(nameof(edited));
        foreach (var span in spans)
        {
            var size = span.End - span.Start;
            if (edited < size) return span.Start + edited;
            edited -= size;
        }
        return spans[^1].End;
    }

    // Deleted source positions map to their splice boundary, never to invented audio.
    public static long ToEdited(IReadOnlyList<SourceSpan> spans, long original)
    {
        Validate(spans);
        if (original < 0) throw new ArgumentOutOfRangeException(nameof(original));
        long cursor = 0;
        foreach (var span in spans)
        {
            if (original < span.Start) return cursor;
            if (original < span.End) return cursor + original - span.Start;
            cursor = checked(cursor + span.End - span.Start);
        }
        return cursor;
    }
}
