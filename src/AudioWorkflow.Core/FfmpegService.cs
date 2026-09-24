using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AudioWorkflow;

public sealed record AudioProbeInfo(int SampleRate, int Channels, double? DurationSeconds);

public sealed class FfmpegService(WorkflowSettings settings, string? applicationDirectory = null)
{
    public static string[] DecodeArguments(string source, string target) => ["-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-i", source, "-map", "0:a:0", "-vn", "-c:a", "pcm_s24le", "-rf64", "auto", "-f", "wav", target];

    public async Task<AudioProbeInfo> ProbeAsync(string source, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var probe = ToolPathResolver.Resolve(settings.FfprobePath, "ffprobe", applicationDirectory);
        var json = await RunAsync(probe,
            ["-v", "error", "-select_streams", "a:0",
             "-show_entries", "stream=sample_rate,channels,duration:format=duration",
             "-of", "json", source], token);
        using var info = JsonDocument.Parse(json);
        var streams = info.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() == 0) throw new InvalidDataException("找不到音訊串流。");
        var stream = streams[0];
        var rateText = stream.GetProperty("sample_rate").GetString();
        if (!int.TryParse(rateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate) || rate <= 0)
            throw new InvalidDataException("無法判斷音訊取樣率。");
        var channels = stream.GetProperty("channels").GetInt32();
        if (channels <= 0) throw new InvalidDataException("無法判斷音訊聲道數。");

        static double? ReadDuration(JsonElement element)
        {
            if (!element.TryGetProperty("duration", out var value)) return null;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) &&
                   double.IsFinite(duration) && duration > 0
                ? duration
                : null;
        }

        var duration = ReadDuration(stream);
        if (duration == null && info.RootElement.TryGetProperty("format", out var format))
            duration = ReadDuration(format);
        return new AudioProbeInfo(rate, channels, duration);
    }

    public async Task<WaveInfo> DecodeAsync(string source, string target, CancellationToken token)
    {
        if (!source.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || SafePath.Equal(source, target) || File.Exists(target)) throw new IOException("解碼目的檔必須是新的工作暫存檔。");
        var decoder = ToolPathResolver.Resolve(settings.FfmpegPath, "ffmpeg", applicationDirectory);
        var sourceInfo = await ProbeAsync(source, token);
        await RunAsync(decoder, DecodeArguments(source, target), token);
        var wave = WaveInfo.Read(target);
        if (wave.SampleRate != sourceInfo.SampleRate || wave.Channels != sourceInfo.Channels || wave.BitsPerSample != 24) throw new InvalidDataException("解碼後格式不符來源，未發布工作檔。");
        return wave;
    }

    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception e) { throw new IOException("無法啟動工具，請檢查設定路徑：" + executable, e); }
        // Drain both pipes concurrently, but cap retained diagnostics.
        var output = DrainAsync(process.StandardOutput);
        var error = DrainAsync(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            throw;
        }
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0) throw new IOException($"工具失敗 ({process.ExitCode})：{await error}");
        return await output;
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (result.Length < 65536) result.Append(buffer, 0, Math.Min(count, 65536 - result.Length));
        return result.ToString();
    }
}
