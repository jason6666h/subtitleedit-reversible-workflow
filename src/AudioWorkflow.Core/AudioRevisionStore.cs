using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioWorkflow;

public sealed record AudioRevision(string Id, string? ParentId, string Kind, string AudioPath,
    string AudioSha256, string SubtitleSha256, string CheckpointSha256, DateTime CreatedUtc);

/// <summary>Validated read handle denies replacement/writes until disposal (Windows local files).</summary>
public sealed class AudioRevisionSource : IDisposable
{
    private readonly object _gate = new();
    private FileStream? _stream;
    public string Path { get; }
    public string Sha256 { get; }
    internal bool IsAlive { get { lock (_gate) return _stream != null; } }
    public AudioRevisionSource(string path, string expectedHash)
    {
        Path = System.IO.Path.GetFullPath(path);
        _stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Sha256 = AudioContentHash.Compute(_stream);
            if (!Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("音訊版本已遭修改，拒絕保存不一致的字幕。");
        }
        catch { _stream.Dispose(); _stream = null; throw; }
    }

    private AudioRevisionSource(string path, string sha256, FileStream validatedStream)
    {
        Path = System.IO.Path.GetFullPath(path);
        Sha256 = sha256;
        _stream = validatedStream;
    }

    public static async Task<AudioRevisionSource> OpenAsync(string path, string expectedHash, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(expectedHash) || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            throw new ArgumentException("An expected SHA-256 identity is required.", nameof(expectedHash));
        return await OpenCoreAsync(path, expectedHash, token).ConfigureAwait(false);
    }

    public static Task<AudioRevisionSource> OpenAndHashAsync(string path, CancellationToken token) =>
        OpenCoreAsync(path, null, token);

    public static async Task<AudioRevisionSource> CopyValidatedAsync(
        string sourcePath, string targetPath, string expectedHash, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(expectedHash) || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            throw new ArgumentException("An expected SHA-256 identity is required.", nameof(expectedHash));

        var source = System.IO.Path.GetFullPath(sourcePath);
        var target = System.IO.Path.GetFullPath(targetPath);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Validated copy source and target must be different.");
        if (File.Exists(target))
            throw new IOException("Validated copy target must be a new file.");

        var partial = target + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var expectedLength = input.Length;
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    copied += read;
                }
                if (copied != expectedLength)
                    throw new EndOfStreamException("Audio length changed during validated copy.");
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Audio content changed during validated copy.");

            File.Move(partial, target, false);
            var validatedStream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 131072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new AudioRevisionSource(target, actualHash, validatedStream);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            try { if (File.Exists(target)) File.Delete(target); } catch { }
            throw;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task<AudioRevisionSource> OpenCoreAsync(string path, string? expectedHash, CancellationToken token)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var hash = await AudioContentHash.ComputeAsync(stream, token).ConfigureAwait(false);
            if (expectedHash != null && !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Audio content changed; do not commit.");
            stream.Position = 0;
            return new AudioRevisionSource(fullPath, hash, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static AudioRevisionSource AdoptValidated(string path, string sha256, FileStream validatedStream)
    {
        if (!validatedStream.CanRead || !System.IO.Path.GetFullPath(validatedStream.Name).Equals(
                System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Validated audio handle does not match its path.");
        validatedStream.Position = 0;
        return new AudioRevisionSource(path, sha256, validatedStream);
    }

    public bool Matches(string path, string sha256)
    {
        lock (_gate) return _stream != null &&
            Path.Equals(System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) &&
            Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    public FileStream OpenPinnedRead()
    {
        lock (_gate)
        {
            if (_stream == null) throw new ObjectDisposedException(nameof(AudioRevisionSource));
            return new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
    }

    public void Dispose()
    {
        lock (_gate) { _stream?.Dispose(); _stream = null; }
    }
}

/// <summary>Append-only paired snapshots. Atomic directory publication; no overwrite API.</summary>
public static class AudioRevisionStore
{
    private const int MaxSnapshotBytes = 64 * 1024 * 1024;
    public static string Save(string root, string? parentId, string kind, string audioPath,
        string expectedAudioHash, string srt, string checkpoint, AudioRevisionSource? validatedSource = null, PortablePathMap? paths = null)
    {
        Directory.CreateDirectory(root);
        using var ownedSource = validatedSource == null ? new AudioRevisionSource(audioPath, expectedAudioHash) : null;
        var verified = validatedSource ?? ownedSource!;
        if (!verified.IsAlive || !verified.Path.Equals(Path.GetFullPath(audioPath), StringComparison.OrdinalIgnoreCase) ||
            !verified.Sha256.Equals(expectedAudioHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Audio revision guard is not valid for this snapshot.");
        var audioHash = verified.Sha256;
        var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N");
        var pending = Path.Combine(root, ".pending-" + id);
        var target = Path.Combine(root, id);
        Directory.CreateDirectory(pending);
        var text = new UTF8Encoding(false).GetBytes(srt);
        paths ??= PortablePaths.Current;
        var state = Encoding.UTF8.GetBytes(paths?.TransformJson(checkpoint, true) ?? checkpoint);
        if (text.Length > MaxSnapshotBytes || state.Length > MaxSnapshotBytes)
            throw new InvalidDataException("版本字幕／恢復資料超過 64 MiB，未發布版本。");
        Write(Path.Combine(pending, "subtitles.srt"), text);
        Write(Path.Combine(pending, "state.syncaudio.json"), state);
        var entry = new AudioRevision(id, parentId, kind, paths?.Encode(Path.GetFullPath(audioPath)) ?? Path.GetFullPath(audioPath), audioHash,
            Convert.ToHexString(SHA256.HashData(text)), Convert.ToHexString(SHA256.HashData(state)), DateTime.UtcNow);
        Write(Path.Combine(pending, "revision.json"), JsonSerializer.SerializeToUtf8Bytes(entry));
        Directory.Move(pending, target);
        return target;
    }

    public static AudioRevision ReadManifest(string directory, PortablePathMap? paths = null)
    {
        if (Path.GetFileName(directory).StartsWith(".pending-", StringComparison.Ordinal))
            throw new InvalidDataException("未完成的版本不可恢復。");
        var manifest = Path.Combine(directory, "revision.json");
        if (new FileInfo(manifest).Length > 1024 * 1024) throw new InvalidDataException("版本清單過大。");
        var revision = JsonSerializer.Deserialize<AudioRevision>(File.ReadAllBytes(manifest))
            ?? throw new InvalidDataException("Missing revision.");
        if (revision.Id != Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            throw new InvalidDataException("版本 ID 與目錄不一致。");
        if (new FileInfo(Path.Combine(directory, "subtitles.srt")).Length > MaxSnapshotBytes ||
            new FileInfo(Path.Combine(directory, "state.syncaudio.json")).Length > MaxSnapshotBytes)
            throw new InvalidDataException("版本資料過大。");
        paths ??= PortablePaths.Current;
        var audioPath = paths?.Resolve(revision.AudioPath) ?? revision.AudioPath;
        return revision with { AudioPath = audioPath };
    }

    public static AudioRevision Validate(string directory, PortablePathMap? paths = null,
        AudioRevisionSource? validatedAudio = null)
    {
        var revision = ReadManifest(directory, paths);
        Check(Path.Combine(directory, "subtitles.srt"), revision.SubtitleSha256);
        Check(Path.Combine(directory, "state.syncaudio.json"), revision.CheckpointSha256);
        if (validatedAudio == null || !validatedAudio.Matches(revision.AudioPath, revision.AudioSha256))
            Check(revision.AudioPath, revision.AudioSha256);
        return revision;
    }

    public static string ReadVerifiedCheckpoint(string directory, PortablePathMap? paths = null)
        => ReadVerifiedCheckpoint(directory, null, paths);

    public static string ReadVerifiedCheckpoint(string directory, AudioRevisionSource? validatedAudio,
        PortablePathMap? paths = null)
    {
        using var manifest = new FileStream(Path.Combine(directory, "revision.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var subtitle = new FileStream(Path.Combine(directory, "subtitles.srt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var state = new FileStream(Path.Combine(directory, "state.syncaudio.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        paths ??= PortablePaths.Current;
        Validate(directory, paths, validatedAudio);
        using var reader = new StreamReader(state, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var raw = reader.ReadToEnd();
        return paths?.TransformJson(raw, false) ?? raw;
    }

    private static void Check(string file, string expected)
    {
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!AudioContentHash.Compute(input).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("版本驗證失敗：" + file);
    }

    private static void Write(string file, byte[] bytes)
    {
        using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
        output.Flush(true);
    }
}
