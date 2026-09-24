using System.Security.Cryptography;

namespace AudioWorkflow;

public sealed record AuditionHandoff(string BeforePath, string EditPath, string BeforeHash, WaveInfo Timeline, AuditionRegion? Region = null);

public sealed class AuditionReturnResult(string path, string sha256, AudioRevisionSource validation) : IDisposable
{
    private AudioRevisionSource? _validation = validation;
    public string Path { get; } = path;
    public string Sha256 { get; } = sha256;
    public AudioRevisionSource Validation => _validation ?? throw new ObjectDisposedException(nameof(AuditionReturnResult));
    public AudioRevisionSource DetachValidation()
    {
        var result = Validation;
        _validation = null;
        return result;
    }
    public void Dispose() { _validation?.Dispose(); _validation = null; }
}

/// <summary>SE only opens immutable snapshots. No player, watcher or deny-write handle touches EditPath.</summary>
public sealed class AuditionHandoffService(WorkflowSettings settings, string? applicationDirectory = null)
{
    public async Task<AuditionHandoff> CreateAsync(string source, string directory, CancellationToken token)
        => await PrepareAsync(source, directory, null, token);

    public async Task<AuditionHandoff> CreateLocalAsync(string source, string directory,
        double startSeconds, double endSeconds, CancellationToken token)
        => await PrepareAsync(source, directory, (startSeconds, endSeconds), token);

    public async Task<AuditionHandoff> CreateLocalFromValidatedAsync(AudioRevisionSource baseline, string directory,
        double startSeconds, double endSeconds, CancellationToken token)
    {
        if (!Path.GetExtension(baseline.Path).Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
            !baseline.Matches(baseline.Path, baseline.Sha256))
            throw new InvalidDataException("Validated AU baseline must be an active PCM WAV source.");
        var folder = Path.Combine(directory, "audition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return await PrepareFromBaselineAsync(baseline.Path, baseline.Sha256, folder,
            (startSeconds, endSeconds), token);
    }

    private async Task<AuditionHandoff> PrepareAsync(string source, string directory,
        (double Start, double End)? range, CancellationToken token)
    {
        var folder = Path.Combine(directory, "audition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var before = Path.Combine(folder, "before.wav");
        if (Path.GetExtension(source).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            await new FfmpegService(settings, applicationDirectory).DecodeAsync(source, before, token);
        else
            await CopyAsync(source, before, FileShare.Read, token);
        var beforeHash = AudioFileWatcher.Fingerprint(before).Hash;
        return await PrepareFromBaselineAsync(before, beforeHash, folder, range, token);
    }

    private static async Task<AuditionHandoff> PrepareFromBaselineAsync(string before, string beforeHash,
        string folder, (double Start, double End)? range, CancellationToken token)
    {
        var info = WaveInfo.Read(before);
        var edit = Path.Combine(folder, "repair_WORK.wav");
        var region = range.HasValue ? LocalAudioRepair.Select(info, range.Value.Start, range.Value.End) : null;
        if (region != null)
        {
            var localStart = (double)region.LocalStartSample / info.SampleRate;
            var localEnd = (double)(region.EndSample - region.ClipStartSample) / info.SampleRate;
            edit = Path.Combine(folder, FormattableString.Invariant($"repair_{localStart:F3}-{localEnd:F3}s_WORK.wav"));
            await LocalAudioRepair.ExtractAsync(before, edit, region, token);
        }
        else await CopyAsync(before, edit, FileShare.Read, token);
        var result = new AuditionHandoff(before, edit, beforeHash, info, region);
        JsonStore.Write(Path.Combine(folder, "handoff.json"), result);
        return result;
    }

    public async Task<string> CaptureReturnAsync(AuditionHandoff handoff, CancellationToken token)
    {
        using var baseline = await AudioRevisionSource.OpenAsync(handoff.BeforePath, handoff.BeforeHash, token);
        using var result = await CaptureVerifiedReturnAsync(handoff, baseline, token);
        return result.Path;
    }

    public async Task<AuditionReturnResult> CaptureVerifiedReturnAsync(AuditionHandoff handoff,
        AudioRevisionSource baseline, CancellationToken token)
    {
        try
        {
            var repaired = await CaptureStableReturnAsync(handoff, token);
            repaired = await AuditionFormatNormalizer.NormalizeVerifiedAsync(handoff, repaired, baseline, token);
            if (handoff.Region == null)
            {
                var validation = await AudioRevisionSource.OpenAndHashAsync(repaired, token);
                return new AuditionReturnResult(validation.Path, validation.Sha256, validation);
            }
            using var merged = await LocalAudioRepair.MergeVerifiedAsync(handoff, repaired, baseline, token);
            return new AuditionReturnResult(merged.Path, merged.Sha256, merged.DetachValidation());
        }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
        {
            throw new IOException("AU 仍獨佔修音檔，尚未接回。請先在 AU 儲存並關閉此音檔分頁（不用退出 AU），再按「完成修音，繼續聽校」。原音訊、字幕及修音副本均保留。", e);
        }
    }

    private static async Task<string> CaptureStableReturnAsync(AuditionHandoff handoff, CancellationToken token)
    {
        var target = Path.Combine(Path.GetDirectoryName(handoff.EditPath)!, "repaired-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            // If AU has finished its save, this read lease excludes current and future writers.
            // A single snapshot is then conclusive and the 500 ms quiet-period probe is unnecessary.
            await using var stable = new FileStream(handoff.EditPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous);
            await stable.CopyToAsync(output, token);
            await output.FlushAsync(token);
            return target;
        }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
        {
            // Audition commonly keeps the tab writable. Preserve the original conservative
            // replace-aware snapshot comparison as the compatibility fallback.
        }
        // AU may save by replacing its file. Open afresh for each observation and allow replacement.
        // Compare the snapshot and two separate source reads; never import a partially written save.
        var initial = Stamp(handoff.EditPath);
        await CopyAsync(handoff.EditPath, target, FileShare.ReadWrite | FileShare.Delete, token);
        var hash = await HashSharedAsync(target, token);
        var first = await HashSharedAsync(handoff.EditPath, token);
        await Task.Delay(500, token);
        var second = await HashSharedAsync(handoff.EditPath, token);
        if (hash != first || hash != second || initial != Stamp(handoff.EditPath))
            throw new IOException("AU 仍在儲存。請等儲存完成（必要時關閉 AU 中此音檔）後，再按「完成修音，繼續聽校」。修音檔仍保留。");
        return target;
    }

    private static (long, DateTime) Stamp(string file)
    {
        var info = new FileInfo(file);
        return (info.Length, info.LastWriteTimeUtc);
    }

    private static async Task<string> HashSharedAsync(string file, CancellationToken token)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await AudioContentHash.ComputeAsync(stream, token).ConfigureAwait(false);
    }

    private static async Task CopyAsync(string source, string target, FileShare share, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, share, 131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            131072, FileOptions.Asynchronous);
        await input.CopyToAsync(output, token);
    }
}
