using System;
using System.IO;
using System.Text.Json;
using AudioWorkflow;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private sealed record AudioCheckpointHeader(int SchemaVersion, string? Kind);

    /// <summary>
    /// Central version dispatch for persisted synchronous-audio checkpoints.
    /// Keep old readers here when a future writer advances the schema; opening a
    /// checkpoint must never rewrite it or silently reinterpret a newer format.
    /// </summary>
    private static AudioCheckpoint ReadAudioCheckpoint(string path)
    {
        var raw = PortablePaths.DecodeJson(File.ReadAllText(path));
        return ReadAudioCheckpointJson(raw, path);
    }

    private static AudioCheckpoint ReadAudioCheckpointJson(string json, string source = "checkpoint")
    {
        var header = JsonSerializer.Deserialize<AudioCheckpointHeader>(json, JsonStore.Options)
            ?? throw new InvalidDataException("工作階段資料為空。");
        if (header.SchemaVersion > 1)
            throw new InvalidDataException($"{source} 由較新的版本建立（schema {header.SchemaVersion}）；已唯讀停止，未改寫舊存檔。");
        if (header.SchemaVersion != 1 || header.Kind != "se-synchronous-audio")
            throw new InvalidDataException($"{source} 使用不支援的工作階段格式。");
        return JsonSerializer.Deserialize<AudioCheckpoint>(json, JsonStore.Options)
            ?? throw new InvalidDataException("工作階段資料不完整。");
    }

    internal static int ReadAudioCheckpointSchemaVersionForTest(string json) =>
        ReadAudioCheckpointJson(json, "test checkpoint").SchemaVersion;
}
