using System;
using System.Diagnostics;
using System.IO;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main;

/// <summary>Bounded local timings only: no media names or subtitle content. Never affects a transaction.</summary>
internal sealed class AudioPerformanceLog : IDisposable
{
    private static readonly object Gate = new();
    private readonly string _stage;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private AudioPerformanceLog(string stage) => _stage = stage;
    internal static AudioPerformanceLog Measure(string stage) => new(stage);
    public void Dispose()
    {
        _watch.Stop();
        try
        {
            lock (Gate)
            {
                var path = Path.Combine(Se.DataFolder, "AudioPerformance.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, FormattableString.Invariant($"{DateTime.UtcNow:O}\t{_stage}\t{_watch.Elapsed.TotalMilliseconds:F1} ms\n"));
            }
        }
        catch { /* Diagnostics must not fail or mask a commit/cancellation. */ }
    }
}
