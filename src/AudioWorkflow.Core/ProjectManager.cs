namespace AudioWorkflow;

public sealed class WorkflowProject
{
    public int SchemaVersion { get; set; } = 1;
    public string ProjectId { get; set; } = Guid.NewGuid().ToString("N");
    public string SourcePath { get; set; } = "";
    public string WorkPath { get; set; } = "";
    public string RootPath { get; set; } = "";
    public string SubtitleFilePath { get; set; } = "";
    public string ManifestPath => WorkPath + ".audio-workflow.json";
}
public static class SafePath
{
    public static bool Equal(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public static void NoLinks(string path)
    {
        var current = Path.GetFullPath(path);
        while (current != null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("安全限制：不接受符號連結／junction 路徑：" + current);
            current = Path.GetDirectoryName(current);
        }
    }
    public static void ValidateWork(WorkflowProject project)
    {
        if (project.SchemaVersion != 1 || !Guid.TryParse(project.ProjectId, out _)) throw new InvalidDataException("無效專案 manifest。");
        if (!Path.IsPathFullyQualified(project.SourcePath) || !Path.IsPathFullyQualified(project.WorkPath) || !Path.IsPathFullyQualified(project.RootPath)) throw new InvalidDataException("專案路徑必須為絕對路徑。");
        if (!project.SourcePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || !project.WorkPath.EndsWith("_WORK.wav", StringComparison.OrdinalIgnoreCase) || Equal(project.SourcePath, project.WorkPath))
            throw new InvalidDataException("來源必須為 MP3，工作檔必須為獨立的 *_WORK.wav。");
        if (!Equal(Path.GetDirectoryName(project.WorkPath)!, Path.Combine(project.RootPath, "Working"))) throw new InvalidDataException("工作音檔不在專案 Working 目錄。");
        NoLinks(project.SourcePath); NoLinks(project.WorkPath); NoLinks(project.ManifestPath);
    }
}
public sealed class ProjectManager
{
    private const string ManifestSuffix = ".audio-workflow.json";

    public WorkflowProject FromSource(string source, string subtitle)
    {
        source = Path.GetFullPath(source);
        if (!File.Exists(source) || !source.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("請先載入存在的原始 MP3。", source);
        var directory = Path.GetDirectoryName(source)!;
        var root = Path.GetFileName(directory).Equals("Source", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(directory)! : directory;
        var name = GetProjectName(source);
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("無效專案名稱。");
        var project = new WorkflowProject { SourcePath = source, WorkPath = Path.Combine(root, "Working", name + "_WORK.wav"), RootPath = root, SubtitleFilePath = subtitle };
        SafePath.ValidateWork(project);
        if (File.Exists(project.ManifestPath))
        {
            var existing = Load(project.WorkPath);
            if (!SafePath.Equal(existing.SourcePath, source)) throw new IOException("同名工作檔屬於另一個來源 MP3，請使用不同專案資料夾。");
            existing.SubtitleFilePath = subtitle;
            return existing;
        }
        return project;
    }
    public WorkflowProject Load(string work)
    {
        work = Path.GetFullPath(work);
        var manifest = work + ManifestSuffix;
        var project = JsonStore.Read<WorkflowProject>(manifest);
        SafePath.ValidateWork(project);
        if (!SafePath.Equal(work, project.WorkPath))
            project = RelocateMovedProject(work, manifest, project);
        return project;
    }

    /// <summary>
    /// Explicit repair used when a project was reorganized in a way that cannot be
    /// inferred safely. The selected MP3 must map to the selected *_WORK.wav name.
    /// </summary>
    public WorkflowProject Relink(string work, string source, string subtitle)
    {
        work = Path.GetFullPath(work);
        source = Path.GetFullPath(source);
        if (!File.Exists(work) || !work.EndsWith("_WORK.wav", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("請先在 SE 載入存在的 *_WORK.wav。", work);
        if (!File.Exists(source) || !source.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("請選擇存在的原始 MP3。", source);
        var expectedWorkName = GetProjectName(source) + "_WORK.wav";
        if (!Path.GetFileName(work).Equals(expectedWorkName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"選取的 MP3 名稱應對應 {Path.GetFileName(work)}；目前選到 {Path.GetFileName(source)}。");
        var working = Path.GetDirectoryName(work)!;
        if (!Path.GetFileName(working).Equals("Working", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("WORK.wav 必須位於專案的 Working 資料夾。");

        var manifest = work + ManifestSuffix;
        WorkflowProject project;
        try
        {
            project = File.Exists(manifest) ? JsonStore.Read<WorkflowProject>(manifest) : new WorkflowProject();
            if (!Guid.TryParse(project.ProjectId, out _)) project.ProjectId = Guid.NewGuid().ToString("N");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            project = new WorkflowProject();
        }
        project.SchemaVersion = 1;
        project.SourcePath = source;
        project.WorkPath = work;
        project.RootPath = Path.GetDirectoryName(working)!;
        project.SubtitleFilePath = string.IsNullOrWhiteSpace(subtitle) ? string.Empty : Path.GetFullPath(subtitle);
        SafePath.ValidateWork(project);
        BackupManifest(manifest, "before-relink");
        Save(project);
        return project;
    }
    public void Save(WorkflowProject project)
    {
        SafePath.ValidateWork(project);
        Directory.CreateDirectory(Path.Combine(project.RootPath, "Output"));
        JsonStore.Write(project.ManifestPath, project);
    }
    public static FileStream Acquire(WorkflowProject project)
    {
        SafePath.ValidateWork(project);
        Directory.CreateDirectory(Path.GetDirectoryName(project.WorkPath)!);
        var lockPath = project.WorkPath + ".workflow.lock";
        SafePath.NoLinks(lockPath);
        try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("另一個 Audio Workflow 工作階段正在使用此 WORK.wav。請先結束該工作階段。", e); }
    }

    private WorkflowProject RelocateMovedProject(string work, string manifest, WorkflowProject stored)
    {
        if (!File.Exists(work) || !Path.GetFileName(work).Equals(Path.GetFileName(stored.WorkPath), StringComparison.OrdinalIgnoreCase))
            throw RelinkRequired();
        var working = Path.GetDirectoryName(work)!;
        if (!Path.GetFileName(working).Equals("Working", StringComparison.OrdinalIgnoreCase)) throw RelinkRequired();
        var newRoot = Path.GetDirectoryName(working)!;
        if (!SafePath.Equal(Path.GetDirectoryName(stored.WorkPath)!, Path.Combine(stored.RootPath, "Working")))
            throw RelinkRequired();

        var relativeSource = SafeRelativePath(stored.RootPath, stored.SourcePath);
        if (relativeSource == null) throw RelinkRequired();
        var source = Path.GetFullPath(Path.Combine(newRoot, relativeSource));
        if (!IsChildOrSelf(newRoot, source) || !File.Exists(source) ||
            !source.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) throw RelinkRequired();

        var subtitle = stored.SubtitleFilePath;
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var relativeSubtitle = SafeRelativePath(stored.RootPath, subtitle);
            var movedSubtitle = relativeSubtitle == null ? null : Path.GetFullPath(Path.Combine(newRoot, relativeSubtitle));
            if (movedSubtitle != null && IsChildOrSelf(newRoot, movedSubtitle) && File.Exists(movedSubtitle)) subtitle = movedSubtitle;
        }

        stored.SourcePath = source;
        stored.WorkPath = work;
        stored.RootPath = newRoot;
        stored.SubtitleFilePath = subtitle;
        SafePath.ValidateWork(stored);
        BackupManifest(manifest, "before-relocation");
        Save(stored);
        return stored;
    }

    private static string GetProjectName(string source)
    {
        var name = Path.GetFileNameWithoutExtension(source);
        return name.EndsWith("_original", StringComparison.OrdinalIgnoreCase) ? name[..^9] : name;
    }

    private static string? SafeRelativePath(string root, string path)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(path)) return null;
        var relative = Path.GetRelativePath(root, path);
        return Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) ||
               relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? null : relative;
    }

    private static bool IsChildOrSelf(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.Equals(fullRoot, comparison) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static void BackupManifest(string manifest, string reason)
    {
        if (!File.Exists(manifest)) return;
        var backup = manifest + "." + reason + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".bak.json";
        File.Copy(manifest, backup, false);
    }

    private static InvalidDataException RelinkRequired() => new(
        "專案位置已變更且無法安全自動辨識。請在 SE 的「更多」選擇「重新連結 WORK 的來源 MP3…」，或從原始 MP3 建立新的 WORK.wav。");
}
