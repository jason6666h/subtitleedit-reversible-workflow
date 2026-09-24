namespace Nikse.SubtitleEdit.Logic.UndoRedo;

/// <summary>
/// Immutable media revision associated with a subtitle undo snapshot. Position is a
/// restore hint, not an edit identity. An empty RevisionId denotes the session baseline.
/// </summary>
public sealed record SynchronousAudioState(
    string FileName,
    string Sha256,
    double PositionSeconds,
    string RevisionId,
    string SessionId,
    string? TimelineMapJson = null,
    int TimelineSampleRate = 0,
    string? AudioEditRegionsJson = null,
    long TimelineOriginalFrames = 0,
    string? RenderBaselineFileName = null,
    string? RenderBaselineSha256 = null,
    string? RenderBaselineTimelineMapJson = null,
    string? AudioRepairPatchesJson = null);
