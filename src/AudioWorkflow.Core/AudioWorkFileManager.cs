namespace AudioWorkflow;

public enum ExistingWorkChoice { UseExisting, Rebuild, Cancel }
public sealed class AudioWorkFileManager(FfmpegService ffmpeg, ProjectManager projects)
{
    // Caller holds ProjectManager.Acquire for the complete session.
    public async Task<WaveInfo?> CreateAsync(WorkflowProject project, ExistingWorkChoice choice, bool rebuildConfirmed, CancellationToken token)
    {
        SafePath.ValidateWork(project);
        if (choice == ExistingWorkChoice.Cancel) return null;
        var exists = File.Exists(project.WorkPath);
        if (exists && choice == ExistingWorkChoice.UseExisting)
        {
            var existing = WaveInfo.Read(project.WorkPath);
            projects.Save(project);
            return existing;
        }
        if (exists && !rebuildConfirmed) throw new InvalidOperationException("重新建立須再次確認，可能覆蓋已修音內容。");
        using var sourceGuard = new FileStream(project.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileStream? oldGuard = exists ? new FileStream(project.WorkPath, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var temp = project.WorkPath + "." + Guid.NewGuid().ToString("N") + ".tmp.wav";
        try
        {
            var wave = await ffmpeg.DecodeAsync(project.SourcePath, temp, token);
            token.ThrowIfCancellationRequested();
            oldGuard?.Dispose(); oldGuard = null;
            if (exists)
            {
                var backup = project.WorkPath + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".wav";
                File.Replace(temp, project.WorkPath, backup);
            }
            else File.Move(temp, project.WorkPath, false);
            projects.Save(project);
            return wave;
        }
        finally
        {
            oldGuard?.Dispose();
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
