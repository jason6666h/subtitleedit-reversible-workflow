using System.Security.Cryptography;

namespace AudioWorkflow;

/// <summary>
/// Creates immutable-by-convention edit versions. Never modifies an input, and never commits host state.
/// Unknown WAV metadata is deliberately omitted: cue/loop offsets would be stale after a cut.
/// </summary>
public sealed partial class AudioTimelineService
{
    private readonly FfmpegService _ffmpeg;
    public AudioTimelineService(FfmpegService ffmpeg) => _ffmpeg = ffmpeg ?? throw new ArgumentNullException(nameof(ffmpeg));
    public AudioTimelineService(WorkflowSettings settings, string? applicationDirectory = null)
        : this(new FfmpegService(settings, applicationDirectory)) { }

    /// <summary>
    /// Removes [start,end), rounding both boundaries to nearest sample (ties away from zero).
    /// Allows at most half a sample outside the timeline for UI rounding, then clamps.
    /// Empty and whole-file removal are rejected. A decoded MP3 baseline is a new unique WAV.
    /// Cancellation/failure never returns a result; no source or earlier version is deleted.
    /// </summary>
    public async Task<AudioCutResult> CutAsync(string sourcePath, string outputDirectory,
        double startSeconds, double endSeconds, CancellationToken token)
        => (await CutCoreAsync(sourcePath, outputDirectory, startSeconds, endSeconds, null, token)
            .ConfigureAwait(false)).Cut;

    /// <summary>
    /// Cuts a source already pinned by the host and returns the fully verified output with its
    /// read-only validation handle. This removes redundant whole-file source and output hashes
    /// without weakening the lifetime lock used by the host transaction.
    /// </summary>
    public async Task<ValidatedAudioCutResult> CutValidatedAsync(AudioRevisionSource source,
        string outputDirectory, double startSeconds, double endSeconds, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Matches(source.Path, source.Sha256))
            throw new InvalidDataException("Validated cut source is no longer active.");
        var prepared = await CutCoreAsync(source.Path, outputDirectory, startSeconds, endSeconds, source, token)
            .ConfigureAwait(false);
        return new ValidatedAudioCutResult(prepared.Cut, prepared.Validation ??
            throw new InvalidDataException("Validated cut did not retain its output guard."));
    }

    private async Task<(AudioCutResult Cut, AudioRevisionSource? Validation)> CutCoreAsync(
        string sourcePath, string outputDirectory, double startSeconds, double endSeconds,
        AudioRevisionSource? validatedSource, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!double.IsFinite(startSeconds) || !double.IsFinite(endSeconds) || endSeconds <= startSeconds)
            throw new ArgumentOutOfRangeException(nameof(endSeconds), "A finite, increasing interval is required.");
        var source = Path.GetFullPath(sourcePath);
        var directory = Path.GetFullPath(outputDirectory);
        using var input = validatedSource?.OpenPinnedRead() ?? OpenRead(source);
        var sourceLength = input.Length;
        var sourceHash = validatedSource?.Sha256 ?? await HashAsync(input, token).ConfigureAwait(false);
        var baseline = source;
        FileStream? decoded = null;
        FileStream? verifiedOutput = null;
        AudioRevisionSource? outputValidation = null;
        string? staging = null;
        string? published = null;
        try
        {
            if (Path.GetExtension(source).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(directory);
                // FFmpeg uses -n; never reuse or overwrite a cache entry, even for identical inputs.
                // A failed decode can leave its uniquely named staging file for diagnostics; it is
                // not published as a baseline and is never adopted by a subsequent operation.
                var decodePath = Path.Combine(directory, "decode-" + Guid.NewGuid().ToString("N") + ".partial.wav");
                await _ffmpeg.DecodeAsync(source, decodePath, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                using (var check = OpenRead(decodePath)) PcmWaveLayout.Read(check);
                baseline = Path.Combine(directory, "baseline-" + Guid.NewGuid().ToString("N") + ".wav");
                File.Move(decodePath, baseline, false);
                decoded = OpenRead(baseline);
            }
            var pcm = decoded ?? input;
            var layout = PcmWaveLayout.Read(pcm);
            var wave = layout.Info;
            var start = Boundary(startSeconds, wave);
            var end = Boundary(endSeconds, wave);
            if (end <= start || end - start == wave.SampleCount)
                throw new ArgumentOutOfRangeException(nameof(endSeconds), "A cut must remove at least one sample and leave at least one sample.");
            var baselineLength = pcm.Length;
            var baselineHash = decoded == null ? sourceHash : await HashAsync(pcm, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            var candidate = Path.Combine(directory, "cut-" + Guid.NewGuid().ToString("N") + ".partial.wav");
            string retainedHash;
            // Record ownership only after CreateNew succeeds; a collision must not be cleaned up.
            using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                staging = candidate;
                layout.WriteHeader(output, wave.SampleCount - (end - start));
                using var retained = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await CopyAsync(pcm, output, layout.DataOffset, checked(start * layout.BlockAlign), retained, token).ConfigureAwait(false);
                await CopyAsync(pcm, output, checked(layout.DataOffset + end * layout.BlockAlign),
                    checked((wave.SampleCount - end) * layout.BlockAlign), retained, token).ConfigureAwait(false);
                if ((((wave.SampleCount - (end - start)) * layout.BlockAlign) & 1) != 0)
                    await output.WriteAsync(new byte[1], token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
                retainedHash = Convert.ToHexString(retained.GetHashAndReset());
            }
            string outputHash;
            long outputLength;
            // Permit only rename while this verified handle remains open. A validated host source
            // is already pinned against writes/replacement, so re-hashing that complete source
            // before commit would add two redundant full-file passes.
            verifiedOutput = new FileStream(candidate, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 131072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = PcmWaveLayout.Read(verifiedOutput);
            if (actual.Info != wave with { SampleCount = wave.SampleCount - (end - start) } ||
                !actual.Format.AsSpan().SequenceEqual(layout.Format))
                throw new InvalidDataException("Cut output format or sample count failed validation.");
            var hashes = await AudioContentHash.ComputeWithRangeAsync(verifiedOutput,
                actual.DataOffset, actual.DataLength, token).ConfigureAwait(false);
            if (hashes.RangeHash != retainedHash)
                throw new InvalidDataException("Cut output PCM failed byte validation.");
            outputHash = hashes.FileHash;
            outputLength = verifiedOutput.Length;
            if (validatedSource == null)
            {
                if (input.Length != sourceLength || await HashAsync(input, token).ConfigureAwait(false) != sourceHash ||
                    (decoded != null && await HashAsync(decoded, token).ConfigureAwait(false) != baselineHash))
                    throw new IOException("Audio source changed during preparation; discard this edit.");
            }
            else if (!validatedSource.Matches(source, sourceHash))
                throw new IOException("Validated audio source was released during preparation; discard this edit.");
            token.ThrowIfCancellationRequested();
            var destination = Path.Combine(directory, "cut-" + Guid.NewGuid().ToString("N") + ".wav");
            File.Move(staging, destination, false);
            staging = null;
            published = destination;
            if (validatedSource != null)
            {
                FileStream? pinned = null;
                try
                {
                    pinned = OpenRead(destination);
                    verifiedOutput.Dispose();
                    verifiedOutput = null;
                    outputValidation = AudioRevisionSource.AdoptValidated(destination, outputHash, pinned);
                    pinned = null;
                }
                finally { pinned?.Dispose(); }
            }
            else
            {
                verifiedOutput.Dispose();
                verifiedOutput = null;
            }
            token.ThrowIfCancellationRequested();
            var cut = new AudioCutResult(source, baseline, destination, sourceHash, baselineHash, outputHash,
                sourceLength, baselineLength, outputLength, start, end, wave);
            var retainedValidation = outputValidation;
            outputValidation = null;
            return (cut, retainedValidation);
        }
        catch
        {
            // Only exact paths this invocation created. Never remove a baseline or an older edit.
            outputValidation?.Dispose();
            verifiedOutput?.Dispose();
            verifiedOutput = null;
            TryDeleteOwned(staging);
            TryDeleteOwned(published);
            throw;
        }
        finally
        {
            verifiedOutput?.Dispose();
            decoded?.Dispose();
        }
    }

    /// <summary>
    /// Revalidates prepared content before commit; throws IOException/InvalidDataException on change.
    /// The host must also check its media/subtitle revision and serialize commit with its own edits.
    /// This is a snapshot validation, not a lock spanning the caller's subsequent commit.
    /// </summary>
    public async Task ValidateAsync(AudioCutResult result, CancellationToken token)
    {
        using var lease = await AcquireValidationAsync(result, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates and keeps source, baseline and output open with FileShare.Read until disposed.
    /// Hold through media loading and the host's atomic audio/subtitle/history commit. On Windows
    /// this denies writers/deletion/rename; other platforms may require additional OS locking.
    /// Does not lock host state: independently check its revision after every asynchronous gap.
    /// </summary>
    public async Task<AudioValidationLease> AcquireValidationAsync(AudioCutResult result, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(result);
        token.ThrowIfCancellationRequested();
        var lease = new AudioValidationLease();
        try
        {
            var source = lease.Add(OpenRead(result.SourcePath));
            var baseline = result.BaselinePath == result.SourcePath ? source : lease.Add(OpenRead(result.BaselinePath));
            var output = lease.Add(OpenRead(result.OutputPath));
            var sourceHash = await HashAsync(source, token).ConfigureAwait(false);
            var baselineHash = baseline == source ? sourceHash : await HashAsync(baseline, token).ConfigureAwait(false);
            if (source.Length != result.SourceLength || baseline.Length != result.BaselineLength || output.Length != result.OutputLength ||
                sourceHash != result.SourceSha256 || baselineHash != result.BaselineSha256 ||
                await HashAsync(output, token).ConfigureAwait(false) != result.OutputSha256)
                throw new IOException("Prepared audio content changed; do not commit.");
            var original = PcmWaveLayout.Read(baseline);
            var cut = PcmWaveLayout.Read(output);
            var expected = new WaveInfo(result.SampleRate, result.Channels, result.BitsPerSample, result.OriginalSamples);
            if (original.Info != expected || cut.Info != expected with { SampleCount = result.OutputSamples })
                throw new InvalidDataException("Prepared audio timeline failed validation.");
            token.ThrowIfCancellationRequested();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    /// <summary>Use for undo/redo/session media: validate the same open file handle kept until commit.</summary>
    public static async Task<AudioValidationLease> AcquireFileValidationAsync(string path, string expectedSha256, CancellationToken token)
    {
        ValidateSha256(expectedSha256);
        token.ThrowIfCancellationRequested();
        var lease = new AudioValidationLease();
        try
        {
            var source = lease.Add(OpenRead(Path.GetFullPath(path)));
            if (!string.Equals(await HashAsync(source, token).ConfigureAwait(false), expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Audio content changed; do not commit.");
            token.ThrowIfCancellationRequested();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static void ValidateSha256(string hash)
    {
        if (hash == null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new ArgumentException("An expected SHA-256 identity is required.", nameof(hash));
    }

    private static long Boundary(double seconds, WaveInfo wave)
    {
        var sample = seconds * wave.SampleRate;
        // Tiny epsilon covers floating point representation only, not a perceptible selection overrun.
        const double epsilon = 1e-6;
        if (!double.IsFinite(sample) || sample < -0.5 - epsilon || sample > wave.SampleCount + 0.5 + epsilon)
            throw new ArgumentOutOfRangeException(nameof(seconds), "Selection is outside the audio timeline.");
        return checked((long)Math.Clamp(Math.Round(sample, MidpointRounding.AwayFromZero), 0, wave.SampleCount));
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<string> HashAsync(Stream stream, CancellationToken token)
    {
        stream.Position = 0;
        return await AudioContentHash.ComputeAsync(stream, token).ConfigureAwait(false);
    }

    private static async Task CopyAsync(Stream source, Stream target, long offset, long count,
        IncrementalHash hash, CancellationToken token)
    {
        source.Position = offset;
        var buffer = new byte[131072];
        while (count > 0)
        {
            token.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("PCM data ended unexpectedly.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            count -= read;
        }
    }

    private static void TryDeleteOwned(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); }
        catch (IOException) { /* An uncommitted orphan is safer than touching any other file. */ }
        catch (UnauthorizedAccessException) { }
    }
}
