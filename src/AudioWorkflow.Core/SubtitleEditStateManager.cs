namespace AudioWorkflow;

public sealed record EditSession(string ProjectId, string AudioFilePath, string SubtitleFilePath, string InvocationId, int[] SelectedIndices, double? PositionSeconds, WaveInfo Baseline);
public sealed class SubtitleEditStateManager
{
    public EditSession Capture(WorkflowProject project, PluginRequest request, WaveInfo baseline) => new(project.ProjectId, project.WorkPath, request.Subtitle.FileName, request.MediaContextId ?? Guid.NewGuid().ToString("N"), [..request.SelectedIndices], request.VideoPositionSeconds, baseline);
    public static double? ReturnPosition(EditSession session, WorkflowSettings settings)
    {
        if (!settings.AutoReturnPosition || session.PositionSeconds is not { } value || !double.IsFinite(value) || value < 0) return null;
        return Math.Max(0, value - settings.PreRollSeconds);
    }
}
