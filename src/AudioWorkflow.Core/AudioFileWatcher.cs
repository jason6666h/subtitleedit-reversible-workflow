using System.Security.Cryptography;

namespace AudioWorkflow;

public sealed record AudioFingerprint(long Length, long LastWriteTicks, string Hash, WaveInfo Wave);
public sealed class AudioFileWatcher : IDisposable
{
    private readonly FileSystemWatcher watcher;
    private readonly string path;
    private long revision;
    private bool disposed;
    public AudioFileWatcher(string path)
    {
        this.path = Path.GetFullPath(path);
        watcher = new FileSystemWatcher(Path.GetDirectoryName(this.path)!) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, IncludeSubdirectories = false };
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Deleted += Changed;
        watcher.Renamed += (_, e) => { if (Matches(e.FullPath) || Matches(e.OldFullPath)) Interlocked.Increment(ref revision); };
        watcher.Error += (_, _) => Interlocked.Increment(ref revision);
        watcher.EnableRaisingEvents = true;
    }
    private bool Matches(string value) => SafePath.Equal(path, value);
    private void Changed(object sender, FileSystemEventArgs e) { if (Matches(e.FullPath)) Interlocked.Increment(ref revision); }
    public static AudioFingerprint Fingerprint(string path)
    {
        SafePath.NoLinks(path);
        // Allow SE's existing read handle, but exclude writers during validation.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var wave = WaveInfo.Read(stream);
        stream.Position = 0;
        var hash = AudioContentHash.Compute(stream);
        return new(stream.Length, File.GetLastWriteTimeUtc(path).Ticks, hash, wave);
    }
    public async Task<AudioFingerprint> WaitForSaveAsync(AudioFingerprint baseline, Action<string>? status, CancellationToken token)
    {
        long checkedRevision = -1;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var before = Interlocked.Read(ref revision);
            // Cheap metadata fallback every 3 seconds covers missed notifications; hashing only on changes.
            var info = new FileInfo(path);
            if (before == checkedRevision && info.Exists && info.Length == baseline.Length && info.LastWriteTimeUtc.Ticks == baseline.LastWriteTicks)
            { await Task.Delay(3000, token); continue; }
            try
            {
                await Task.Delay(1200, token);
                if (before != Interlocked.Read(ref revision)) continue;
                var first = await Task.Run(() => Fingerprint(path), token);
                if (first.Hash == baseline.Hash) { checkedRevision = before; baseline = first; await Task.Delay(3000, token); continue; }
                await Task.Delay(1200, token);
                var second = await Task.Run(() => Fingerprint(path), token);
                if (first != second || before != Interlocked.Read(ref revision)) continue;
                if (!baseline.Wave.SameTimeline(second.Wave)) throw new InvalidOperationException("音檔 sample rate、channels 或 sample 數已改變。第一階段不支援時間軸刪減；未自動重載，請還原長度或人工同步字幕。");
                return second;
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                status?.Invoke("等待 WORK.wav 完成儲存／解除鎖定；若已改名，請還原名稱或取消。");
                await Task.Delay(3000, token);
            }
        }
    }
    public void Dispose() { if (disposed) return; disposed = true; watcher.Dispose(); }
}
