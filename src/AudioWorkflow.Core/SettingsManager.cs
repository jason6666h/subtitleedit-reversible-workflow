namespace AudioWorkflow;

public sealed class WorkflowSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string AuditionPath { get; set; } = "";
    // Empty = automatic bundled tool. Legacy bare names are also interpreted as automatic.
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public double PreRollSeconds { get; set; } = 2;
    public double PostRollSeconds { get; set; } = 0;
    public bool AutoReloadAudio { get; set; } = true;
    public bool AutoReturnPosition { get; set; } = true;
    public bool OpenAuditionOnLaunch { get; set; } = true;
    public string LastWorkingDirectory { get; set; } = "";
    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("設定版本不相容，未覆蓋設定檔。");
        if (!double.IsFinite(PreRollSeconds) || PreRollSeconds < 0 || PreRollSeconds > 60 || !double.IsFinite(PostRollSeconds) || PostRollSeconds < 0 || PostRollSeconds > 60)
            throw new InvalidDataException("Pre/Post roll 必須介於 0–60 秒。");
    }
}
public sealed class SettingsManager
{
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public SettingsManager(string pluginDataDirectory)
    {
        DirectoryPath = string.IsNullOrWhiteSpace(pluginDataDirectory)
            ? PortablePaths.SettingsRoot
            : Path.GetFullPath(pluginDataDirectory);
    }
    public WorkflowSettings Load()
    {
        var settings = File.Exists(FilePath) ? JsonStore.Read<WorkflowSettings>(FilePath) : new();
        settings.Validate();
        return settings;
    }
    public void Save(WorkflowSettings settings) { settings.Validate(); JsonStore.Write(FilePath, settings); }
}
