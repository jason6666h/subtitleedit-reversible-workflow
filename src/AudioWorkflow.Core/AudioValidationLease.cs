namespace AudioWorkflow;

/// <summary>Read guards for validated files. Keep alive through commit, then dispose.</summary>
public sealed class AudioValidationLease : IDisposable
{
    private readonly List<FileStream> _streams = [];
    internal AudioValidationLease() { }
    internal FileStream Add(FileStream stream) { _streams.Add(stream); return stream; }
    public void Dispose()
    {
        foreach (var stream in _streams) stream.Dispose();
        _streams.Clear();
    }
}
