using System.Text;

namespace AudioWorkflow;

/// <summary>Validated byte layout; preserves the complete PCM fmt chunk including channel masks.</summary>
internal sealed record PcmWaveLayout(WaveInfo Info, byte[] Format, long DataOffset, long DataLength, bool IsRf64)
{
    public int BlockAlign => checked(Info.Channels * (Info.BitsPerSample / 8));

    public static PcmWaveLayout Read(Stream stream, bool allowFloatingPoint = false)
    {
        stream.Position = 0;
        var info = WaveInfo.Read(stream, allowFloatingPoint);
        if (info.BitsPerSample is not (16 or 24 or 32) && !(allowFloatingPoint && info.BitsPerSample == 64))
            throw new InvalidDataException("Timeline editing requires 16-, 24-, or 32-bit integer PCM.");
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);
        var rf64 = reader.ReadUInt32() == 0x34364652; // RF64
        var riffSize = reader.ReadUInt32();
        reader.ReadUInt32();
        if (rf64 && riffSize != uint.MaxValue) throw new InvalidDataException("Invalid RF64 RIFF size.");
        byte[]? format = null;
        long offset = -1, dataLength = 0;
        long? rfDataLength = null;
        ulong rfSamples = 0;
        while (stream.Position < stream.Length)
        {
            var tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
            long size = reader.ReadUInt32();
            var start = stream.Position;
            if (tag == "ds64" && rf64)
            {
                if (rfDataLength != null || size < 28) throw new InvalidDataException("Invalid ds64 chunk.");
                reader.ReadUInt64();
                rfDataLength = checked((long)reader.ReadUInt64());
                rfSamples = reader.ReadUInt64();
                var tableCount = reader.ReadUInt32();
                if (28L + 12L * tableCount > size) throw new InvalidDataException("Truncated ds64 table.");
            }
            if (tag == "data" && size == uint.MaxValue)
                size = rfDataLength ?? throw new InvalidDataException("Missing ds64 data size.");
            if (tag == "fmt ")
            {
                if (format != null || size > 65536) throw new InvalidDataException("Ambiguous or excessive PCM format chunk.");
                format = reader.ReadBytes(checked((int)size));
                if (BitConverter.ToUInt16(format, 0) == 0xfffe)
                {
                    var extra = BitConverter.ToUInt16(format, 16);
                    var validBits = BitConverter.ToUInt16(format, 18);
                    if (18L + extra > size || validBits == 0 || validBits > info.BitsPerSample)
                        throw new InvalidDataException("Invalid extensible PCM format.");
                }
            }
            if (tag == "data") { offset = start; dataLength = size; }
            stream.Position = checked(start + size + (size & 1));
        }
        if (format == null || offset < 0 || (rf64 && (rfDataLength != dataLength ||
            (rfSamples != 0 && rfSamples != (ulong)info.SampleCount))))
            throw new InvalidDataException("Inconsistent PCM layout.");
        return new(info, format, offset, dataLength, rf64);
    }

    public void WriteHeader(Stream stream, long samples)
    {
        var bytes = checked(samples * BlockAlign);
        var baseSize = checked(4L + 8 + Format.Length + (Format.Length & 1) + 8 + bytes + (bytes & 1));
        var rf64 = IsRf64 || baseSize >= uint.MaxValue;
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write(rf64 ? "RF64"u8 : "RIFF"u8);
        writer.Write(rf64 ? uint.MaxValue : checked((uint)baseSize));
        writer.Write("WAVE"u8);
        if (rf64)
        {
            writer.Write("ds64"u8); writer.Write(28u);
            writer.Write(checked((ulong)(baseSize + 36)));
            writer.Write((ulong)bytes); writer.Write((ulong)samples); writer.Write(0u);
        }
        writer.Write("fmt "u8); writer.Write((uint)Format.Length); writer.Write(Format);
        if ((Format.Length & 1) != 0) writer.Write((byte)0);
        writer.Write("data"u8); writer.Write(rf64 ? uint.MaxValue : checked((uint)bytes));
    }
}
