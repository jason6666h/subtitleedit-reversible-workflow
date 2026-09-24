using System.Text.Json;

namespace AudioWorkflow;

/// <summary>A compact PCM patch applied on the timeline that existed when AU returned it.</summary>
public sealed record AudioRepairPatch(string Id, string RelativeFileName, string Sha256,
    long StartFrame, long EndFrame, SourceSpan[] AppliedTimeline);

/// <summary>Result of a streaming, sample-exact replay audit. No full temporary WAV is created.</summary>
public sealed record AudioProjectVerificationResult(string Sha256, WaveInfo Wave, long ComparedFrames,
    long DeletedFrames, long RepairedFrames, int RepairPatchCount);

/// <summary>
/// Rebuilds disposable working WAVs from one immutable PCM baseline, retained timeline spans,
/// and small AU repair patches. Historical working WAVs therefore do not need to stay on disk.
/// </summary>
public static class AudioDeltaStore
{
    public static AudioRepairPatch[] ReadPatches(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var patches = JsonSerializer.Deserialize<AudioRepairPatch[]>(json) ?? [];
        foreach (var patch in patches)
        {
            if (string.IsNullOrWhiteSpace(patch.Id) || string.IsNullOrWhiteSpace(patch.RelativeFileName) ||
                string.IsNullOrWhiteSpace(patch.Sha256) || patch.EndFrame <= patch.StartFrame)
                throw new InvalidDataException("Invalid compact audio repair patch.");
            TimelineMap.Validate(patch.AppliedTimeline);
            if (patch.StartFrame < 0 || patch.EndFrame > TimelineMap.Validate(patch.AppliedTimeline))
                throw new InvalidDataException("Compact audio repair patch is outside its timeline.");
        }
        return patches;
    }

    public static string WritePatches(IEnumerable<AudioRepairPatch> patches) =>
        JsonSerializer.Serialize(patches.ToArray());

    public static async Task<AudioRepairPatch> CapturePatchAsync(string fullAudioPath, string fullAudioHash,
        string projectDirectory, string id, long startFrame, long endFrame,
        IReadOnlyList<SourceSpan> appliedTimeline, CancellationToken token,
        AudioRevisionSource? validatedSource = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var timelineLength = TimelineMap.Validate(appliedTimeline);
        if (startFrame < 0 || endFrame <= startFrame || endFrame > timelineLength)
            throw new InvalidDataException("Repair patch interval is outside the edited timeline.");
        var root = Path.GetFullPath(projectDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var deltaDirectory = Path.Combine(root, "deltas");
        Directory.CreateDirectory(deltaDirectory);
        var relative = Path.Combine("deltas", "repair-" + id + ".wav");
        var target = ResolveInside(root, relative);
        using var ownedSource = validatedSource == null
            ? await AudioRevisionSource.OpenAsync(fullAudioPath, fullAudioHash, token).ConfigureAwait(false)
            : null;
        var sourceGuard = validatedSource ?? ownedSource!;
        if (!sourceGuard.Matches(fullAudioPath, fullAudioHash))
            throw new InvalidDataException("Validated repair output does not match the compact patch source.");
        await using var source = sourceGuard.OpenPinnedRead();
        var layout = PcmWaveLayout.Read(source);
        if (layout.Info.SampleCount != timelineLength)
            throw new InvalidDataException("Repair output and edited timeline lengths differ.");
        try
        {
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                layout.WriteHeader(output, endFrame - startFrame);
                await CopyAsync(source, output, layout.DataOffset + startFrame * layout.BlockAlign,
                    (endFrame - startFrame) * layout.BlockAlign, token).ConfigureAwait(false);
                if ((((endFrame - startFrame) * layout.BlockAlign) & 1) != 0) output.WriteByte(0);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            using var patchGuard = await AudioRevisionSource.OpenAndHashAsync(target, token).ConfigureAwait(false);
            return new AudioRepairPatch(id, relative.Replace('\\', '/'), patchGuard.Sha256,
                startFrame, endFrame, appliedTimeline.ToArray());
        }
        catch
        {
            if (File.Exists(target)) File.Delete(target);
            throw;
        }
    }

    public static async Task<string> RebuildAsync(string baselinePath, string baselineHash,
        IReadOnlyList<SourceSpan> baselineTimeline, IReadOnlyList<SourceSpan> targetTimeline,
        IReadOnlyList<AudioRepairPatch> patches, string projectDirectory, string outputPath,
        CancellationToken token)
    {
        var root = Path.GetFullPath(projectDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var output = ResolveInside(root, Path.GetRelativePath(root, Path.GetFullPath(outputPath)));
        if (File.Exists(output)) throw new IOException("Refusing to overwrite an existing working audio file.");
        using var baselineGuard = await AudioRevisionSource.OpenAsync(baselinePath, baselineHash, token).ConfigureAwait(false);
        await using var baseline = baselineGuard.OpenPinnedRead();
        var layout = PcmWaveLayout.Read(baseline);
        if (TimelineMap.Validate(baselineTimeline) != layout.Info.SampleCount)
            throw new InvalidDataException("Compact baseline and its timeline differ.");
        var targetFrames = TimelineMap.Validate(targetTimeline);
        var partial = output + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var rebuilt = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                131072, FileOptions.Asynchronous))
            {
                layout.WriteHeader(rebuilt, targetFrames);
                var rebuiltDataOffset = rebuilt.Position;
                foreach (var span in targetTimeline)
                {
                    var baselineOffset = FindBaselineOffset(baselineTimeline, span.Start, span.End);
                    await CopyAsync(baseline, rebuilt, layout.DataOffset + baselineOffset * layout.BlockAlign,
                        (span.End - span.Start) * layout.BlockAlign, token).ConfigureAwait(false);
                }
                if (((targetFrames * layout.BlockAlign) & 1) != 0) rebuilt.WriteByte(0);

                foreach (var patch in patches)
                {
                    var patchPath = ResolveInside(root, patch.RelativeFileName);
                    using var patchGuard = await AudioRevisionSource.OpenAsync(patchPath, patch.Sha256, token).ConfigureAwait(false);
                    await using var patchStream = patchGuard.OpenPinnedRead();
                    var patchLayout = PcmWaveLayout.Read(patchStream);
                    if (patchLayout.Info != layout.Info with { SampleCount = patch.EndFrame - patch.StartFrame } ||
                        !patchLayout.Format.AsSpan().SequenceEqual(layout.Format))
                        throw new InvalidDataException("Compact repair patch format differs from its baseline.");
                    foreach (var segment in Slice(patch.AppliedTimeline, patch.StartFrame, patch.EndFrame))
                    {
                        long targetCursor = 0;
                        foreach (var targetSpan in targetTimeline)
                        {
                            var start = Math.Max(segment.OriginalStart, targetSpan.Start);
                            var end = Math.Min(segment.OriginalEnd, targetSpan.End);
                            if (end > start)
                            {
                                patchStream.Position = patchLayout.DataOffset +
                                    (segment.PatchOffset + start - segment.OriginalStart) * patchLayout.BlockAlign;
                                rebuilt.Position = rebuiltDataOffset +
                                    (targetCursor + start - targetSpan.Start) * layout.BlockAlign;
                                await CopyCurrentAsync(patchStream, rebuilt, (end - start) * layout.BlockAlign, token)
                                    .ConfigureAwait(false);
                            }
                            targetCursor += targetSpan.End - targetSpan.Start;
                        }
                    }
                }
                await rebuilt.FlushAsync(token).ConfigureAwait(false);
                rebuilt.Flush(true);
            }
            File.Move(partial, output, false);
            using var result = await AudioRevisionSource.OpenAndHashAsync(output, token).ConfigureAwait(false);
            return result.Sha256;
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            if (File.Exists(output)) File.Delete(output);
            throw;
        }
    }

    /// <summary>
    /// Replays the immutable baseline, retained timeline and AU patches directly against the current
    /// WAV. Every PCM byte must match the recorded operations; no second full-size WAV is written.
    /// </summary>
    public static async Task<AudioProjectVerificationResult> VerifyAsync(string baselinePath, string baselineHash,
        IReadOnlyList<SourceSpan> baselineTimeline, IReadOnlyList<SourceSpan> targetTimeline,
        IReadOnlyList<AudioRepairPatch> patches, string projectDirectory, string currentPath, string currentHash,
        CancellationToken token, AudioRevisionSource? validatedCurrent = null)
    {
        var root = Path.GetFullPath(projectDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using var baselineGuard = await AudioRevisionSource.OpenAsync(baselinePath, baselineHash, token).ConfigureAwait(false);
        using var ownedCurrent = validatedCurrent == null
            ? await AudioRevisionSource.OpenAsync(currentPath, currentHash, token).ConfigureAwait(false)
            : null;
        var currentGuard = validatedCurrent ?? ownedCurrent!;
        if (!currentGuard.Matches(currentPath, currentHash))
            throw new InvalidDataException("目前音訊與待驗證版本不一致。");

        await using var baseline = baselineGuard.OpenPinnedRead();
        await using var current = currentGuard.OpenPinnedRead();
        var baselineLayout = PcmWaveLayout.Read(baseline);
        var currentLayout = PcmWaveLayout.Read(current);
        var baselineFrames = TimelineMap.Validate(baselineTimeline);
        var targetFrames = TimelineMap.Validate(targetTimeline);
        if (baselineFrames != baselineLayout.Info.SampleCount)
            throw new InvalidDataException("驗證失敗：不可變基準與基準時間軸長度不同。");
        if (targetFrames != currentLayout.Info.SampleCount ||
            currentLayout.Info != baselineLayout.Info with { SampleCount = targetFrames } ||
            !currentLayout.Format.AsSpan().SequenceEqual(baselineLayout.Format))
            throw new InvalidDataException("驗證失敗：目前音檔格式或樣本數與操作時間軸不同。");

        var readers = new List<VerifiedPatchReader>();
        try
        {
            foreach (var patch in patches)
            {
                ValidatePatch(patch);
                var patchPath = ResolveInside(root, patch.RelativeFileName);
                var guard = await AudioRevisionSource.OpenAsync(patchPath, patch.Sha256, token).ConfigureAwait(false);
                FileStream? stream = null;
                try
                {
                    stream = guard.OpenPinnedRead();
                    var layout = PcmWaveLayout.Read(stream);
                    if (layout.Info != baselineLayout.Info with { SampleCount = patch.EndFrame - patch.StartFrame } ||
                        !layout.Format.AsSpan().SequenceEqual(baselineLayout.Format))
                        throw new InvalidDataException("驗證失敗：AU 修音差分格式或長度不一致。");
                    readers.Add(new VerifiedPatchReader(guard, stream, layout,
                        Slice(patch.AppliedTimeline, patch.StartFrame, patch.EndFrame).ToArray()));
                    stream = null;
                }
                catch
                {
                    stream?.Dispose();
                    guard.Dispose();
                    throw;
                }
            }

            var expectedBuffer = new byte[131072];
            var actualBuffer = new byte[expectedBuffer.Length];
            long outputFrame = 0;
            long repairedFrames = 0;
            foreach (var span in targetTimeline)
            {
                var boundaries = new SortedSet<long> { span.Start, span.End };
                foreach (var reader in readers)
                foreach (var segment in reader.Segments)
                {
                    var start = Math.Max(span.Start, segment.OriginalStart);
                    var end = Math.Min(span.End, segment.OriginalEnd);
                    if (end > start) { boundaries.Add(start); boundaries.Add(end); }
                }

                var points = boundaries.ToArray();
                for (var i = 0; i + 1 < points.Length; i++)
                {
                    var start = points[i];
                    var end = points[i + 1];
                    if (end <= start) continue;
                    VerifiedPatchReader? selectedReader = null;
                    PatchSegment? selectedSegment = null;
                    // RebuildAsync applies patches in list order, so the newest overlapping patch wins.
                    foreach (var reader in readers)
                    foreach (var segment in reader.Segments)
                        if (start >= segment.OriginalStart && end <= segment.OriginalEnd)
                        {
                            selectedReader = reader;
                            selectedSegment = segment;
                        }

                    Stream expected;
                    long expectedOffset;
                    if (selectedReader != null && selectedSegment != null)
                    {
                        expected = selectedReader.Stream;
                        expectedOffset = selectedReader.Layout.DataOffset +
                            (selectedSegment.PatchOffset + start - selectedSegment.OriginalStart) * baselineLayout.BlockAlign;
                        repairedFrames += end - start;
                    }
                    else
                    {
                        expected = baseline;
                        expectedOffset = baselineLayout.DataOffset +
                            FindBaselineOffset(baselineTimeline, start, end) * baselineLayout.BlockAlign;
                    }

                    var frames = end - start;
                    await CompareAsync(expected, expectedOffset, current,
                        currentLayout.DataOffset + outputFrame * currentLayout.BlockAlign,
                        frames * currentLayout.BlockAlign, currentLayout.BlockAlign, outputFrame,
                        expectedBuffer, actualBuffer, token).ConfigureAwait(false);
                    outputFrame += frames;
                }
            }
            if (outputFrame != targetFrames)
                throw new InvalidDataException("驗證失敗：操作重播沒有覆蓋完整音訊時間軸。");
            return new(currentGuard.Sha256, currentLayout.Info, outputFrame,
                baselineFrames - targetFrames, repairedFrames, patches.Count);
        }
        finally
        {
            foreach (var reader in readers) reader.Dispose();
        }
    }

    private sealed record PatchSegment(long PatchOffset, long OriginalStart, long OriginalEnd);

    private sealed class VerifiedPatchReader(AudioRevisionSource guard, FileStream stream,
        PcmWaveLayout layout, PatchSegment[] segments) : IDisposable
    {
        public FileStream Stream { get; } = stream;
        public PcmWaveLayout Layout { get; } = layout;
        public PatchSegment[] Segments { get; } = segments;
        public void Dispose() { Stream.Dispose(); guard.Dispose(); }
    }

    private static void ValidatePatch(AudioRepairPatch patch)
    {
        if (string.IsNullOrWhiteSpace(patch.Id) || string.IsNullOrWhiteSpace(patch.RelativeFileName) ||
            string.IsNullOrWhiteSpace(patch.Sha256) || patch.EndFrame <= patch.StartFrame)
            throw new InvalidDataException("Invalid compact audio repair patch.");
        var length = TimelineMap.Validate(patch.AppliedTimeline);
        if (patch.StartFrame < 0 || patch.EndFrame > length)
            throw new InvalidDataException("Compact audio repair patch is outside its timeline.");
    }

    private static async Task CompareAsync(Stream expected, long expectedOffset, Stream actual, long actualOffset,
        long bytes, int blockAlign, long outputFrame, byte[] expectedBuffer, byte[] actualBuffer,
        CancellationToken token)
    {
        expected.Position = expectedOffset;
        actual.Position = actualOffset;
        long compared = 0;
        while (bytes > 0)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(bytes, expectedBuffer.Length);
            await expected.ReadExactlyAsync(expectedBuffer.AsMemory(0, count), token).ConfigureAwait(false);
            await actual.ReadExactlyAsync(actualBuffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (!expectedBuffer.AsSpan(0, count).SequenceEqual(actualBuffer.AsSpan(0, count)))
            {
                var difference = 0;
                while (difference < count && expectedBuffer[difference] == actualBuffer[difference]) difference++;
                var frame = outputFrame + (compared + difference) / blockAlign;
                throw new InvalidDataException($"驗證失敗：第 {frame} 個樣本框與操作記錄不一致，拒絕輸出。");
            }
            compared += count;
            bytes -= count;
        }
    }

    private static IEnumerable<PatchSegment> Slice(IReadOnlyList<SourceSpan> timeline, long start, long end)
    {
        TimelineMap.Validate(timeline);
        long cursor = 0;
        foreach (var span in timeline)
        {
            var next = cursor + span.End - span.Start;
            var a = Math.Max(start, cursor);
            var b = Math.Min(end, next);
            if (b > a) yield return new PatchSegment(a - start, span.Start + a - cursor, span.Start + b - cursor);
            cursor = next;
        }
    }

    private static long FindBaselineOffset(IReadOnlyList<SourceSpan> baseline, long start, long end)
    {
        long cursor = 0;
        foreach (var span in baseline)
        {
            if (start >= span.Start && end <= span.End) return cursor + start - span.Start;
            cursor += span.End - span.Start;
        }
        throw new InvalidDataException("Edited timeline is not a subset of its compact baseline.");
    }

    private static string ResolveInside(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Compact audio path must be relative.");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Compact audio path escaped its project.");
        return full;
    }

    private static async Task CopyAsync(Stream source, Stream output, long offset, long bytes, CancellationToken token)
    {
        source.Position = offset;
        await CopyCurrentAsync(source, output, bytes, token).ConfigureAwait(false);
    }

    private static async Task CopyCurrentAsync(Stream source, Stream output, long bytes, CancellationToken token)
    {
        var buffer = new byte[131072];
        while (bytes > 0)
        {
            token.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(bytes, buffer.Length)), token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Incomplete compact PCM data.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            bytes -= read;
        }
    }
}
