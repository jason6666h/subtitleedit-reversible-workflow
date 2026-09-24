using System.Buffers;
using System.Security.Cryptography;

namespace AudioWorkflow;

/// <summary>Full SHA-256, from the stream's current position. No metadata or partial-hash shortcut.</summary>
public static class AudioContentHash
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>Reads the entire file once, hashing both all bytes and an exact PCM byte interval.</summary>
    public static async Task<(string FileHash, string RangeHash)> ComputeWithRangeAsync(
        Stream stream, long offset, long length, CancellationToken token = default)
    {
        if (offset < 0 || length < 0 || offset > stream.Length || length > stream.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(length));
        token.ThrowIfCancellationRequested();
        stream.Position = 0;
        var end = checked(offset + length);
        var expectedLength = stream.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var full = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var range = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long position = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), token).ConfigureAwait(false)) != 0)
            {
                token.ThrowIfCancellationRequested();
                full.AppendData(buffer, 0, read);
                var startInBlock = (int)Math.Clamp(offset - position, 0, read);
                var endInBlock = (int)Math.Clamp(end - position, 0, read);
                if (endInBlock > startInBlock) range.AppendData(buffer, startInBlock, endInBlock - startInBlock);
                position += read;
            }
            token.ThrowIfCancellationRequested();
            if (position != expectedLength) throw new EndOfStreamException("Audio length changed during verification.");
            return (Convert.ToHexString(full.GetHashAndReset()), Convert.ToHexString(range.GetHashAndReset()));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public static string Compute(Stream stream)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;
            while ((read = stream.Read(buffer, 0, BufferSize)) != 0)
                hash.AppendData(buffer, 0, read);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public static async Task<string> ComputeAsync(Stream stream, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), token).ConfigureAwait(false)) != 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            token.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
