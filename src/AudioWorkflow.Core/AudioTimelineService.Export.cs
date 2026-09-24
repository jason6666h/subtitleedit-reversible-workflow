using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioWorkflow;

public sealed partial class AudioTimelineService
{
    /// <summary>
    /// Copies/decodes one media revision plus an already serialized subtitle snapshot. Writes all
    /// files with CreateNew in a unique sibling .pending directory, validates, then publishes with
    /// a single same-parent Directory.Move. No existing file/directory is overwritten or deleted.
    /// Incomplete .pending directories are retained for user cleanup; consumers must ignore them.
    /// Atomic visibility relies on local filesystem rename semantics, not a distributed/cloud store.
    /// </summary>
    public async Task<AudioExportResult> ExportPairAsync(AudioExportRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        ArgumentNullException.ThrowIfNull(request.SubtitleText);
        // Freeze bounds before any await; these describe the same host snapshot as SubtitleText.
        var intervals = request.SubtitleTimeline?.ToArray();
        if (intervals?.Any(p => p == null || !double.IsFinite(p.StartSeconds) || !double.IsFinite(p.EndSeconds) ||
            p.StartSeconds < 0 || p.EndSeconds <= p.StartSeconds) == true)
            throw new InvalidDataException("字幕含無效時間或非正長度，未輸出成品。");
        if (request.ExpectedSampleCount is <= 0 || request.ExpectedSampleRate is <= 0)
            throw new InvalidDataException("Invalid expected audio timeline.");
        ValidateSha256(request.ExpectedSourceSha256);
        ValidateExportName(request.BaseName);
        var extension = request.SubtitleExtension;
        if (string.IsNullOrEmpty(extension) || extension.Length > 17 || extension[0] != '.' ||
            extension.Length < 2 || !extension.Skip(1).All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Subtitle extension must be a simple dot-prefixed extension.", nameof(request));
        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Subtitle extension cannot collide with the WAV.", nameof(request));
        var audioName = request.BaseName + ".wav";
        var subtitleName = request.BaseName + extension;
        if (new[] { audioName, subtitleName, "checkpoint.json", "report.json" }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
            throw new ArgumentException("Export filenames collide with checkpoint/report files.", nameof(request));
        // JSON syntax is checked now; the engine treats host state as opaque data, never instructions.
        using var hostCheckpoint = request.HostCheckpointJson == null ? null : JsonDocument.Parse(request.HostCheckpointJson);
        var encoding = new UTF8Encoding(false, true);
        var subtitles = encoding.GetBytes(request.SubtitleText);
        if (subtitles.Length > 64 * 1024 * 1024) throw new ArgumentException("Subtitle snapshot exceeds 64 MiB.", nameof(request));
        var subtitleHash = Convert.ToHexString(SHA256.HashData(subtitles));
        var sourcePath = Path.GetFullPath(request.SourcePath);
        var parent = Path.GetFullPath(request.OutputDirectory);
        using var source = OpenRead(sourcePath);
        var sourceLength = source.Length;
        var sourceHash = await HashAsync(source, token).ConfigureAwait(false);
        if (!sourceHash.Equals(request.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Export source differs from the selected host revision.");
        var isMp3 = Path.GetExtension(sourcePath).Equals(".mp3", StringComparison.OrdinalIgnoreCase);
        var sourceWave = isMp3 ? null : PcmWaveLayout.Read(source);
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(parent);
        var id = Guid.NewGuid().ToString("N");
        var name = request.BaseName + "-pair-" + id;
        var staging = Path.Combine(parent, "." + name + ".pending");
        var destination = Path.Combine(parent, name);
        // Reserve the name cooperatively with CreateNew. Pre-existing directories are never adopted.
        var reservation = Path.Combine(parent, "." + name + ".reserve");
        using var reservationGuard = new FileStream(reservation, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.DeleteOnClose);
        if (Directory.Exists(staging) || File.Exists(staging) || Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Export name is already in use.");
        Directory.CreateDirectory(staging);
        var audioPath = Path.Combine(staging, audioName);
        if (isMp3)
            await _ffmpeg.DecodeAsync(sourcePath, audioPath, token).ConfigureAwait(false);
        else
        {
            source.Position = 0;
            using var output = CreateExportFile(audioPath);
            await source.CopyToAsync(output, 131072, token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(true);
        }
        WaveInfo wave;
        string audioHash;
        long audioLength;
        using (var audio = OpenRead(audioPath))
        {
            wave = PcmWaveLayout.Read(audio).Info;
            // Validate against the actual decoded PCM, including a first export from MP3.
            if ((request.ExpectedSampleCount.HasValue && request.ExpectedSampleCount != wave.SampleCount) ||
                (request.ExpectedSampleRate.HasValue && request.ExpectedSampleRate != wave.SampleRate))
                throw new InvalidDataException("原／修改時間軸映射與成品 PCM 不一致，未發布成品。");
            if (intervals?.Any(p => p.EndSeconds > wave.DurationSeconds + 0.001) == true)
                throw new InvalidDataException("字幕超出實際音訊結尾，未發布成品。");
            if (sourceWave != null && wave != sourceWave.Info) throw new InvalidDataException("Export timeline changed.");
            audioHash = await HashAsync(audio, token).ConfigureAwait(false);
            audioLength = audio.Length;
            if (!isMp3 && (audioHash != sourceHash || audioLength != sourceLength))
                throw new InvalidDataException("Export WAV byte verification failed.");
        }
        if (source.Length != sourceLength || await HashAsync(source, token).ConfigureAwait(false) != sourceHash)
            throw new IOException("Source changed during export.");
        await WriteExportBytesAsync(Path.Combine(staging, subtitleName), subtitles, token).ConfigureAwait(false);
        var manifest = new
        {
            SchemaVersion = 1, Kind = "audio-workflow-pair", Id = id,
            AudioFile = audioName, AudioSha256 = audioHash, AudioLength = audioLength,
            SubtitleFile = subtitleName, SubtitleSha256 = subtitleHash, SubtitleLength = subtitles.LongLength,
            SourcePath = sourcePath, SourceSha256 = sourceHash, SourceLength = sourceLength,
            DecodedFromMp3 = isMp3, wave.SampleRate, wave.Channels, wave.BitsPerSample, wave.SampleCount,
            wave.DurationSeconds, HostCheckpoint = hostCheckpoint?.RootElement.Clone()
        };
        var options = new JsonSerializerOptions { WriteIndented = true };
        var checkpointBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, options);
        if (checkpointBytes.Length > 64 * 1024 * 1024) throw new ArgumentException("Checkpoint snapshot exceeds 64 MiB.", nameof(request));
        await WriteExportBytesAsync(Path.Combine(staging, "checkpoint.json"), checkpointBytes, token).ConfigureAwait(false);
        var reportBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1, Kind = "audio-workflow-export-report", Id = id,
            CreatedUtc = DateTimeOffset.UtcNow, CheckpointFile = "checkpoint.json",
            CheckpointSha256 = Convert.ToHexString(SHA256.HashData(checkpointBytes)),
            AudioFile = audioName, AudioSha256 = audioHash, SubtitleFile = subtitleName, SubtitleSha256 = subtitleHash,
            SourceSha256 = sourceHash, SampleCount = wave.SampleCount, SampleRate = wave.SampleRate,
            DurationSeconds = wave.DurationSeconds, AudioEncoding = "integer PCM WAV", DecodedFromMp3 = isMp3,
            SubtitleTimelineValidated = intervals != null, SubtitleCount = intervals?.Length,
            TimelineMapLengthValidated = request.ExpectedSampleCount.HasValue && request.ExpectedSampleRate.HasValue,
            SemanticAlignmentVerified = false
        }, options);
        await WriteExportBytesAsync(Path.Combine(staging, "report.json"), reportBytes, token).ConfigureAwait(false);
        // Validate all files while denying writes/deletion. Windows can deny directory rename
        // while descendant handles are open, so close these immediately before the rename.
        // The randomized staging name and caller-controlled parent must not be exposed to an
        // untrusted process that can replace directory entries in this short publication window.
        // No pathname is accepted from opaque host JSON.
        var guards = new List<FileStream>();
        try
        {
            foreach (var (file, expected) in new[] {
                (audioName, audioHash), (subtitleName, subtitleHash),
                ("checkpoint.json", Convert.ToHexString(SHA256.HashData(checkpointBytes))),
                ("report.json", Convert.ToHexString(SHA256.HashData(reportBytes))) })
            {
                var guard = new FileStream(Path.Combine(staging, file), FileMode.Open, FileAccess.Read,
                    FileShare.Read, 131072, FileOptions.Asynchronous);
                guards.Add(guard);
                if (await HashAsync(guard, token).ConfigureAwait(false) != expected)
                    throw new InvalidDataException("Staged export changed before publication.");
            }
            token.ThrowIfCancellationRequested();
            foreach (var guard in guards) guard.Dispose();
            guards.Clear();
            Directory.Move(staging, destination);
            // Rename is the commit point. Do not observe late cancellation or remove this pair.
            return new(destination, audioName, subtitleName, sourceHash, audioHash, subtitleHash, wave);
        }
        finally { foreach (var guard in guards) guard.Dispose(); }
    }

    private static void ValidateExportName(string name, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > maxLength || name is "." or ".." ||
            name.EndsWith(' ') || name.EndsWith('.') || name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)))
            throw new ArgumentException("Export basename must be a safe single filename component.", nameof(name));
        var stem = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[3])))
            throw new ArgumentException("Reserved device filename.", nameof(name));
    }

    private static FileStream CreateExportFile(string path) => new(path, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task WriteExportBytesAsync(string path, byte[] bytes, CancellationToken token)
    {
        using var output = CreateExportFile(path);
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(true);
    }
}
