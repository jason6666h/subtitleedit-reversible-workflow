namespace AudioWorkflow;

/// <summary>A prepared edit, not a host commit. Sample counts count interleaved frames, not channel values.</summary>
public sealed class AudioCutResult
{
    public string SourcePath { get; }
    public string BaselinePath { get; }
    public string OutputPath { get; }
    public string SourceSha256 { get; }
    public string BaselineSha256 { get; }
    public string OutputSha256 { get; }
    public long SourceLength { get; }
    public long BaselineLength { get; }
    public long OutputLength { get; }
    public long StartSample { get; }
    public long EndSample { get; }
    public double StartSeconds => (double)StartSample / SampleRate;
    public double EndSeconds => (double)EndSample / SampleRate;
    public long RemovedSamples => EndSample - StartSample;
    public long OriginalSamples { get; }
    public long OutputSamples => OriginalSamples - RemovedSamples;
    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }

    internal AudioCutResult(string source, string baseline, string output, string sourceHash,
        string baselineHash, string outputHash, long sourceLength, long baselineLength,
        long outputLength, long start, long end, WaveInfo wave)
    {
        SourcePath = source; BaselinePath = baseline; OutputPath = output;
        SourceSha256 = sourceHash; BaselineSha256 = baselineHash; OutputSha256 = outputHash;
        SourceLength = sourceLength; BaselineLength = baselineLength; OutputLength = outputLength;
        StartSample = start; EndSample = end; OriginalSamples = wave.SampleCount;
        SampleRate = wave.SampleRate; Channels = wave.Channels; BitsPerSample = wave.BitsPerSample;
    }
}

/// <summary>
/// A prepared cut whose output was fully hashed and remains pinned read-only.
/// The caller can transfer the validation handle into its session cache without
/// reading the complete output file again.
/// </summary>
public sealed class ValidatedAudioCutResult(AudioCutResult cut, AudioRevisionSource validation) : IDisposable
{
    private AudioRevisionSource? _validation = validation;
    public AudioCutResult Cut { get; } = cut;
    public AudioRevisionSource Validation => _validation ??
        throw new ObjectDisposedException(nameof(ValidatedAudioCutResult));

    public AudioRevisionSource DetachValidation()
    {
        var result = Validation;
        _validation = null;
        return result;
    }

    public void Dispose()
    {
        _validation?.Dispose();
        _validation = null;
    }
}
