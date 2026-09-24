namespace AudioWorkflow;

/// <summary>All strings are snapshots captured by the host at one media/subtitle revision.</summary>
public sealed record AudioExportRequest(string SourcePath, string OutputDirectory, string SubtitleText,
    string SubtitleExtension, string ExpectedSourceSha256, string? HostCheckpointJson = null, string BaseName = "FINAL",
    SubtitleInterval[]? SubtitleTimeline = null, long? ExpectedSampleCount = null, int? ExpectedSampleRate = null);

public sealed record SubtitleInterval(double StartSeconds, double EndSeconds);

/// <summary>An already published pair. Cancellation after the directory rename does not undo publication.</summary>
public sealed class AudioExportResult
{
    public string DirectoryPath { get; }
    public string AudioPath { get; }
    public string SubtitlePath { get; }
    public string CheckpointPath => Path.Combine(DirectoryPath, "checkpoint.json");
    public string ReportPath => Path.Combine(DirectoryPath, "report.json");
    public string SourceSha256 { get; }
    public string AudioSha256 { get; }
    public string SubtitleSha256 { get; }
    public int SampleRate { get; }
    public long SampleCount { get; }
    public double DurationSeconds => (double)SampleCount / SampleRate;
    internal AudioExportResult(string directory, string audioName, string subtitleName,
        string sourceHash, string audioHash, string subtitleHash, WaveInfo wave)
    {
        DirectoryPath = directory; AudioPath = Path.Combine(directory, audioName); SubtitlePath = Path.Combine(directory, subtitleName);
        SourceSha256 = sourceHash; AudioSha256 = audioHash; SubtitleSha256 = subtitleHash;
        SampleRate = wave.SampleRate; SampleCount = wave.SampleCount;
    }
}
