using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nikse.SubtitleEdit;

internal static class PortableStartup
{
    private sealed record Mapping(string Source, string Target);
    private static FileStream? _runLock;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(
        IntPtr hWnd,
        string text,
        string caption,
        uint type);

    public static void Initialize()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var configPath = Path.Combine(root, "portable-paths.json");
        var inventoryPath = Path.Combine(root, "PORTABLE-PAYLOAD-SHA256.json");
        if (!File.Exists(configPath) || !File.Exists(inventoryPath))
            return;

        try
        {
            InitializePortable(root, configPath, inventoryPath);
        }
        catch (Exception ex)
        {
            _runLock?.Dispose();
            _runLock = null;
            ShowFatalError(ex.Message);
            throw;
        }
    }

    private static void InitializePortable(
        string root,
        string configPath,
        string inventoryPath)
    {
        EnsureWritableLocation(root);
        Directory.SetCurrentDirectory(root);
        AcquireRunLock(root);

        var mappings = ReadMappings(root, configPath);
        VerifyPayload(root, inventoryPath);
        VerifyRequiredFiles(root);
        RebaseSettings(root, mappings);
        ProbeTool(SafePath(root, "ffmpeg/ffmpeg.exe"), "FFmpeg");
        ProbeTool(SafePath(root, "ffmpeg/ffprobe.exe"), "FFprobe");

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _runLock?.Dispose();
            _runLock = null;
        };
    }

    private static void EnsureWritableLocation(string root)
    {
        foreach (var forbidden in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (!string.IsNullOrWhiteSpace(forbidden) &&
                root.StartsWith(
                    Path.TrimEndingDirectorySeparator(forbidden) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "可攜版 Subtitle Edit 必須放在可寫入的位置，不能放在 Program Files。");
            }
        }
    }
    private static void AcquireRunLock(string root)
    {
        try
        {
            _runLock = new FileStream(
                SafePath(root, ".portable-running.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "這個 Portable Subtitle Edit 已經在執行中，請先關閉目前視窗再重新開啟。",
                ex);
        }
    }

    private static List<Mapping> ReadMappings(string root, string configPath)
    {
        var json = JsonNode.Parse(File.ReadAllText(configPath, Encoding.UTF8))
                   as JsonObject
                   ?? throw new InvalidOperationException("portable-paths.json 格式無效。");
        if (json["schemaVersion"]?.GetValue<int>() != 1)
            throw new InvalidOperationException("不支援的 portable-paths.json 版本。");

        var result = new List<Mapping>();
        if (json["mappings"] is JsonArray mappings)
        {
            foreach (var node in mappings.OfType<JsonObject>())
            {
                var source = node["source"]?.GetValue<string>();
                var target = node["target"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                    continue;
                _ = SafePath(root, target);
                result.Add(new Mapping(source, target));
            }
        }

        var lastLocationPath = SafePath(root, "Data/last-location.json");
        if (File.Exists(lastLocationPath))
        {
            var last = JsonNode.Parse(File.ReadAllText(lastLocationPath, Encoding.UTF8))
                       as JsonObject;
            var oldRoot = last?["root"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(oldRoot) &&
                !string.Equals(oldRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                result.Insert(0, new Mapping(oldRoot, "."));
            }
        }

        return result
            .OrderByDescending(mapping => mapping.Source.Length)
            .ToList();
    }
    private static void VerifyPayload(string root, string inventoryPath)
    {
        var entries = JsonNode.Parse(File.ReadAllText(inventoryPath, Encoding.UTF8))
                      as JsonArray
                      ?? throw new InvalidOperationException(
                          "PORTABLE-PAYLOAD-SHA256.json 格式無效。");

        foreach (var node in entries.OfType<JsonObject>())
        {
            var relative = node["path"]?.GetValue<string>()
                           ?? throw new InvalidOperationException("程式檔清單缺少 path。");
            var expected = node["sha256"]?.GetValue<string>()
                           ?? throw new InvalidOperationException(
                               $"程式檔清單缺少 SHA-256：{relative}");
            var file = SafePath(root, relative);
            if (!File.Exists(file))
                throw new InvalidOperationException($"Portable 程式檔缺失：{relative}");

            using var stream = File.Open(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Portable 程式檔驗證失敗：{relative}");
        }
    }
    private static void VerifyRequiredFiles(string root)
    {
        foreach (var relative in new[]
                 {
                     "SubtitleEdit.exe",
                     "AudioWorkflow.Core.dll",
                     "SubtitleReview.Core.dll",
                     "ffmpeg/ffmpeg.exe",
                     "ffmpeg/ffprobe.exe",
                     "libmpv-2.dll",
                     "Languages/ChineseTraditional.json",
                 })
        {
            if (!File.Exists(SafePath(root, relative)))
                throw new InvalidOperationException($"缺少必要檔案：{relative}");
        }
    }

    private static void RebaseSettings(
        string root,
        IReadOnlyList<Mapping> mappings)
    {
        var settingsPath = SafePath(root, "Settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath, Encoding.UTF8))
                       as JsonObject
                       ?? throw new InvalidOperationException("Settings.json 格式無效。");

        VisitSettings(root, settings, mappings);
        if (settings["General"] is not JsonObject general)
            throw new InvalidOperationException("Settings.json 缺少 General 設定。");
        general["FfmpegPath"] = SafePath(root, "ffmpeg/ffmpeg.exe");
        general["LibMpvPath"] = SafePath(root, "libmpv-2.dll");
        general["CheckForUpdatesOnStartup"] = false;
        general["LastWorkingDirectory"] = SafePath(root, "Data");
        if (string.IsNullOrWhiteSpace(general["Language"]?.GetValue<string>()))
            general["Language"] = "ChineseTraditional";

        Directory.CreateDirectory(SafePath(root, "Data/AudioWork"));
        Directory.CreateDirectory(SafePath(root, "Data/Settings"));

        var temp = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = SafePath(root, "Data/Settings-before-launch.json");
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(
            temp,
            settings.ToJsonString(options),
            new UTF8Encoding(false));
        try
        {
            File.Replace(temp, settingsPath, backup, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        WriteLastLocation(root);
    }
    private static void VisitSettings(
        string root,
        JsonNode? node,
        IReadOnlyList<Mapping> mappings)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Value is JsonValue value &&
                    value.TryGetValue<string>(out var text) &&
                    (IsPathProperty(property.Key) ||
                     text.StartsWith("portable:/", StringComparison.Ordinal)))
                {
                    obj[property.Key] = ResolvePortable(root, text, mappings);
                }
                else
                {
                    VisitSettings(root, property.Value, mappings);
                }
            }

            return;
        }

        if (node is not JsonArray array)
            return;
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonValue value &&
                value.TryGetValue<string>(out var text))
            {
                array[i] = ResolvePortable(root, text, mappings);
            }
            else
            {
                VisitSettings(root, array[i], mappings);
            }
        }
    }

    private static bool IsPathProperty(string name) =>
        name.Equals("ImmutableRevision", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Path", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("FileName", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("Directory", StringComparison.OrdinalIgnoreCase);

    private static string ResolvePortable(
        string root,
        string value,
        IReadOnlyList<Mapping> mappings)
    {
        if (value.StartsWith("portable:/", StringComparison.Ordinal))
            return SafePath(root, value[10..]);
        foreach (var mapping in mappings)
        {
            var source = mapping.Source.TrimEnd('\\', '/');
            if (value.Equals(source, StringComparison.OrdinalIgnoreCase))
                return SafePath(root, mapping.Target);

            if (!value.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase) &&
                !value.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase))
                continue;

            var suffix = value[(source.Length + 1)..];
            return SafePath(root, Path.Combine(mapping.Target, suffix));
        }

        return value;
    }

    private static string SafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) ||
            relative.Contains(':') ||
            relative.Split('/', '\\').Any(part => part == ".."))
        {
            throw new InvalidOperationException($"不安全的 Portable 路徑：{relative}");
        }

        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Portable 路徑超出程式目錄：{relative}");
        }

        return full;
    }

    private static void WriteLastLocation(string root)
    {
        var path = SafePath(root, "Data/last-location.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = new JsonObject { ["root"] = root };
        File.WriteAllText(
            path,
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static void ProbeTool(string path, string name)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            Arguments = "-v error -version",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"{name} 無法啟動。");
        if (!process.WaitForExit(15_000))
        {
            try { process.Kill(true); } catch { }
            throw new InvalidOperationException($"{name} 啟動驗證逾時。");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{name} 啟動驗證失敗。");
    }

    private static void ShowFatalError(string message)
    {
        try
        {
            MessageBoxW(
                IntPtr.Zero,
                "Portable Subtitle Edit 啟動失敗：\n\n" + message,
                "Subtitle Edit",
                0x10);
        }
        catch
        {
            // Program.Main will still log and terminate if the native dialog is unavailable.
        }
    }
}
