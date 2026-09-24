using System.Buffers.Binary;

namespace AudioWorkflow;

internal sealed class ValidatedAudioOutput(string path, string sha256, AudioRevisionSource validation) : IDisposable
{
    private AudioRevisionSource? _validation = validation;
    public string Path { get; } = path;
    public string Sha256 { get; } = sha256;
    public AudioRevisionSource DetachValidation()
    {
        var result = _validation ?? throw new ObjectDisposedException(nameof(ValidatedAudioOutput));
        _validation = null;
        return result;
    }
    public void Dispose() { _validation?.Dispose(); _validation = null; }
}

public sealed record AuditionRegion(long StartSample, long EndSample, long ClipStartSample, long ClipEndSample, int FadeSamples)
{
    public long ClipSamples => ClipEndSample - ClipStartSample;
    public long RepairSamples => EndSample - StartSample;
    public long LocalStartSample => StartSample - ClipStartSample;
}

/// <summary>Sample-exact local repair. Context is read-only; PCM outside the selection is copied verbatim.</summary>
internal static class LocalAudioRepair
{
    public static AuditionRegion Select(WaveInfo wave, double startSeconds, double endSeconds)
    {
        long Boundary(double seconds)
        {
            var sample = seconds * wave.SampleRate;
            if (!double.IsFinite(sample) || sample < -0.500001 || sample > wave.SampleCount + 0.500001)
                throw new InvalidDataException("修音範圍超出目前音訊；請重新選取波形區間。");
            return checked((long)Math.Clamp(Math.Round(sample, MidpointRounding.AwayFromZero), 0, wave.SampleCount));
        }
        var start = Boundary(startSeconds);
        var end = Boundary(endSeconds);
        if (end <= start) throw new InvalidDataException("請選取有長度的修音區間。");
        var context = checked(wave.SampleRate * 2L);
        var fade = end - start < 4 ? 0 : (int)Math.Min(Math.Round(wave.SampleRate * 0.005), (end - start) / 2);
        return new(start, end, Math.Max(0, start - context), Math.Min(wave.SampleCount, end + context), fade);
    }

    public static async Task ExtractAsync(string before, string edit, AuditionRegion region, CancellationToken token)
    {
        await using var source = Open(before);
        var layout = PcmWaveLayout.Read(source);
        Validate(region, layout.Info);
        await using var output = New(edit);
        layout.WriteHeader(output, region.ClipSamples);
        await CopyAsync(source, output, layout.DataOffset + region.ClipStartSample * layout.BlockAlign,
            region.ClipSamples * layout.BlockAlign, token);
        if ((region.ClipSamples * layout.BlockAlign & 1) != 0) output.WriteByte(0);
    }

    public static async Task<string> MergeAsync(AuditionHandoff handoff, string repaired, CancellationToken token)
    {
        using var baseline = await AudioRevisionSource.OpenAsync(handoff.BeforePath, handoff.BeforeHash, token);
        using var result = await MergeVerifiedAsync(handoff, repaired, baseline, token);
        return result.Path;
    }

    internal static async Task<ValidatedAudioOutput> MergeVerifiedAsync(AuditionHandoff handoff, string repaired,
        AudioRevisionSource baseline, CancellationToken token)
    {
        var region = handoff.Region ?? throw new InvalidOperationException("Missing local repair range.");
        if (!baseline.Matches(handoff.BeforePath, handoff.BeforeHash))
            throw new InvalidDataException("AU handoff baseline validation is no longer active.");
        await using var source = Open(handoff.BeforePath);
        await using var repair = Open(repaired);
        var original = PcmWaveLayout.Read(source);
        var patch = PcmWaveLayout.Read(repair);
        Validate(region, original.Info);
        if (original.Info != handoff.Timeline || patch.Info != original.Info with { SampleCount = region.ClipSamples } ||
            ValidBits(original) != ValidBits(patch) || ChannelMask(original) != ChannelMask(patch))
            throw new InvalidDataException("AU 片段格式已變更。請保留原長度、取樣率、聲道及整數 PCM 位元深度後重試。");
        var target = Path.Combine(Path.GetDirectoryName(repaired)!, "local-repaired-" + Guid.NewGuid().ToString("N") + ".wav");
        await using (var output = New(target))
        {
            original.WriteHeader(output, original.Info.SampleCount);
            var align = original.BlockAlign;
            await CopyAsync(source, output, original.DataOffset, region.StartSample * align, token);
            await BlendEdgeAsync(source, repair, output, original, patch, region, true, token);
            await CopyAsync(repair, output, patch.DataOffset + (region.LocalStartSample + region.FadeSamples) * align,
                (region.RepairSamples - 2L * region.FadeSamples) * align, token);
            await BlendEdgeAsync(source, repair, output, original, patch, region, false, token);
            await CopyAsync(source, output, original.DataOffset + region.EndSample * align,
                (original.Info.SampleCount - region.EndSample) * align, token);
            if ((original.DataLength & 1) != 0) output.WriteByte(0);
        }
        token.ThrowIfCancellationRequested();
        if (WaveInfo.Read(target) != original.Info) throw new InvalidDataException("局部接回長度驗證失敗；未套用修音。");
        // The baseline is pinned read-only for this entire merge and every byte outside the
        // repair range was copied directly from that same validated handle above. Re-reading
        // the complete baseline solely to compare those bytes adds no new observation. Keep
        // the independent full-file hash of the newly written output before it is adopted.
        var verified = await ValidateAndHashOutputAsync(target, original.Info, original.Format, token);
        return new ValidatedAudioOutput(target, verified.Sha256,
            AudioRevisionSource.AdoptValidated(target, verified.Sha256, verified.Stream));
    }

    private static async Task<(string Sha256, FileStream Stream)> ValidateAndHashOutputAsync(string after,
        WaveInfo expectedInfo, byte[] expectedFormat, CancellationToken token)
    {
        var b = Open(after);
        try
        {
            var output = PcmWaveLayout.Read(b);
            if (expectedInfo != output.Info || !expectedFormat.AsSpan().SequenceEqual(output.Format))
                throw new InvalidDataException("局部修音驗證失敗：格式或長度不同。");
            b.Position = 0;
            var sha256 = await AudioContentHash.ComputeAsync(b, token).ConfigureAwait(false);
            b.Position = 0;
            return (sha256, b);
        }
        catch
        {
            b.Dispose();
            throw;
        }
    }

    internal static async Task VerifyOutsideAsync(string before, string after, AuditionRegion region, CancellationToken token)
    {
        await using var a = Open(before);
        await using var b = Open(after);
        var source = PcmWaveLayout.Read(a);
        var output = PcmWaveLayout.Read(b);
        Validate(region, source.Info);
        if (source.Info != output.Info || !source.Format.AsSpan().SequenceEqual(output.Format))
            throw new InvalidDataException("局部修音驗證失敗：格式或長度不同。");
        var x = new byte[131072];
        var y = new byte[x.Length];
        foreach (var (start, end) in new[] { (0L, region.StartSample), (region.EndSample, source.Info.SampleCount) })
        {
            a.Position = source.DataOffset + start * source.BlockAlign;
            b.Position = output.DataOffset + start * output.BlockAlign;
            var remaining = (end - start) * source.BlockAlign;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, x.Length);
                await a.ReadExactlyAsync(x.AsMemory(0, count), token);
                await b.ReadExactlyAsync(y.AsMemory(0, count), token);
                if (!x.AsSpan(0, count).SequenceEqual(y.AsSpan(0, count)))
                    throw new InvalidDataException("局部修音範圍外 PCM 不一致，拒絕接回。");
                remaining -= count;
            }
        }
    }

    private static void Validate(AuditionRegion region, WaveInfo wave)
    {
        if (region.ClipStartSample < 0 || region.StartSample < region.ClipStartSample ||
            region.EndSample <= region.StartSample || region.EndSample > region.ClipEndSample ||
            region.ClipEndSample > wave.SampleCount || region.FadeSamples < 0 ||
            region.FadeSamples > wave.SampleRate || 2L * region.FadeSamples > region.RepairSamples)
            throw new InvalidDataException("Invalid local repair sample mapping.");
    }

    private static int ValidBits(PcmWaveLayout layout) => BitConverter.ToUInt16(layout.Format) == 0xfffe
        ? BitConverter.ToUInt16(layout.Format, 18) : layout.Info.BitsPerSample;
    private static uint ChannelMask(PcmWaveLayout layout) => BitConverter.ToUInt16(layout.Format) == 0xfffe
        ? BitConverter.ToUInt32(layout.Format, 20) : 0;

    private static async Task BlendEdgeAsync(Stream source, Stream repair, Stream output, PcmWaveLayout original,
        PcmWaveLayout patch, AuditionRegion region, bool entry, CancellationToken token)
    {
        var frames = region.FadeSamples;
        if (frames == 0) return;
        var sample = entry ? region.StartSample : region.EndSample - frames;
        var count = checked(frames * original.BlockAlign);
        var a = new byte[count];
        var b = new byte[count];
        source.Position = original.DataOffset + sample * original.BlockAlign;
        repair.Position = patch.DataOffset + (sample - region.ClipStartSample) * patch.BlockAlign;
        await source.ReadExactlyAsync(a, token);
        await repair.ReadExactlyAsync(b, token);
        var width = original.Info.BitsPerSample / 8;
        for (var frame = 0; frame < frames; frame++)
        {
            var weight = (double)(entry ? frame : frames - 1 - frame) / frames;
            // At file edges there is no external join to smooth.
            if ((entry && region.StartSample == 0) || (!entry && region.EndSample == original.Info.SampleCount)) weight = 1;
            for (var channel = 0; channel < original.Info.Channels; channel++)
            {
                var offset = frame * original.BlockAlign + channel * width;
                var oldValue = ReadSample(a.AsSpan(offset, width));
                var newValue = ReadSample(b.AsSpan(offset, width));
                var value = (int)Math.Round(oldValue + ((double)newValue - oldValue) * weight);
                WriteSample(b.AsSpan(offset, width), value);
            }
        }
        await output.WriteAsync(b, token);
    }

    private static int ReadSample(ReadOnlySpan<byte> sample) => sample.Length switch
    {
        2 => BinaryPrimitives.ReadInt16LittleEndian(sample),
        3 => (sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8,
        4 => BinaryPrimitives.ReadInt32LittleEndian(sample),
        _ => throw new InvalidDataException("Unsupported PCM width."),
    };

    private static void WriteSample(Span<byte> sample, int value)
    {
        for (var i = 0; i < sample.Length; i++) sample[i] = (byte)(value >> (8 * i));
    }

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static FileStream New(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
        131072, FileOptions.Asynchronous);

    private static async Task CopyAsync(Stream source, Stream output, long offset, long count, CancellationToken token)
    {
        source.Position = offset;
        var buffer = new byte[131072];
        while (count > 0)
        {
            token.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), token);
            if (read == 0) throw new EndOfStreamException("Incomplete PCM samples.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            count -= read;
        }
    }
}
