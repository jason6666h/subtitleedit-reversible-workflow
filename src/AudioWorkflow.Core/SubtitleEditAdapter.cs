using System.Text.Json;

namespace AudioWorkflow;

public sealed class SubtitleData
{
    public string FileName { get; set; } = "";
    public string Native { get; set; } = "";
    public string Format { get; set; } = "";
}
public sealed class PluginRequest
{
    public int ApiVersion { get; set; } = 1;
    public string ResponseFilePath { get; set; } = "";
    public string PluginDataDirectory { get; set; } = "";
    public string VideoFileName { get; set; } = "";
    public double? VideoPositionSeconds { get; set; }
    public int[] SelectedIndices { get; set; } = [];
    public SubtitleData Subtitle { get; set; } = new();
    public string? MediaContextId { get; set; }
    public int? MediaBridgeVersion { get; set; }
}
public sealed class MediaAction
{
    public string ContextId { get; set; } = "";
    public string FileName { get; set; } = "";
    public double? SeekToSeconds { get; set; }
    public long? ExpectedLength { get; set; }
    public long? ExpectedLastWriteTicks { get; set; }
}
public sealed class PluginResponse
{
    public int ApiVersion { get; set; } = 1;
    public string Status { get; set; } = "cancelled";
    public string? Message { get; set; }
    public MediaAction? MediaAction { get; set; }
    // Intentionally no Subtitle: audio actions must never replace subtitle content.
}
public sealed class SubtitleEditAdapter(PluginRequest request)
{
    public PluginRequest Request { get; } = request;
    public bool CanReload => Request.MediaBridgeVersion == 1 && !string.IsNullOrWhiteSpace(Request.MediaContextId);
    public static PluginRequest Read(string path)
    {
        var value = JsonStore.Read<PluginRequest>(path);
        if (value.ApiVersion != 1) throw new InvalidDataException("不支援的 Subtitle Edit Plugin API 版本。");
        if (!Path.IsPathFullyQualified(value.ResponseFilePath)) throw new InvalidDataException("responseFilePath 必須是絕對路徑。");
        if (value.Subtitle == null || value.SelectedIndices == null) throw new InvalidDataException("無效的字幕狀態。");
        return value;
    }
    public PluginResponse Reload(string path, double? position, AudioFingerprint? fingerprint = null)
    {
        if (!CanReload) throw new InvalidOperationException("官方 Plugin API 尚不支援 reload，請手動載入 WORK.wav 或安裝獨立 bridge。");
        if (position is { } p && (!double.IsFinite(p) || p < 0)) throw new InvalidDataException("無效播放位置。");
        return new() { Status = "ok", MediaAction = new() { ContextId = Request.MediaContextId!, FileName = Path.GetFullPath(path), SeekToSeconds = position, ExpectedLength = fingerprint?.Length, ExpectedLastWriteTicks = fingerprint?.LastWriteTicks } };
    }
}
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(PortablePaths.DecodeJson(File.ReadAllText(path)), Options) ?? throw new InvalidDataException("JSON 為空。");
    public static void Write<T>(string path, T data)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(PortablePaths.EncodeJson(JsonSerializer.Serialize(data, Options)));
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, full, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
