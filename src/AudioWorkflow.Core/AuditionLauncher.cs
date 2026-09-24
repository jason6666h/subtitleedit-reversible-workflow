using System.Diagnostics;

namespace AudioWorkflow;

public sealed class AuditionLauncher
{
    public void Open(WorkflowProject project, string auditionPath)
    {
        SafePath.ValidateWork(project);
        OpenFile(project.WorkPath, auditionPath);
    }

    public void OpenFile(string workPath, string auditionPath)
    {
        if (!Path.IsPathFullyQualified(workPath)) throw new IOException("Audition 工作檔必須是完整路徑。");
        _ = WaveInfo.Read(workPath);
        if (!Path.IsPathFullyQualified(auditionPath) || !File.Exists(auditionPath) || !auditionPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Adobe Audition 路徑無效，請在設定中選擇已安裝的 Adobe Audition.exe。", auditionPath);
        var start = new ProcessStartInfo(auditionPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(auditionPath)! };
        start.ArgumentList.Add(workPath);
        using var process = Process.Start(start) ?? throw new IOException("Adobe Audition 未能啟動。");
    }
}
