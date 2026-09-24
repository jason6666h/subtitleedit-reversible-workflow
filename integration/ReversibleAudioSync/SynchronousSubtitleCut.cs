using System;
using Nikse.SubtitleEdit.Core.Common;

namespace Nikse.SubtitleEdit.Features.Main;

/// <summary>One surviving paragraph per input paragraph; never duplicates text at a splice.</summary>
internal static class SynchronousSubtitleCut
{
    internal static Subtitle Remove(Subtitle source, double startSeconds, double endSeconds)
    {
        if (!double.IsFinite(startSeconds) || !double.IsFinite(endSeconds) ||
            startSeconds < 0 || endSeconds <= startSeconds) throw new ArgumentOutOfRangeException(nameof(endSeconds));
        var result = new Subtitle { Header = source.Header, Footer = source.Footer };
        var start = startSeconds * 1000;
        var end = endSeconds * 1000;
        double Map(double time) => time <= start ? time : time >= end ? time - (end - start) : start;
        foreach (var paragraph in source.Paragraphs)
        {
            var mappedStart = Map(paragraph.StartTime.TotalMilliseconds);
            var mappedEnd = Map(paragraph.EndTime.TotalMilliseconds);
            if (mappedEnd <= mappedStart) continue; // Entire paragraph removed with its audio.
            var copy = new Paragraph(paragraph);
            copy.StartTime.TotalMilliseconds = mappedStart;
            copy.EndTime.TotalMilliseconds = mappedEnd;
            result.Paragraphs.Add(copy);
        }
        result.Renumber();
        return result;
    }
}
