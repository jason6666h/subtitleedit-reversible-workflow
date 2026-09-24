namespace AudioWorkflow;

public sealed class PlaybackShortcutPreset
{
    public string Id { get; set; } = "";
    public double Value { get; set; }
    public List<string> Keys { get; set; } = [];

    public PlaybackShortcutPreset() { }
    public PlaybackShortcutPreset(string id, double value, params string[] keys)
    {
        Id = id;
        Value = value;
        Keys = [.. keys];
    }
}

public sealed class PlaybackControlSettings
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<PlaybackShortcutPreset> Presets { get; set; } =
    [
        new("speed-1", 1.2, "Ctrl", "Shift", "D1"),
        new("speed-2", 2.0, "Ctrl", "Shift", "D2"),
        new("volume-1", 50, "Ctrl", "Shift", "D3"),
        new("volume-2", 85, "Ctrl", "Shift", "D4"),
    ];
    public List<string> BalanceKeys { get; set; } = ["Ctrl", "Shift", "D5"];
    public bool BalanceEnabled { get; set; }
    public string BalanceStrength { get; set; } = "medium";
    public bool FocusSubtitleGridAfterTextBoxSplit { get; set; } = true;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException("播放控制設定版本不相容，未覆蓋設定檔。");
        if (Presets == null || Presets.Count != 4 || BalanceKeys == null ||
            BalanceStrength is not ("soft" or "medium" or "strong"))
            throw new InvalidDataException("播放控制設定不完整。");
        var expected = new[] { "speed-1", "speed-2", "volume-1", "volume-2" };
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < expected.Length; i++)
        {
            var preset = Presets[i];
            if (preset == null || preset.Id != expected[i] || preset.Keys == null ||
                !double.IsFinite(preset.Value) ||
                (i < 2 && (preset.Value < .25 || preset.Value > 4)) ||
                (i >= 2 && (preset.Value < 0 || preset.Value > 130)))
                throw new InvalidDataException("播放速度須介於 0.25–4 倍，音量須介於 0–130%。");
            CheckKeys(preset.Keys);
        }
        CheckKeys(BalanceKeys);
        return;

        void CheckKeys(List<string> keys)
        {
            if (keys.Any(string.IsNullOrWhiteSpace) || keys.Any(k => k.Contains('+')))
                throw new InvalidDataException("無效的快捷鍵。");
            if (keys.Count == 0) return;
            var canonical = string.Join("+", keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            if (!usedKeys.Add(canonical))
                throw new InvalidDataException("播放控制快捷鍵重複。");
        }
    }
}

public sealed class PlaybackControlSettingsStore
{
    public string FilePath { get; }

    public PlaybackControlSettingsStore(string? directory = null)
    {
        FilePath = Path.Combine(directory ?? PortablePaths.SettingsRoot, "playback-controls.json");
    }

    public PlaybackControlSettings Load()
    {
        var settings = File.Exists(FilePath) ? JsonStore.Read<PlaybackControlSettings>(FilePath) : new();
        settings.Validate();
        return settings;
    }

    public void Save(PlaybackControlSettings settings, bool repairInvalidExisting = false)
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
            var existing = JsonStore.Read<PlaybackControlSettings>(FilePath);
            return existing.SchemaVersion == PlaybackControlSettings.CurrentSchemaVersion;
        }
        catch
        {
            // Explicit UI repair may replace unreadable JSON, but only after preserving it verbatim.
            return true;
        }
    }

    private void BackupInvalidExistingFile()
    {
        var backup = FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".bak";
        File.Copy(FilePath, backup, overwrite: false);
    }
}
