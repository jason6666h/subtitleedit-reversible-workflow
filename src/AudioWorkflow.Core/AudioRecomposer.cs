namespace AudioWorkflow;

/// <summary>
/// Rebuilds a target original-axis timeline from the current PCM audio, borrowing only
/// explicitly selected original ranges from an alternate revision. This preserves later
/// edits outside the cancelled operation and also supports pre-compaction projects.
/// </summary>
public static class AudioRecomposer
{
    public static async Task<string> RecomposeAsync(
        string currentPath, string currentHash, IReadOnlyList<SourceSpan> currentTimeline,
        string alternatePath, string alternateHash, IReadOnlyList<SourceSpan> alternateTimeline,
        IReadOnlyList<SourceSpan> targetTimeline, IReadOnlyList<SourceSpan> useAlternateRanges,
        string outputPath, CancellationToken token)
    {
        TimelineMap.Validate(currentTimeline);
        TimelineMap.Validate(alternateTimeline);
        var targetFrames = TimelineMap.Validate(targetTimeline);
        var replacements = Normalize(useAlternateRanges);
        if (replacements.Length == 0) throw new InvalidDataException("No alternate audio range was selected.");
        if (File.Exists(outputPath)) throw new IOException("Refusing to overwrite an existing recomposed audio file.");

        using var currentGuard = await AudioRevisionSource.OpenAsync(currentPath, currentHash, token).ConfigureAwait(false);
        using var alternateGuard = await AudioRevisionSource.OpenAsync(alternatePath, alternateHash, token).ConfigureAwait(false);
        await using var current = currentGuard.OpenPinnedRead();
        await using var alternate = alternateGuard.OpenPinnedRead();
        var currentLayout = PcmWaveLayout.Read(current);
        var alternateLayout = PcmWaveLayout.Read(alternate);
        if (TimelineMap.Validate(currentTimeline) != currentLayout.Info.SampleCount ||
            TimelineMap.Validate(alternateTimeline) != alternateLayout.Info.SampleCount)
            throw new InvalidDataException("Audio length and original-axis timeline differ.");
        if (currentLayout.Info with { SampleCount = 0 } != alternateLayout.Info with { SampleCount = 0 } ||
            !currentLayout.Format.AsSpan().SequenceEqual(alternateLayout.Format))
            throw new InvalidDataException("Audio revisions use incompatible PCM formats.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var partial = outputPath + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                currentLayout.WriteHeader(output, targetFrames);
                foreach (var target in targetTimeline)
                {
                    var original = target.Start;
                    while (original < target.End)
                    {
                        var replacement = FindRange(replacements, original);
                        var useAlternate = replacement != null || !TryFind(currentTimeline, original, out _, out _);
                        var sourceTimeline = useAlternate ? alternateTimeline : currentTimeline;
                        var source = useAlternate ? alternate : current;
                        var layout = useAlternate ? alternateLayout : currentLayout;
                        if (!TryFind(sourceTimeline, original, out var sourceSpan, out var sourceOffset))
                            throw new InvalidDataException("A target range is unavailable in both audio revisions.");
                        var end = Math.Min(target.End, sourceSpan.End);
                        if (replacement != null) end = Math.Min(end, replacement.End);
                        else
                        {
                            var next = replacements.FirstOrDefault(r => r.Start > original);
                            if (next != null) end = Math.Min(end, next.Start);
                        }
                        await CopyAsync(source, output, layout.DataOffset + sourceOffset * layout.BlockAlign,
                            (end - original) * layout.BlockAlign, token).ConfigureAwait(false);
                        original = end;
                    }
                }
                if (((targetFrames * currentLayout.BlockAlign) & 1) != 0) output.WriteByte(0);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            File.Move(partial, outputPath, false);
            using var result = await AudioRevisionSource.OpenAndHashAsync(outputPath, token).ConfigureAwait(false);
            return result.Sha256;
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            throw;
        }
    }

    private static SourceSpan[] Normalize(IReadOnlyList<SourceSpan> ranges)
    {
        var result = new List<SourceSpan>();
        foreach (var range in ranges.OrderBy(r => r.Start).ThenBy(r => r.End))
        {
            if (range.Start < 0 || range.End <= range.Start) throw new InvalidDataException("Invalid replacement range.");
            if (result.Count == 0 || range.Start > result[^1].End) result.Add(range);
            else result[^1] = new SourceSpan(result[^1].Start, Math.Max(result[^1].End, range.End));
        }
        return result.ToArray();
    }

    private static SourceSpan? FindRange(IReadOnlyList<SourceSpan> ranges, long original) =>
        ranges.FirstOrDefault(r => original >= r.Start && original < r.End);

    private static bool TryFind(IReadOnlyList<SourceSpan> timeline, long original,
        out SourceSpan span, out long editedOffset)
    {
        editedOffset = 0;
        foreach (var candidate in timeline)
        {
            if (original >= candidate.Start && original < candidate.End)
            {
                span = candidate;
                editedOffset += original - candidate.Start;
                return true;
            }
            editedOffset += candidate.End - candidate.Start;
        }
        span = null!;
        return false;
    }

    private static async Task CopyAsync(Stream source, Stream output, long offset, long bytes,
        CancellationToken token)
    {
        source.Position = offset;
        var buffer = new byte[131072];
        while (bytes > 0)
        {
            token.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(bytes, buffer.Length)), token)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Incomplete PCM source data.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            bytes -= read;
        }
    }
}
