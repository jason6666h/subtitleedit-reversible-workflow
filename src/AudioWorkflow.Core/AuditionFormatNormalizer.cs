using System.Buffers.Binary;

namespace AudioWorkflow;

/// <summary>Only changes PCM representation, never frame count, rate or channel order.</summary>
internal static class AuditionFormatNormalizer
{
    public static async Task<string> NormalizeAsync(AuditionHandoff handoff, string captured, CancellationToken token)
    {
        using var baseline = await AudioRevisionSource.OpenAsync(handoff.BeforePath, handoff.BeforeHash, token);
        return await NormalizeVerifiedAsync(handoff, captured, baseline, token);
    }

    internal static async Task<string> NormalizeVerifiedAsync(AuditionHandoff handoff, string captured,
        AudioRevisionSource baseline, CancellationToken token)
    {
        if (!baseline.Matches(handoff.BeforePath, handoff.BeforeHash))
            throw new InvalidDataException("AU handoff baseline validation is no longer active.");
        using var originalStream = File.OpenRead(handoff.BeforePath);
        var original = PcmWaveLayout.Read(originalStream);
        if (original.Info != handoff.Timeline) throw new InvalidDataException("交接基準格式與紀錄不一致，未接回。");
        await using var input = new FileStream(captured, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
        var edited = PcmWaveLayout.Read(input, allowFloatingPoint: true);
        var expected = original.Info with { SampleCount = handoff.Region?.ClipSamples ?? original.Info.SampleCount };
        if (!expected.SameTimeline(edited.Info))
            throw new InvalidDataException($"AU 時間軸不符，未接回。需要 {expected.SampleRate} Hz、{expected.Channels} 聲道、{expected.SampleCount} 取樣；實際 {edited.Info.SampleRate} Hz、{edited.Info.Channels} 聲道、{edited.Info.SampleCount} 取樣。請在 AU 復原刪除／插入／伸縮或取樣率變更後重試。修音副本仍保留。");
        var oldMask = Mask(original); var newMask = Mask(edited);
        bool Equivalent(uint a, uint b) => a == b || (expected.Channels == 1 && a is 0 or 4 && b is 0 or 4) || (expected.Channels == 2 && a is 0 or 3 && b is 0 or 3);
        if (!Equivalent(oldMask,newMask)) throw new InvalidDataException($"AU 聲道排列不同（{oldMask:X} → {newMask:X}），無法安全猜測；請保留原聲道排列後重試。");
        if (ValidBits(original) != original.Info.BitsPerSample) throw new InvalidDataException("基準 WAV 使用非完整有效位元，請先另建標準 PCM 工作檔。");
        if (edited.Info == expected && original.Format.AsSpan().SequenceEqual(edited.Format)) return captured;
        var target = Path.Combine(Path.GetDirectoryName(captured)!, "normalized-" + Guid.NewGuid().ToString("N") + ".wav");
        var floating = BitConverter.ToUInt16(edited.Format) == 3 || (BitConverter.ToUInt16(edited.Format) == 0xfffe && new Guid(edited.Format.AsSpan(24,16)) == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        var inWidth = edited.Info.BitsPerSample / 8; var outWidth = expected.BitsPerSample / 8;
        var scale = Math.Pow(2,expected.BitsPerSample-1);
        var inBuffer = new byte[4096 * edited.BlockAlign]; var outBuffer = new byte[4096 * original.BlockAlign];
        input.Position = edited.DataOffset;
        await using (var output = new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,FileOptions.Asynchronous))
        {
            original.WriteHeader(output,expected.SampleCount);
            long left = expected.SampleCount;
            while(left > 0)
            {
                token.ThrowIfCancellationRequested();
                int frames = (int)Math.Min(4096,left);
                await input.ReadExactlyAsync(inBuffer.AsMemory(0,frames * edited.BlockAlign),token);
                for(int i=0;i<frames * expected.Channels;i++)
                {
                    var bytes = inBuffer.AsSpan(i * inWidth,inWidth);
                    double value = floating ? (inWidth == 4 ? BitConverter.ToSingle(bytes) : BitConverter.ToDouble(bytes)) :
                        ReadInteger(bytes) / Math.Pow(2,edited.Info.BitsPerSample-1);
                    if(!double.IsFinite(value) || value < -1 || value > 1)
                        throw new InvalidDataException("AU 浮點修音含超過 0 dBFS 或非有限數值，未自動截波。請降低增益／限制峰值後重試；修音仍保留。");
                    var integer = (long)Math.Clamp(Math.Round(value * scale,MidpointRounding.ToEven),-scale,scale-1);
                    for(int b=0;b<outWidth;b++) outBuffer[i*outWidth+b]=(byte)(integer>>(b*8));
                }
                await output.WriteAsync(outBuffer.AsMemory(0,frames * original.BlockAlign),token);
                left-=frames;
            }
            if((expected.SampleCount * original.BlockAlign & 1) != 0) output.WriteByte(0);
        }
        if(WaveInfo.Read(target) != expected) throw new InvalidDataException("AU 格式正規化後取樣驗證失敗，未接回。");
        JsonStore.Write(target+".conversion.json",new { SourcePath=captured, Original=original.Info, Returned=edited.Info, FloatingPoint=floating, NormalizedPath=target, FramesPreserved=true });
        return target;
    }
    private static long ReadInteger(ReadOnlySpan<byte> bytes) => bytes.Length switch {2=>BinaryPrimitives.ReadInt16LittleEndian(bytes),3=>(bytes[0]|bytes[1]<<8|bytes[2]<<16)<<8>>8,4=>BinaryPrimitives.ReadInt32LittleEndian(bytes),_=>throw new InvalidDataException("Unsupported PCM width")};
    private static int ValidBits(PcmWaveLayout x) => BitConverter.ToUInt16(x.Format)==0xfffe ? BitConverter.ToUInt16(x.Format,18) : x.Info.BitsPerSample;
    private static uint Mask(PcmWaveLayout x) => BitConverter.ToUInt16(x.Format)==0xfffe ? BitConverter.ToUInt32(x.Format,20) : 0;
}
