using System.Text;

namespace AudioWorkflow;

public sealed record WaveInfo(int SampleRate, int Channels, int BitsPerSample, long SampleCount)
{
    public double DurationSeconds => (double)SampleCount / SampleRate;
    public static WaveInfo Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(stream);
    }
    public static WaveInfo Read(Stream stream, bool allowFloatingPoint = false)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);
        string Tag() => Encoding.ASCII.GetString(reader.ReadBytes(4));
        var riff = Tag();
        var riffSize = reader.ReadUInt32();
        if ((riff != "RIFF" && riff != "RF64") || Tag() != "WAVE") throw new InvalidDataException("不是 RIFF/RF64 PCM WAV。");
        long? rfDataSize = null;
        long? declaredLength = riff == "RIFF" ? (long)riffSize + 8 : null;
        int rate = 0, channels = 0, bits = 0, align = 0;
        long dataSize = -1;
        while (stream.Position + 8 <= stream.Length)
        {
            var tag = Tag();
            long size = reader.ReadUInt32();
            var start = stream.Position;
            if (tag == "ds64" && riff == "RF64" && size >= 28)
            {
                declaredLength = checked((long)reader.ReadUInt64() + 8);
                rfDataSize = checked((long)reader.ReadUInt64());
            }
            if (tag == "data" && size == uint.MaxValue) size = rfDataSize ?? throw new InvalidDataException("RF64 缺少 ds64。");
            if (size < 0 || size > stream.Length - start) throw new InvalidDataException("WAV 尚未儲存完整。");
            if (tag == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("WAV fmt 太短。");
                var format = reader.ReadUInt16();
                var floating = format == 3;
                channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                var byteRate = reader.ReadUInt32(); align = reader.ReadUInt16(); bits = reader.ReadUInt16();
                if (format == 0xfffe && size >= 40)
                {
                    if (reader.ReadUInt16() < 22) throw new InvalidDataException("無效 extensible WAV。");
                    reader.ReadUInt16(); reader.ReadUInt32();
                    var guid = new Guid(reader.ReadBytes(16));
                    floating = guid == new Guid("00000003-0000-0010-8000-00aa00389b71");
                    if (guid != new Guid("00000001-0000-0010-8000-00aa00389b71") && !(allowFloatingPoint && floating)) throw new InvalidDataException("工作母檔須為整數 PCM WAV。");
                }
                else if (format != 1 && !(allowFloatingPoint && floating)) throw new InvalidDataException("工作母檔須為整數 PCM WAV。");
                if (rate <= 0 || channels <= 0 || (floating ? bits is not (32 or 64) : bits is not (8 or 16 or 24 or 32)) || align != channels * (bits / 8) || byteRate != (long)rate * align)
                    throw new InvalidDataException("無效 WAV 音訊格式。");
            }
            if (tag == "data") { if (dataSize >= 0) throw new InvalidDataException("不支援多 data chunk WAV。"); dataSize = size; }
            var end = checked(start + size + (size & 1));
            if (end > stream.Length) throw new InvalidDataException("WAV chunk 尚未完成。");
            stream.Position = end;
        }
        if (declaredLength != stream.Length || stream.Position != stream.Length || rate <= 0 || dataSize <= 0 || dataSize % align != 0)
            throw new InvalidDataException("WAV 不完整、空白或缺少音訊資料。");
        return new(rate, channels, bits, dataSize / align);
    }
    public bool SameTimeline(WaveInfo other) => SampleRate == other.SampleRate && Channels == other.Channels && SampleCount == other.SampleCount;
}
