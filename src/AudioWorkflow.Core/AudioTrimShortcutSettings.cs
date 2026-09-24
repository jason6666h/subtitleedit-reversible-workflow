namespace AudioWorkflow;

public sealed class AudioTrimShortcutBinding
{
    public string Id { get; set; } = "";
    public List<string> Keys { get; set; } = [];

    public AudioTrimShortcutBinding() { }

    public AudioTrimShortcutBinding(string id, params string[] keys)
    {
        Id = id;
        Keys = [.. keys];
    }
}

public sealed class AudioTrimShortcutSettings
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<AudioTrimShortcutBinding> Bindings { get; set; } =
    [
        new("toggle-trim"),
        new("delete-selection", "Delete"),
        new("clear-selection", "Escape"),
        new("preview-cut"),
        new("previous-change"),
        new("next-change"),
        new("toggle-comparison"),
        new("audition"),
        new("toggle-timeline"),
    ];

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException("音訊剪修快捷鍵設定版本不相容，未覆蓋設定檔。");

        var expected = new[]
        {
            "toggle-trim", "delete-selection", "clear-selection", "preview-cut",
            "previous-change", "next-change", "toggle-comparison", "audition", "toggle-timeline",
        };

        if (Bindings == null || Bindings.Count != expected.Length)
            throw new InvalidDataException("音訊剪修快捷鍵設定不完整。");

        for (var i = 0; i < expected.Length; i++)
        {
            var binding = Bindings[i];
            if (binding == null || binding.Id != expected[i] || binding.Keys == null)
                throw new InvalidDataException("音訊剪修快捷鍵設定不完整。");
            if (binding.Keys.Any(string.IsNullOrWhiteSpace) || binding.Keys.Any(k => k.Contains('+')))
                throw new InvalidDataException("音訊剪修快捷鍵包含無效按鍵。");
        }
    }

    public AudioTrimShortcutBinding Get(string id) =>
        Bindings.First(b => string.Equals(b.Id, id, StringComparison.Ordinal));
}

public sealed class AudioTrimShortcutSettingsStore
{
    public string FilePath { get; }

    public AudioTrimShortcutSettingsStore(string? directory = null)
    {
        FilePath = Path.Combine(directory ?? PortablePaths.SettingsRoot, "audio-trim-shortcuts.json");
    }

    public AudioTrimShortcutSettings Load()
    {
        var settings = File.Exists(FilePath) ? JsonStore.Read<AudioTrimShortcutSettings>(FilePath) : new();
        settings.Validate();
        return settings;
    }

    public void Save(AudioTrimShortcutSettings settings, bool repairInvalidExisting = false)
    {
        settings.Validate();
        if (File.Exists(FilePath))
        {
            try { _ = Load(); }
            catch when (repairInvalidExisting && ExistingFileCanBeRepaired())
            {
                BackupInvalidExistingFile();
                JsonStore.Write(FilePath, settings);
                return;
            }
        }

        JsonStore.Write(FilePath, settings);
    }

    private bool ExistingFileCanBeRepaired()
    {
        try
        {
            var existing = JsonStore.Read<AudioTrimShortcutSettings>(FilePath);
            return existing.SchemaVersion == AudioTrimShortcutSettings.CurrentSchemaVersion;
        }
        catch
        {
            return true;
        }
    }

    private void BackupInvalidExistingFile()
    {
        var backup = FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") +
                     "-" + Guid.NewGuid().ToString("N") + ".bak";
        File.Copy(FilePath, backup, overwrite: false);
    }
}
