using AudioWorkflow;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private sealed record CachedAudioSource(AudioRevisionSource Source, long LastUse);

    private const int MaximumCachedAudioSources = 4;
    private readonly object _validatedAudioSourcesLock = new();
    private readonly Dictionary<string, CachedAudioSource> _validatedAudioSources =
        new(StringComparer.OrdinalIgnoreCase);
    private long _validatedAudioSourceClock;

    private static string AudioValidationKey(string path, string sha256) =>
        Path.GetFullPath(path) + "|" + sha256.ToUpperInvariant();

    private AudioRevisionSource? TryGetValidatedAudioSource(string path, string sha256)
    {
        var key = AudioValidationKey(path, sha256);
        lock (_validatedAudioSourcesLock)
        {
            if (!_validatedAudioSources.TryGetValue(key, out var cached) ||
                !cached.Source.Matches(path, sha256)) return null;
            _validatedAudioSources[key] = cached with { LastUse = ++_validatedAudioSourceClock };
            return cached.Source;
        }
    }

    private async Task<AudioRevisionSource> GetValidatedAudioSourceAsync(string path, string sha256,
        CancellationToken token)
    {
        var cached = TryGetValidatedAudioSource(path, sha256);
        if (cached != null) return cached;
        var created = await AudioRevisionSource.OpenAsync(path, sha256, token);
        return CacheValidatedAudioSource(created);
    }

    internal async Task<FileStream> OpenCachedAudioReadGuardAsync(string path, string sha256,
        CancellationToken token)
    {
        var source = await GetValidatedAudioSourceAsync(path, sha256, token);
        lock (_validatedAudioSourcesLock) return source.OpenPinnedRead();
    }

    private AudioRevisionSource CacheValidatedAudioSource(AudioRevisionSource source)
    {
        var key = AudioValidationKey(source.Path, source.Sha256);
        List<AudioRevisionSource>? dispose = null;
        lock (_validatedAudioSourcesLock)
        {
            if (_validatedAudioSources.TryGetValue(key, out var existing) &&
                existing.Source.Matches(source.Path, source.Sha256))
            {
                source.Dispose();
                _validatedAudioSources[key] = existing with { LastUse = ++_validatedAudioSourceClock };
                return existing.Source;
            }
            _validatedAudioSources[key] = new CachedAudioSource(source, ++_validatedAudioSourceClock);
            while (_validatedAudioSources.Count > MaximumCachedAudioSources)
            {
                var oldest = _validatedAudioSources.MinBy(p => p.Value.LastUse);
                _validatedAudioSources.Remove(oldest.Key);
                (dispose ??= []).Add(oldest.Value.Source);
            }
        }
        if (dispose != null) foreach (var item in dispose) item.Dispose();
        return source;
    }

    internal void ReleaseSynchronousAudioValidationCache()
    {
        AudioRevisionSource[] sources;
        lock (_validatedAudioSourcesLock)
        {
            sources = _validatedAudioSources.Values.Select(p => p.Source).Distinct().ToArray();
            _validatedAudioSources.Clear();
        }
        foreach (var source in sources) source.Dispose();
    }

    private void ReleaseCachedAudioSource(string path)
    {
        AudioRevisionSource[] sources;
        lock (_validatedAudioSourcesLock)
        {
            var full = Path.GetFullPath(path);
            var keys = _validatedAudioSources.Where(p =>
                p.Value.Source.Path.Equals(full, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Key).ToArray();
            sources = keys.Select(k => _validatedAudioSources[k].Source).ToArray();
            foreach (var key in keys) _validatedAudioSources.Remove(key);
        }
        foreach (var source in sources) source.Dispose();
    }

    private static bool IsManagedAudioPath(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return relative.Length > 0 && !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
