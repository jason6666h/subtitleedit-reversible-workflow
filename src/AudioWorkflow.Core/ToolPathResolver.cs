namespace AudioWorkflow;

/// <summary>Resolve against the executable directory, never the caller's working directory.</summary>
public static class ToolPathResolver
{
    public static string Resolve(string? configuredPath, string toolName, string? applicationDirectory = null, string? searchPath = null)
    {
        if (toolName is not ("ffmpeg" or "ffprobe")) throw new ArgumentException("不支援的工具名稱。", nameof(toolName));
        var appDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        var exeName = toolName + ".exe";
        var configured = configuredPath?.Trim() ?? "";
        // The original settings defaults were bare executable names. Treat these as automatic
        // without rewriting the user's settings or losing explicit custom paths on upgrade.
        var automatic = configured.Length == 0 || configured.Equals(exeName, StringComparison.OrdinalIgnoreCase) || configured.Equals(toolName, StringComparison.OrdinalIgnoreCase);
        if (!automatic)
        {
            if (Path.IsPathFullyQualified(configured)) return RequireFile(configured, toolName);
            if (configured.IndexOfAny(['/', '\\']) >= 0)
            {
                if (Path.IsPathRooted(configured)) throw new IOException("請使用完整絕對路徑，或相對於外掛目錄的路徑。");
                return RequireFile(Path.GetFullPath(configured, appDirectory), toolName);
            }
            return FindOnPath(configured, searchPath, toolName);
        }
        var bundledDirectory = Path.Combine(appDirectory, "tools", "ffmpeg");
        var bundled = Path.Combine(bundledDirectory, exeName);
        if (Directory.Exists(bundledDirectory))
        {
            if (!File.Exists(bundled)) throw new FileNotFoundException($"內附 {exeName} 遺失。請重新解壓完整安裝包，或在設定指定有效工具。", bundled);
            return Path.GetFullPath(bundled);
        }
        // Development/unbundled installs only. Empty and relative PATH entries are deliberately
        // ignored so an executable in an unrelated current directory cannot hijack the workflow.
        return FindOnPath(exeName, searchPath, toolName);
    }

    private static string RequireFile(string path, string toolName)
    {
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException($"指定的 {toolName} 路徑無效；請修正設定或清空欄位以使用內附工具。", path);
        return Path.GetFullPath(path);
    }
    private static string FindOnPath(string name, string? searchPath, string toolName)
    {
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        foreach (var entry in (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException($"找不到 {toolName}。請使用完整附帶工具安裝包，或在設定指定 exe。", name);
    }
}
