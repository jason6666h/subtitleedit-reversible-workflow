namespace AudioWorkflow;

/// <summary>A portable pair loaded and guarded for host restore. Dispose only after commit/rollback.</summary>
public sealed class AudioExportSession : IDisposable
{
    private readonly AudioValidationLease _lease;
    public AudioExportResult Result { get; }
    public string SubtitleText { get; }
    /// <summary>Opaque snapshot metadata. Host validates its own schema and must use Result.AudioPath.</summary>
    public string? HostCheckpointJson { get; }
    internal AudioExportSession(AudioExportResult result, string subtitleText, string? hostCheckpointJson, AudioValidationLease lease)
    {
        Result = result; SubtitleText = subtitleText; HostCheckpointJson = hostCheckpointJson; _lease = lease;
    }
    public void Dispose() => _lease.Dispose();
}
