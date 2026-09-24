using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioWorkflow;

public sealed partial class AudioTimelineService
{
    /// <summary>
    /// Loads the schema-1 portable pair created by ExportPairAsync, not an arbitrary host session.
    /// Original source need not exist. Validates hashes/lengths/PCM timeline and returns file guards
    /// for host restore. Refuses .pending directories and path traversal. JSON/text limit: 64 MiB each.
    /// Hashes detect mismatch/corruption, not a malicious rewrite of the entire unsigned bundle.
    /// </summary>
    public async Task<AudioExportSession> LoadExportPairAsync(string directoryPath, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        token.ThrowIfCancellationRequested();
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        if (Path.GetFileName(directory).EndsWith(".pending", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An uncommitted export cannot be loaded.");
        var lease = new AudioValidationLease();
        try
        {
            var checkpoint = lease.Add(OpenRead(Path.Combine(directory, "checkpoint.json")));
            var report = lease.Add(OpenRead(Path.Combine(directory, "report.json")));
            var checkpointBytes = await ReadSnapshotBytesAsync(checkpoint, token).ConfigureAwait(false);
            var reportBytes = await ReadSnapshotBytesAsync(report, token).ConfigureAwait(false);
            using var checkpointJson = JsonDocument.Parse(checkpointBytes);
            using var reportJson = JsonDocument.Parse(reportBytes);
            var cp = checkpointJson.RootElement;
            var rp = reportJson.RootElement;
            if (cp.GetProperty("SchemaVersion").GetInt32() != 1 || cp.GetProperty("Kind").GetString() != "audio-workflow-pair" ||
                rp.GetProperty("SchemaVersion").GetInt32() != 1 || rp.GetProperty("Kind").GetString() != "audio-workflow-export-report" ||
                rp.GetProperty("CheckpointFile").GetString() != "checkpoint.json" ||
                cp.GetProperty("Id").GetString() != rp.GetProperty("Id").GetString() ||
                rp.GetProperty("CheckpointSha256").GetString() != Convert.ToHexString(SHA256.HashData(checkpointBytes)))
                throw new InvalidDataException("Unsupported or inconsistent export checkpoint/report.");
            var audioName = cp.GetProperty("AudioFile").GetString()!;
            var subtitleName = cp.GetProperty("SubtitleFile").GetString()!;
            ValidateExportName(audioName, 100); ValidateExportName(subtitleName, 100);
            if (!audioName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                new[] { audioName, subtitleName, "checkpoint.json", "report.json" }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
                throw new InvalidDataException("Invalid pair filenames.");
            var audioHash = cp.GetProperty("AudioSha256").GetString()!;
            var subtitleHash = cp.GetProperty("SubtitleSha256").GetString()!;
            var sourceHash = cp.GetProperty("SourceSha256").GetString()!;
            ValidateSha256(audioHash); ValidateSha256(subtitleHash); ValidateSha256(sourceHash);
            if (rp.GetProperty("AudioFile").GetString() != audioName || rp.GetProperty("SubtitleFile").GetString() != subtitleName ||
                rp.GetProperty("AudioSha256").GetString() != audioHash || rp.GetProperty("SubtitleSha256").GetString() != subtitleHash ||
                rp.GetProperty("SourceSha256").GetString() != sourceHash)
                throw new InvalidDataException("Report does not describe this pair.");
            var audio = lease.Add(OpenRead(Path.Combine(directory, audioName)));
            var subtitle = lease.Add(OpenRead(Path.Combine(directory, subtitleName)));
            if (audio.Length != cp.GetProperty("AudioLength").GetInt64() || subtitle.Length != cp.GetProperty("SubtitleLength").GetInt64() ||
                await HashAsync(audio, token).ConfigureAwait(false) != audioHash)
                throw new InvalidDataException("Export audio/length validation failed.");
            var subtitleBytes = await ReadSnapshotBytesAsync(subtitle, token).ConfigureAwait(false);
            if (Convert.ToHexString(SHA256.HashData(subtitleBytes)) != subtitleHash)
                throw new InvalidDataException("Export subtitles changed.");
            var wave = PcmWaveLayout.Read(audio).Info;
            if (wave.SampleCount != cp.GetProperty("SampleCount").GetInt64() || wave.SampleRate != cp.GetProperty("SampleRate").GetInt32() ||
                wave.Channels != cp.GetProperty("Channels").GetInt32() || wave.BitsPerSample != cp.GetProperty("BitsPerSample").GetInt32() ||
                wave.DurationSeconds != cp.GetProperty("DurationSeconds").GetDouble() ||
                wave.SampleCount != rp.GetProperty("SampleCount").GetInt64() || wave.SampleRate != rp.GetProperty("SampleRate").GetInt32() ||
                wave.DurationSeconds != rp.GetProperty("DurationSeconds").GetDouble())
                throw new InvalidDataException("Export timeline does not match the checkpoint.");
            var host = cp.GetProperty("HostCheckpoint");
            var native = host.ValueKind == JsonValueKind.Null ? null : host.GetRawText();
            var text = new UTF8Encoding(false, true).GetString(subtitleBytes);
            var result = new AudioExportResult(directory, audioName, subtitleName, sourceHash, audioHash, subtitleHash, wave);
            token.ThrowIfCancellationRequested();
            return new(result, text, native, lease);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
        {
            lease.Dispose();
            throw new InvalidDataException("Invalid export session metadata.", e);
        }
        catch { lease.Dispose(); throw; }
    }

    private static async Task<byte[]> ReadSnapshotBytesAsync(Stream stream, CancellationToken token)
    {
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("Session JSON/subtitle snapshot exceeds 64 MiB.");
        stream.Position = 0;
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }
}
