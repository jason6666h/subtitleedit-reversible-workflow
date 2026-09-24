using AudioWorkflow;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;
using System.Text.Json.Nodes;

// Dependency-free executable tests, matching the existing repository test convention.
var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
var workspace = Path.GetFullPath(Path.Combine(project, "../.."));
var root = Path.Combine(project, "runs", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var service = new AudioTimelineService(new WorkflowSettings(), workspace);
int failures = 0, passed = 0;
async Task Check(string name, Func<string, Task> action)
{
    var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try { await action(folder); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e); }
}
void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
byte[] Pattern(int count) => Enumerable.Range(0, count).Select(i => (byte)((i * 47 + i / 251) % 256)).ToArray();
void WriteWave(string path, int bits, int channels, byte[] pcm, bool rf64 = false, bool extensible = false, int rate = 1000)
{
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
    using var writer = new BinaryWriter(stream);
    int formatSize = extensible ? 40 : 16;
    long riffSize = 4 + (rf64 ? 36 : 0) + 12 + 8 + formatSize + 8L + pcm.Length + (pcm.Length & 1) + 10;
    writer.Write(rf64 ? "RF64"u8 : "RIFF"u8); writer.Write(rf64 ? uint.MaxValue : (uint)riffSize); writer.Write("WAVE"u8);
    if (rf64)
    {
        writer.Write("ds64"u8); writer.Write(28u); writer.Write((ulong)riffSize); writer.Write((ulong)pcm.Length);
        writer.Write((ulong)(pcm.Length / (channels * bits / 8))); writer.Write(0u);
    }
    writer.Write("JUNK"u8); writer.Write(3u); writer.Write(new byte[] { 9, 8, 7, 0 });
    writer.Write("fmt "u8); writer.Write(formatSize); writer.Write((ushort)(extensible ? 0xfffe : 1)); writer.Write((ushort)channels);
    writer.Write(rate); writer.Write(rate * channels * bits / 8); writer.Write((ushort)(channels * bits / 8)); writer.Write((ushort)bits);
    if (extensible)
    {
        writer.Write((ushort)22); writer.Write((ushort)bits); writer.Write(channels == 6 ? 0x3fu : 0u);
        writer.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
    }
    writer.Write("data"u8); writer.Write(rf64 ? uint.MaxValue : (uint)pcm.Length); writer.Write(pcm);
    if ((pcm.Length & 1) != 0) writer.Write((byte)0);
    writer.Write("LIST"u8); writer.Write(2u); writer.Write((ushort)7);
}
byte[] ReadPcm(string path)
{
    using var stream = File.OpenRead(path);
    var layout = PcmWaveLayout.Read(stream);
    stream.Position = layout.DataOffset;
    var bytes = new byte[checked((int)layout.DataLength)];
    stream.ReadExactly(bytes);
    return bytes;
}

await Check("subtitle punctuation normalization matches portable rules and preserves tags", folder =>
{
    var normalized = SubtitlePunctuationNormalizer.NormalizeText(
        "是不是呢? 好!\t文字1   文字2,文字3.甲:乙;丙(丁)[戊]");
    Assert(normalized == "是不是呢  ？  好  ！  文字1  文字2，文字3。甲：乙；丙（丁）【戊】", normalized);
    Assert(SubtitlePunctuationNormalizer.NormalizeText("價格 1,000 圓周率 3.14 時間 12:30") ==
           "價格  1,000  圓周率  3.14  時間  12:30");
    Assert(SubtitlePunctuationNormalizer.NormalizeText("<i>真的?</i> {\\an8}位置(上)") ==
           "<i>真的  ？  </i>  {\\an8}位置（上）");
    Assert(SubtitlePunctuationNormalizer.NormalizeText("甲。\r\n乙．\r丙?") == "甲。\n乙。\n丙  ？");
    Assert(SubtitlePunctuationNormalizer.NormalizeText("?甲!!") == "？  甲  ！！");
    Assert(SubtitlePunctuationNormalizer.NormalizeText("   ") == string.Empty);
    Assert(SubtitlePunctuationNormalizer.NormalizeText("。") == "。");
    Assert(SubtitlePunctuationNormalizer.NormalizeText(normalized) == normalized, "Normalization must be idempotent");
    return Task.CompletedTask;
});

await Check("Buffered hashes match SHA256 for short, boundary and partial ranges", async folder =>
{
    foreach (var size in new[] { 0, 1, 1048575, 1048576, 1048613, 2097189 })
    {
        var bytes = Pattern(size);
        using var stream = new MemoryStream(bytes);
        var expected = Convert.ToHexString(SHA256.HashData(bytes));
        Assert(AudioContentHash.Compute(stream) == expected);
        stream.Position = 0;
        Assert(await AudioContentHash.ComputeAsync(stream) == expected);
        foreach (var (offset, count) in new[] { (0, size), (size / 3, size / 2), (size, 0) })
        {
            var hashes = await AudioContentHash.ComputeWithRangeAsync(stream, offset, count);
            Assert(hashes.FileHash == expected);
            Assert(hashes.RangeHash == Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, count))));
        }
    }
});
await Check("Buffered hash cancellation and invalid ranges are rejected", async folder =>
{
    using var stream = new MemoryStream(Pattern(100));
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    await Throws<OperationCanceledException>(() => AudioContentHash.ComputeAsync(stream, cancel.Token));
    await Throws<OperationCanceledException>(() => AudioContentHash.ComputeWithRangeAsync(stream, 0, 100, cancel.Token));
    await Throws<ArgumentOutOfRangeException>(() => AudioContentHash.ComputeWithRangeAsync(stream, 99, 2));
    await Throws<ArgumentOutOfRangeException>(() => AudioContentHash.ComputeWithRangeAsync(stream, -1, 1));
});

foreach (var bits in new[] { 16, 24, 32 })
foreach (var channels in new[] { 1, 2, 6 })
foreach (var rf64 in new[] { false, true })
await Check($"exact {bits}-bit {channels}ch {(rf64 ? "RF64" : "RIFF")} PCM + extensible fmt + padding", async folder =>
{
    var source = Path.Combine(folder, "來源 original.wav");
    var align = bits / 8 * channels;
    var bytes = Pattern(101 * align);
    WriteWave(source, bits, channels, bytes, rf64, channels == 6);
    var originalHash = Hash(source); var originalTime = File.GetLastWriteTimeUtc(source);
    var cut = await service.CutAsync(source, folder, 0.0105, 0.0305, CancellationToken.None);
    Assert(cut.StartSample == 11 && cut.EndSample == 31 && cut.RemovedSamples == 20);
    Assert(cut.StartSeconds == 0.011 && cut.EndSeconds == 0.031);
    Assert(cut.OriginalSamples == 101 && cut.OutputSamples == 81 && cut.SampleRate == 1000);
    Assert(cut.Channels == channels && cut.BitsPerSample == bits);
    Assert(ReadPcm(cut.OutputPath).SequenceEqual(bytes.Take(11 * align).Concat(bytes.Skip(31 * align))), "Retained PCM changed");
    Assert(cut.SourceSha256 == originalHash && Hash(source) == originalHash && File.GetLastWriteTimeUtc(source) == originalTime);
    Assert(cut.BaselinePath == source && cut.BaselineSha256 == originalHash && cut.OutputSha256 == Hash(cut.OutputPath));
    using var output = File.OpenRead(cut.OutputPath);
    Assert(PcmWaveLayout.Read(output).IsRf64 == rf64);
    await service.ValidateAsync(cut, CancellationToken.None);

    // Exercise retained-prefix/suffix copying across multiple 128 KiB reads on both sides
    // without creating another test count. Exact PCM and the independent SHA-256 must still match.
    if (bits == 24 && channels == 2 && !rf64)
    {
        const int copyChunkSize = 128 * 1024;
        var largeSource = Path.Combine(folder, "multi-read-copy-boundary.wav");
        var retainedFramesPerSide = copyChunkSize / align + 2048;
        const int removedFrames = 2000;
        var largeBytes = Pattern((2 * retainedFramesPerSide + removedFrames) * align);
        WriteWave(largeSource, bits, channels, largeBytes);
        var largeCut = await service.CutAsync(largeSource, folder,
            retainedFramesPerSide / 1000d, (retainedFramesPerSide + removedFrames) / 1000d,
            CancellationToken.None);
        Assert(largeCut.StartSample * align > copyChunkSize &&
               (largeCut.OriginalSamples - largeCut.EndSample) * align > copyChunkSize,
            "PCM copy fixture must span multiple reads on both retained ranges");
        var expected = largeBytes.Take((int)largeCut.StartSample * align)
            .Concat(largeBytes.Skip((int)largeCut.EndSample * align));
        Assert(ReadPcm(largeCut.OutputPath).SequenceEqual(expected),
            "Multi-read PCM copy changed retained bytes");
        Assert(largeCut.OutputSha256 == Hash(largeCut.OutputPath),
            "Independent output SHA-256 changed after multi-read copy");
        await service.ValidateAsync(largeCut, CancellationToken.None);
    }
});

await Check("prefix/suffix clamping, one-sample remainder, and chained undo versions", async folder =>
{
    var source = Path.Combine(folder, "original.wav"); var bytes = Pattern(200);
    WriteWave(source, 16, 1, bytes);
    var prefix = await service.CutAsync(source, folder, -0.0005, 0.050, CancellationToken.None);
    Assert(prefix.StartSample == 0 && ReadPcm(prefix.OutputPath).SequenceEqual(bytes.Skip(100)));
    var suffix = await service.CutAsync(source, folder, 0.050, 0.1005, CancellationToken.None);
    Assert(suffix.EndSample == 100 && ReadPcm(suffix.OutputPath).SequenceEqual(bytes.Take(100)));
    var next = await service.CutAsync(prefix.OutputPath, folder, 0, 0.049, CancellationToken.None);
    Assert(next.OutputSamples == 1 && ReadPcm(next.OutputPath).SequenceEqual(bytes.Skip(198)));
    await service.ValidateAsync(prefix, CancellationToken.None);
    await service.ValidateAsync(suffix, CancellationToken.None);
    await service.ValidateAsync(next, CancellationToken.None);
});

await Check("invalid intervals and whole-file deletion never publish or change source", async folder =>
{
    var source = Path.Combine(folder, "original.wav"); WriteWave(source, 16, 1, Pattern(200)); var original = Hash(source);
    foreach (var (start, end) in new (double, double)[] {
        (double.NaN, 0.01), (0, double.PositiveInfinity), (0.01, 0.01), (0.02, 0.01),
        (0, 0.1), (-0.0005, 0.1005), (-0.0006, 0.01), (0.01, 0.1006), (0.01, 1e100),
        (0.01001, 0.01002), (0.1001, 0.1002) })
        await Throws<ArgumentOutOfRangeException>(() => service.CutAsync(source, folder, start, end, CancellationToken.None));
    Assert(Directory.GetFiles(folder).Length == 1 && Hash(source) == original);
});

await Check("parallel cuts use unique destinations and preserve all versions", async folder =>
{
    var source = Path.Combine(folder, "original.wav"); WriteWave(source, 24, 2, Pattern(600));
    var cuts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.CutAsync(source, folder, .01, .02, CancellationToken.None)));
    Assert(cuts.Select(x => x.OutputPath).Distinct().Count() == 8);
    Assert(cuts.Select(x => x.OutputSha256).Distinct().Count() == 1);
    foreach (var cut in cuts) await service.ValidateAsync(cut, CancellationToken.None);
    Assert(Directory.GetFiles(folder).Length == 9);
});

await Check("source and output mutation detected before commit (including same-size mutations)", async folder =>
{
    var source = Path.Combine(folder, "original.wav"); WriteWave(source, 16, 1, Pattern(200));
    var cut = await service.CutAsync(source, folder, .01, .02, CancellationToken.None);
    var saved = File.ReadAllBytes(source);
    var changed = saved.ToArray(); changed[^12] ^= 1; File.WriteAllBytes(source, changed);
    await Throws<IOException>(() => service.ValidateAsync(cut, CancellationToken.None));
    File.WriteAllBytes(source, saved);
    changed = File.ReadAllBytes(cut.OutputPath); changed[^2] ^= 1; File.WriteAllBytes(cut.OutputPath, changed);
    await Throws<IOException>(() => service.ValidateAsync(cut, CancellationToken.None));
    Assert(File.Exists(source) && File.Exists(cut.OutputPath));
});

await Check("truncated, float, misaligned, inconsistent RF64 and invalid extensible WAV rejected", async folder =>
{
    foreach (var mode in new[] { "truncated", "float", "align", "rf64Samples", "validBits" })
    {
        var source = Path.Combine(folder, mode + ".wav");
        WriteWave(source, 24, 1, Pattern(300), mode == "rf64Samples", mode == "validBits");
        var file = File.ReadAllBytes(source);
        if (mode == "truncated") file = file[..^1];
        if (mode == "float") file[32] = 3; // fmt payload after RIFF and JUNK
        if (mode == "align") file[44] = 2;
        if (mode == "rf64Samples") file[36] = 99;
        if (mode == "validBits") file[50] = 25;
        File.WriteAllBytes(source, file); var hash = Hash(source);
        await Throws<InvalidDataException>(() => service.CutAsync(source, folder, .01, .02, CancellationToken.None));
        Assert(Hash(source) == hash);
    }
    Assert(Directory.GetFiles(folder).Length == 5);
});

await Check("pre-cancel creates nothing; cancellation during copy removes only owned partial", async folder =>
{
    var source = Path.Combine(folder, "original.wav"); WriteWave(source, 16, 1, Pattern(200));
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    var absent = Path.Combine(folder, "not-created");
    await Throws<OperationCanceledException>(() => service.CutAsync(source, absent, .01, .02, cancelled.Token));
    Assert(!Directory.Exists(absent));
    var large = Path.Combine(folder, "large.wav");
    WriteWave(large, 16, 1, new byte[64 * 1024 * 1024]); var before = Hash(large);
    var output = Path.Combine(folder, "output"); Directory.CreateDirectory(output);
    var sentinel = Path.Combine(output, "older.partial.wav"); File.WriteAllText(sentinel, "user data");
    using var cts = new CancellationTokenSource();
    using var watcher = new FileSystemWatcher(output, "cut-*.partial.wav");
    watcher.Created += (_, _) => cts.Cancel(); watcher.EnableRaisingEvents = true;
    await Throws<OperationCanceledException>(() => service.CutAsync(large, output, .01, .02, cts.Token));
    Assert(cts.IsCancellationRequested && Hash(large) == before);
    Assert(Directory.GetFiles(output).SequenceEqual(new[] { sentinel }) && File.ReadAllText(sentinel) == "user data");
});

foreach (var (fixture, rate, channels) in new[] { ("sine-22050-1.mp3", 22050, 1), ("sine-44100-1.mp3", 44100, 1), ("sine-48000-2.mp3", 48000, 2) })
await Check("bundled FFmpeg MP3 baseline exact PCM cut: " + fixture, async folder =>
{
    var source = Path.Combine(folder, "中文 " + fixture); File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), source);
    var before = Hash(source); var time = File.GetLastWriteTimeUtc(source);
    var cut = await service.CutAsync(source, folder, 0.20001, 0.60001, CancellationToken.None);
    Assert(Path.GetExtension(cut.OutputPath) == ".wav" && cut.OutputPath != source);
    Assert(cut.SampleRate == rate && cut.Channels == channels && cut.BitsPerSample == 24 && cut.OriginalSamples == rate);
    Assert(cut.SourceSha256 == before && Hash(source) == before && File.GetLastWriteTimeUtc(source) == time);
    Assert(cut.BaselinePath != source && cut.BaselineSha256 == Hash(cut.BaselinePath));
    var baseline = ReadPcm(cut.BaselinePath); var align = channels * 3;
    var expected = baseline.Take((int)cut.StartSample * align).Concat(baseline.Skip((int)cut.EndSample * align));
    Assert(ReadPcm(cut.OutputPath).SequenceEqual(expected));
    await service.ValidateAsync(cut, CancellationToken.None);
    var second = await service.CutAsync(source, folder, .2, .6, CancellationToken.None);
    Assert(second.BaselinePath != cut.BaselinePath && File.Exists(cut.BaselinePath) && File.Exists(cut.OutputPath));
    var changed = File.ReadAllBytes(cut.BaselinePath); changed[^2] ^= 1; File.WriteAllBytes(cut.BaselinePath, changed);
    await Throws<IOException>(() => service.ValidateAsync(cut, CancellationToken.None));
});

await Check("RF64 >4 GiB layout uses 64-bit offsets/counts; large RIFF output upgrades to RF64", folder =>
{
    var source = Path.Combine(folder, "small.wav"); WriteWave(source, 32, 2, Pattern(800));
    using var small = File.OpenRead(source); var layout = PcmWaveLayout.Read(small);
    const long samples = 600_000_000;
    using var header = new MemoryStream(); layout.WriteHeader(header, samples);
    var headerBytes = header.ToArray(); Assert(Encoding.ASCII.GetString(headerBytes, 0, 4) == "RF64");
    using var virtualFile = new HeaderAndZerosStream(headerBytes, samples * 8);
    var large = PcmWaveLayout.Read(virtualFile);
    Assert(large.Info.SampleCount == samples && large.DataLength == 4_800_000_000L && large.DataOffset == headerBytes.Length);
    Assert(large.IsRf64 && large.Info.DurationSeconds == 600_000);
    using var shortenedHeader = new MemoryStream(); large.WriteHeader(shortenedHeader, 10);
    using var shortened = new HeaderAndZerosStream(shortenedHeader.ToArray(), 80);
    Assert(PcmWaveLayout.Read(shortened).Info.SampleCount == 10);
    return Task.CompletedTask;
});

await Check("validation leases deny file writes/replacement through commit and release afterward", async folder =>
{
    var source = Path.Combine(folder, "source.wav"); WriteWave(source, 16, 1, Pattern(200));
    var cut = await service.CutAsync(source, folder, .01, .02, CancellationToken.None);
    using (var lease = await service.AcquireValidationAsync(cut, CancellationToken.None))
    {
        foreach (var file in new[] { cut.SourcePath, cut.OutputPath })
        {
            await Throws<IOException>(() => { using var writer = File.OpenWrite(file); return Task.CompletedTask; });
            await Throws<IOException>(() => { File.Move(file, file + ".moved"); return Task.CompletedTask; });
        }
    }
    using (var writer = File.OpenWrite(cut.OutputPath)) Assert(writer.CanWrite);
    using (var lease = await AudioTimelineService.AcquireFileValidationAsync(source, cut.SourceSha256, CancellationToken.None))
        await Throws<IOException>(() => { File.Delete(source); return Task.CompletedTask; });
    await Throws<IOException>(() => AudioTimelineService.AcquireFileValidationAsync(source, new string('0', 64), CancellationToken.None));
    using (var writer = File.OpenWrite(source)) Assert(writer.CanWrite, "Failed validation leaked read guard");
});

await Check("validated cut reuses pinned source and returns one pinned verified output", async folder =>
{
    var source = Path.Combine(folder, "validated-source.wav");
    var bytes = Pattern(2000 * 2);
    WriteWave(source, 16, 2, bytes);
    using var sourceValidation = await AudioRevisionSource.OpenAndHashAsync(source, CancellationToken.None);
    using var prepared = await service.CutValidatedAsync(sourceValidation, folder, .2, .6,
        CancellationToken.None);
    var cut = prepared.Cut;
    Assert(cut.SourceSha256 == sourceValidation.Sha256);
    Assert(cut.OutputSha256 == Hash(cut.OutputPath));
    Assert(ReadPcm(cut.OutputPath).SequenceEqual(bytes.Take(200 * 4).Concat(bytes.Skip(600 * 4))),
        "Validated cut PCM changed");
    await Throws<IOException>(() => { using var writer = File.OpenWrite(source); return Task.CompletedTask; });
    await Throws<IOException>(() => { using var writer = File.OpenWrite(cut.OutputPath); return Task.CompletedTask; });
    using var outputValidation = prepared.DetachValidation();
    Assert(outputValidation.Matches(cut.OutputPath, cut.OutputSha256));
});

await Check("atomic PCM pair export includes hashes, portable checkpoint, native host state and Unicode subtitles", async folder =>
{
    var source = Path.Combine(folder, "source.wav"); WriteWave(source, 24, 2, Pattern(600));
    var parent = Path.Combine(folder, "exports");
    const string subtitles = "1\r\n00:00:00,001 --> 00:00:00,099\r\n中文 😀\r\n";
    var request = new AudioExportRequest(source, parent, subtitles, ".srt", Hash(source), "{\"SessionId\":\"host-1\",\"Bookmarks\":[\"check\"]}", "測試 FINAL");
    var result = await service.ExportPairAsync(request, CancellationToken.None);
    Assert(File.ReadAllText(result.SubtitlePath) == subtitles && Hash(result.AudioPath) == Hash(source));
    Assert(result.AudioSha256 == Hash(result.AudioPath) && result.SubtitleSha256 == Hash(result.SubtitlePath));
    Assert(result.SampleCount == 100 && result.SampleRate == 1000 && result.DurationSeconds == .1);
    Assert(Directory.GetFiles(result.DirectoryPath).Length == 4 && Directory.GetFiles(parent).Length == 0);
    Assert(!Directory.GetDirectories(parent).Any(p => p.EndsWith(".pending")));
    using var checkpoint = JsonDocument.Parse(File.ReadAllText(result.CheckpointPath));
    Assert(checkpoint.RootElement.GetProperty("AudioFile").GetString() == Path.GetFileName(result.AudioPath));
    Assert(checkpoint.RootElement.GetProperty("HostCheckpoint").GetProperty("SessionId").GetString() == "host-1");
    using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
    Assert(report.RootElement.GetProperty("CheckpointSha256").GetString() == Hash(result.CheckpointPath));
    var second = await service.ExportPairAsync(request, CancellationToken.None);
    Assert(second.DirectoryPath != result.DirectoryPath && File.Exists(result.AudioPath));
});

await Check("export MP3 baseline restore decodes to actual PCM WAV and records decoded duration", async folder =>
{
    var source = Path.Combine(folder, "restored.mp3");
    File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sine-44100-1.mp3"), source);
    var before = Hash(source);
    var result = await service.ExportPairAsync(new(source, folder, "", ".ass", before), CancellationToken.None);
    var wave = WaveInfo.Read(result.AudioPath);
    Assert(wave.SampleCount == 44100 && wave.BitsPerSample == 24 && result.DurationSeconds == 1);
    Assert(Hash(source) == before && result.SourceSha256 == before && result.AudioSha256 != before);
    using var report = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
    Assert(report.RootElement.GetProperty("DecodedFromMp3").GetBoolean());
});

await Check("bad export hash/path/JSON, pre-cancel and in-copy cancellation never publish a pair", async folder =>
{
    var source = Path.Combine(folder, "source.wav"); WriteWave(source, 16, 1, Pattern(200));
    var parent = Path.Combine(folder, "exports");
    var request = new AudioExportRequest(source, parent, "", ".srt", Hash(source));
    await Throws<IOException>(() => service.ExportPairAsync(request with { ExpectedSourceSha256 = new string('0', 64) }, CancellationToken.None));
    foreach (var name in new[] { "../escape", "CON", "bad:name", "ending.", "" })
        await Throws<ArgumentException>(() => service.ExportPairAsync(request with { BaseName = name }, CancellationToken.None));
    foreach (var ext in new[] { ".wav", "srt", ".srt/../../evil", "" })
        await Throws<ArgumentException>(() => service.ExportPairAsync(request with { SubtitleExtension = ext }, CancellationToken.None));
    await Throws<JsonException>(() => service.ExportPairAsync(request with { HostCheckpointJson = "{" }, CancellationToken.None));
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    await Throws<OperationCanceledException>(() => service.ExportPairAsync(request, cancelled.Token));
    Assert(!Directory.Exists(parent));
    Directory.CreateDirectory(parent);
    var sentinel = Path.Combine(parent, "older-pair"); Directory.CreateDirectory(sentinel);
    File.WriteAllText(Path.Combine(sentinel, "original.txt"), "untouched");
    var large = Path.Combine(folder, "large.wav"); WriteWave(large, 16, 1, new byte[64 * 1024 * 1024]);
    using var cts = new CancellationTokenSource();
    using var watcher = new FileSystemWatcher(parent, "*.wav") { IncludeSubdirectories = true };
    watcher.Created += (_, _) => cts.Cancel(); watcher.EnableRaisingEvents = true;
    await Throws<OperationCanceledException>(() => service.ExportPairAsync(request with { SourcePath = large, ExpectedSourceSha256 = Hash(large) }, cts.Token));
    Assert(Directory.GetDirectories(parent).Where(p => !p.EndsWith(".pending")).SequenceEqual(new[] { sentinel }));
    Assert(File.ReadAllText(Path.Combine(sentinel, "original.txt")) == "untouched");
    Assert(Directory.GetFiles(parent).Length == 0, "Leaked reservation file");
});

await Check("portable save/load survives relocation/missing source and holds restore guards", async folder =>
{
    var source = Path.Combine(folder, "source.wav"); WriteWave(source, 16, 1, Pattern(200));
    const string text = "1\n00:00:00,000 --> 00:00:00,100\n字幕\n";
    var saved = await service.ExportPairAsync(new(source, folder, text, ".srt", Hash(source), "{\"Bookmarks\":[\"review\"]}"), CancellationToken.None);
    File.Move(source, source + ".moved");
    var relocated = Path.Combine(folder, "relocated"); Directory.Move(saved.DirectoryPath, relocated);
    string restoredAudio;
    using (var loaded = await service.LoadExportPairAsync(relocated, CancellationToken.None))
    {
        Assert(loaded.SubtitleText == text && loaded.Result.DurationSeconds == .1);
        Assert(loaded.HostCheckpointJson!.Contains("review"));
        restoredAudio = loaded.Result.AudioPath;
        Assert(Path.GetDirectoryName(restoredAudio) == relocated && Hash(restoredAudio) == saved.AudioSha256);
        await Throws<IOException>(() => { using var writer = File.OpenWrite(restoredAudio); return Task.CompletedTask; });
    }
    using (var writer = File.OpenWrite(restoredAudio)) Assert(writer.CanWrite);
});

await Check("session rejects corrupted text/checkpoint/audio, traversal, unsupported versions and pending directories", async folder =>
{
    var source = Path.Combine(folder, "source.wav"); WriteWave(source, 16, 1, Pattern(200));
    var saved = await service.ExportPairAsync(new(source, folder, "subtitle", ".srt", Hash(source)), CancellationToken.None);
    var originalSubtitle = File.ReadAllBytes(saved.SubtitlePath);
    File.WriteAllText(saved.SubtitlePath, "tampered");
    await Throws<InvalidDataException>(() => service.LoadExportPairAsync(saved.DirectoryPath, CancellationToken.None));
    File.WriteAllBytes(saved.SubtitlePath, originalSubtitle);
    var originalAudio = File.ReadAllBytes(saved.AudioPath);
    var changedAudio = originalAudio.ToArray(); changedAudio[^12] ^= 1; File.WriteAllBytes(saved.AudioPath, changedAudio);
    await Throws<InvalidDataException>(() => service.LoadExportPairAsync(saved.DirectoryPath, CancellationToken.None));
    File.WriteAllBytes(saved.AudioPath, originalAudio);
    var originalCheckpoint = File.ReadAllText(saved.CheckpointPath);
    var originalReport = File.ReadAllText(saved.ReportPath);
    foreach (var mode in new[] { "hash", "version", "traversal" })
    {
        var checkpoint = JsonNode.Parse(originalCheckpoint)!;
        if (mode == "version") checkpoint["SchemaVersion"] = 99;
        else if (mode == "traversal") checkpoint["AudioFile"] = "../source.wav";
        else checkpoint["SourcePath"] = "changed";
        File.WriteAllText(saved.CheckpointPath, checkpoint.ToJsonString());
        if (mode != "hash")
        {
            var report = JsonNode.Parse(originalReport)!;
            report["CheckpointSha256"] = Hash(saved.CheckpointPath);
            File.WriteAllText(saved.ReportPath, report.ToJsonString());
        }
        await Throws<InvalidDataException>(() => service.LoadExportPairAsync(saved.DirectoryPath, CancellationToken.None));
        File.WriteAllText(saved.CheckpointPath, originalCheckpoint); File.WriteAllText(saved.ReportPath, originalReport);
    }
    using (var loaded = await service.LoadExportPairAsync(saved.DirectoryPath, CancellationToken.None)) Assert(loaded.SubtitleText == "subtitle");
    var pending = saved.DirectoryPath + ".pending"; Directory.Move(saved.DirectoryPath, pending);
    await Throws<InvalidDataException>(() => service.LoadExportPairAsync(pending, CancellationToken.None));
});

if (args.Contains("--large-rf64"))
await Check("physical sparse >4 GiB RF64 cut seeks and preserves exact tail frames", async folder =>
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sparse physical test requires Windows/NTFS.");
    var smallPath = Path.Combine(folder, "format.wav"); WriteWave(smallPath, 32, 2, Pattern(800));
    using var small = File.OpenRead(smallPath); var layout = PcmWaveLayout.Read(small);
    const long samples = 540_000_000;
    var source = Path.Combine(folder, "long-source.wav");
    var head = Pattern(80); var tail = Pattern(160).Skip(80).ToArray();
    using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    {
        if (!NativeSparse.MarkSparse(stream.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("FSCTL_SET_SPARSE failed: " + Marshal.GetLastWin32Error());
        layout.WriteHeader(stream, samples); var offset = stream.Position;
        stream.Write(head); stream.Position = offset + samples * 8 - tail.Length; stream.Write(tail);
    }
    var cut = await service.CutAsync(source, folder, .01, (samples - 10) / 1000d, CancellationToken.None);
    Assert(cut.OriginalSamples == samples && cut.OutputSamples == 20 && cut.SourceLength > uint.MaxValue);
    Assert(ReadPcm(cut.OutputPath).SequenceEqual(head.Concat(tail)));
    await service.ValidateAsync(cut, CancellationToken.None);
});

await Check("timeline repeated cuts retain exact original coordinates", async folder =>
{
    var first = TimelineMap.Delete(new[] { new SourceSpan(0, 8000) }, 2000, 4000);
    Assert(first.SequenceEqual(new[] { new SourceSpan(0, 2000), new SourceSpan(4000, 8000) }));
    var second = TimelineMap.Delete(first, 1000, 3000);
    Assert(second.SequenceEqual(new[] { new SourceSpan(0, 1000), new SourceSpan(5000, 8000) }));
    Assert(TimelineMap.Validate(second) == 4000);
    Assert(TimelineMap.ToOriginal(second, 1000) == 5000);
    Assert(TimelineMap.ToEdited(second, 3000) == 1000);
    Assert(TimelineMap.ToOriginal(second, 4000) == 8000);
    await Throws<InvalidDataException>(() => Task.Run(() => TimelineMap.Delete(first, 0, 6000)));
});

await Check("append-only SRT snapshots reject audio and subtitle tampering", async folder =>
{
    var audio = Path.Combine(folder, "source.wav");
    WriteWave(audio, 16, 1, Pattern(2000));
    var originalHash = Hash(audio);
    var rootDir = Path.Combine(folder, "revisions");
    var a = AudioRevisionStore.Save(rootDir, null, "original", audio, originalHash, "old SRT", "{}");
    var oldBytes = File.ReadAllBytes(Path.Combine(a, "subtitles.srt"));
    var b = AudioRevisionStore.Save(rootDir, Path.GetFileName(a), "timing", audio, originalHash, "new SRT", "{}");
    Assert(a != b && oldBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(a, "subtitles.srt"))));
    Assert(AudioRevisionStore.Validate(b).ParentId == Path.GetFileName(a));
    File.AppendAllText(Path.Combine(b, "subtitles.srt"), "tamper");
    await Throws<InvalidDataException>(() => Task.Run(() => AudioRevisionStore.Validate(b)));
    File.AppendAllText(audio, "tamper");
    await Throws<InvalidDataException>(() => Task.Run(() => AudioRevisionStore.Save(rootDir, null, "bad", audio, originalHash, "bad", "{}")));
});

await Check("local repair independent outside-byte validation detects corruption", async folder =>
{
    var source = Path.Combine(folder, "before.wav");
    var after = Path.Combine(folder, "after.wav");
    WriteWave(source, 24, 2, Pattern(6000));
    File.Copy(source, after);
    var region = LocalAudioRepair.Select(WaveInfo.Read(source), 0.2, 0.4);
    await LocalAudioRepair.VerifyOutsideAsync(source, after, region, CancellationToken.None);
    using (var stream = new FileStream(after, FileMode.Open, FileAccess.ReadWrite))
    {
        var layout = PcmWaveLayout.Read(stream);
        stream.Position = layout.DataOffset;
        var value = stream.ReadByte();
        stream.Position--;
        stream.WriteByte((byte)(value ^ 1));
    }
    await Throws<InvalidDataException>(() => LocalAudioRepair.VerifyOutsideAsync(source, after, region, CancellationToken.None));
});

await Check("MP3 export validates subtitle bounds against decoded PCM before publication", async folder =>
{
    var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sine-44100-1.mp3");
    var request = new AudioExportRequest(source, folder, "snapshot", ".srt", Hash(source),
        SubtitleTimeline: new[] { new SubtitleInterval(0, 30) });
    await Throws<InvalidDataException>(() => service.ExportPairAsync(request, CancellationToken.None));
    Assert(!Directory.EnumerateDirectories(folder).Any(p => !Path.GetFileName(p).EndsWith(".pending")), "Invalid pair published");
    var good = await service.ExportPairAsync(request with { SubtitleTimeline = new[] { new SubtitleInterval(0, 0.5) } }, CancellationToken.None);
    using var report = JsonDocument.Parse(File.ReadAllText(good.ReportPath));
    Assert(report.RootElement.GetProperty("SubtitleTimelineValidated").GetBoolean());
    Assert(!report.RootElement.GetProperty("SemanticAlignmentVerified").GetBoolean());
});

await Check("pair export rejects mismatched map and invalid subtitle intervals", async folder =>
{
    var source = Path.Combine(folder, "source.wav");
    WriteWave(source, 16, 1, Pattern(2000));
    var request = new AudioExportRequest(source, Path.Combine(folder, "out"), "snapshot", ".srt", Hash(source),
        SubtitleTimeline: new[] { new SubtitleInterval(0, 0.5) }, ExpectedSampleCount: 999, ExpectedSampleRate: 1000);
    await Throws<InvalidDataException>(() => service.ExportPairAsync(request, CancellationToken.None));
    foreach (var interval in new[] { new SubtitleInterval(-1, 1), new SubtitleInterval(1, 1), new SubtitleInterval(double.NaN, 1) })
        await Throws<InvalidDataException>(() => service.ExportPairAsync(request with { SubtitleTimeline = new[] { interval } }, CancellationToken.None));
});

await Check("revision guard pins audio and verified checkpoint rejects corrupted history", async folder =>
{
    var source = Path.Combine(folder, "source.wav");
    WriteWave(source, 16, 1, Pattern(2000));
    var hash = Hash(source);
    using var guard = new AudioRevisionSource(source, hash);
    await Throws<IOException>(() => Task.Run(() => { using var writer = File.OpenWrite(source); }));
    var revision = AudioRevisionStore.Save(Path.Combine(folder, "revisions"), null, "original", source, hash, "text", "{\"test\":1}", guard);
    Assert(AudioRevisionStore.ReadVerifiedCheckpoint(revision) == "{\"test\":1}");
    File.AppendAllText(Path.Combine(revision, "state.syncaudio.json"), " ");
    await Throws<InvalidDataException>(() => Task.Run(() => AudioRevisionStore.ReadVerifiedCheckpoint(revision)));
    guard.Dispose();
    await Throws<InvalidDataException>(() => Task.Run(() => AudioRevisionStore.Save(folder, null, "bad", source, hash, "text", "{}", guard)));
});

await Check("validated AU local handoff reuses baseline and returns one pinned verified output", async folder =>
{
    var source = Path.Combine(folder, "managed.wav");
    WriteWave(source, 24, 2, Pattern(12000 * 2 * 3), rate: 1000);
    using var baseline = await AudioRevisionSource.OpenAsync(source, Hash(source), CancellationToken.None);
    var handoff = await new AuditionHandoffService(new(), workspace).CreateLocalFromValidatedAsync(
        baseline, folder, 3, 4, CancellationToken.None);
    Assert(handoff.BeforePath == source, "Managed baseline was copied instead of reused");
    Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(handoff.EditPath)!, "before.wav")),
        "Fast handoff created a redundant whole-file baseline");
    await Throws<IOException>(() => Task.Run(() => { using var writer = File.OpenWrite(source); }));
    using (var edit = File.Open(handoff.EditPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var layout = PcmWaveLayout.Read(edit);
        edit.Position = layout.DataOffset + handoff.Region!.LocalStartSample * layout.BlockAlign;
        edit.Write(new byte[layout.BlockAlign]);
    }
    using var returned = await new AuditionHandoffService(new(), workspace).CaptureVerifiedReturnAsync(
        handoff, baseline, CancellationToken.None);
    Assert(returned.Sha256 == Hash(returned.Path), "Returned hash does not identify the verified output");
    await Throws<IOException>(() => Task.Run(() => { using var writer = File.OpenWrite(returned.Path); }));
    await LocalAudioRepair.VerifyOutsideAsync(source, returned.Path, handoff.Region!, CancellationToken.None);
});

foreach(var variant in new[] { "int16", "int32", "float32", "float64", "stereo-mask" })
await Check("AU representation-only return normalizes safely: " + variant, async folder =>
{
    var original = Path.Combine(folder,"original.wav");
    var channels = variant == "stereo-mask" ? 2 : 1;
    WriteWave(original,24,channels,new byte[10000*channels*3]);
    var handoff = await new AuditionHandoffService(new(),workspace).CreateLocalAsync(original,folder,3,4,CancellationToken.None);
    var frames=(int)handoff.Region!.ClipSamples;
    var edited=Path.Combine(folder,"au-save.wav");
    if(variant.StartsWith("float"))
    {
        var width=variant=="float32"?4:8;
        using var writer=new BinaryWriter(File.Create(edited));
        writer.Write("RIFF"u8);writer.Write(36+frames*width);writer.Write("WAVEfmt "u8);writer.Write(16);
        writer.Write((ushort)3);writer.Write((ushort)1);writer.Write(1000);writer.Write(1000*width);writer.Write((ushort)width);writer.Write((ushort)(width*8));
        writer.Write("data"u8);writer.Write(frames*width);
        for(int i=0;i<frames;i++) { if(width==4)writer.Write(0.25f);else writer.Write(0.25d); }
    }
    else if(variant=="stereo-mask") WriteWave(edited,24,channels,new byte[frames*channels*3],extensible:true);
    else {var bits=variant=="int16"?16:32; WriteWave(edited,bits,channels,new byte[frames*channels*(bits/8)]);}
    File.Copy(edited,handoff.EditPath,true);
    var auHash=Hash(handoff.EditPath);
    var output=await new AuditionHandoffService(new(),workspace).CaptureReturnAsync(handoff,CancellationToken.None);
    Assert(WaveInfo.Read(output)==WaveInfo.Read(original));
    await LocalAudioRepair.VerifyOutsideAsync(handoff.BeforePath,output,handoff.Region,CancellationToken.None);
    Assert(Hash(handoff.EditPath)==auHash,"AU editable file was modified");
    if(variant.StartsWith("float")) Assert(ReadPcm(output).Any(b=>b!=0),"Repaired PCM was not imported");
});
foreach (var bits in new[] { 16, 24, 32 })
await Check("audit chained cuts AU return and lossless WAV export " + bits, async folder =>
{
    var original = Path.Combine(folder, "source.wav");
    WriteWave(original, bits, 2, Pattern(12000 * 2 * (bits / 8)), rate: 1000);
    var originalHash = Hash(original);
    var first = await service.CutAsync(original, folder, 2, 4, default);
    var second = await service.CutAsync(first.OutputPath, folder, 5, 6, default);
    var map = TimelineMap.Delete(TimelineMap.Delete(new[] { new SourceSpan(0, 12000) }, 2000, 4000), 5000, 6000);
    Assert(TimelineMap.Validate(map) == 9000);
    Assert(WaveInfo.Read(second.OutputPath).SampleCount == 9000);
    Assert(TimelineMap.ToEdited(map, 9000) == 6000);
    var au = new AuditionHandoffService(new(), workspace);
    var handoff = await au.CreateLocalAsync(second.OutputPath, folder, 3, 4, default);
    // Simulate AU changing the interior of the selected range without changing duration/format.
    using (var edit = new FileStream(handoff.EditPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var layout = PcmWaveLayout.Read(edit);
        edit.Position = layout.DataOffset + (handoff.Region!.LocalStartSample + 500) * layout.BlockAlign;
        edit.Write(new byte[layout.BlockAlign]);
    }
    var repaired = await au.CaptureReturnAsync(handoff, default);
    await LocalAudioRepair.VerifyOutsideAsync(second.OutputPath, repaired, handoff.Region!, default);
    Assert(WaveInfo.Read(repaired) == WaveInfo.Read(second.OutputPath));
    const string srt = "1\r\n00:00:06,000 --> 00:00:07,000\r\n原始第九至十秒\r\n";
    var request = new AudioExportRequest(repaired, folder, srt, ".srt", Hash(repaired),
        SubtitleTimeline: new[] { new SubtitleInterval(6, 7) }, ExpectedSampleCount: TimelineMap.Validate(map), ExpectedSampleRate: 1000);
    var result = await service.ExportPairAsync(request, default);
    Assert(Hash(result.AudioPath) == Hash(repaired), "Export must preserve every WAV byte");
    Assert(File.ReadAllText(result.SubtitlePath) == srt);
    Assert(result.SampleCount == 9000 && result.DurationSeconds == 9);
    var firstOutputHash = Hash(result.AudioPath);
    var next = await service.ExportPairAsync(request, default);
    Assert(next.DirectoryPath != result.DirectoryPath && Hash(result.AudioPath) == firstOutputHash);
    Assert(Hash(original) == originalHash);
    using var reloaded = await service.LoadExportPairAsync(result.DirectoryPath, default);
    Assert(reloaded.SubtitleText == srt && reloaded.Result.SampleCount == 9000);
});

await Check("AU length changes remain rejected without consuming editable file",async folder=>
{
    var original=Path.Combine(folder,"original.wav"); WriteWave(original,24,1,new byte[10000*3]);
    var h=await new AuditionHandoffService(new(),workspace).CreateLocalAsync(original,folder,3,4,CancellationToken.None);
    var changed=Path.Combine(folder,"shortened.wav");WriteWave(changed,16,1,new byte[((int)h.Region!.ClipSamples-1)*2]);File.Copy(changed,h.EditPath,true);
    await Throws<InvalidDataException>(()=>new AuditionHandoffService(new(),workspace).CaptureReturnAsync(h,CancellationToken.None));
    Assert(File.Exists(h.EditPath)&&Hash(h.BeforePath)==h.BeforeHash);
});

await Check("original-axis indexed map matches canonical map across all cut boundaries",folder=>
{
    SourceSpan[] spans=[new(100,300),new(700,900),new(1000,1100)];var map=new OriginalAxisMap(spans,1000);
    for(int i=0;i<=500;i++)Assert(Math.Abs(map.ToOriginal(i/1000d)-TimelineMap.ToOriginal(spans,i)/1000d)<1e-9);
    for(int i=0;i<1200;i++)Assert(Math.Abs(map.ToEdited(i/1000d,out _)-TimelineMap.ToEdited(spans,i)/1000d)<1e-9);
    Assert(map.ToEdited(.5,out var removed)==.2 && removed);
    Assert(map.ToOriginal(.2)==.7);return Task.CompletedTask;
});
await Check("audio edit regions validate immutable original-axis annotations",folder=>
{
    var region=new AudioEditRegion("revision",AudioEditRegionKind.AuditionRepair,2.5,3.75);
    region.Validate();
    Assert(JsonSerializer.Deserialize<AudioEditRegion>(JsonSerializer.Serialize(region))==region);
    var rejected=false;
    try { new AudioEditRegion("",AudioEditRegionKind.Deleted,4,4).Validate(); }
    catch(InvalidDataException) { rejected=true; }
    Assert(rejected,"empty or reversed edit regions must be rejected");
    return Task.CompletedTask;
});
await Check("compact audio rebuild preserves cuts and AU patches without historical full WAVs",async folder=>
{
    var original=Path.Combine(folder,"baseline.wav");
    WriteWave(original,16,1,Pattern(20*2),rate:1000);
    var baseMap=new[]{new SourceSpan(0,20)};
    var first=await service.CutAsync(original,folder,.003,.005,default);
    var firstMap=TimelineMap.Delete(baseMap,3,5);
    var repaired=Path.Combine(folder,"full-repaired.wav");File.Copy(first.OutputPath,repaired);
    using(var edit=File.Open(repaired,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
    {
        var layout=PcmWaveLayout.Read(edit);edit.Position=layout.DataOffset+4*layout.BlockAlign;
        edit.Write(new byte[]{201,202,203,204});
    }
    var repairedHash=Hash(repaired);
    var patch=await AudioDeltaStore.CapturePatchAsync(repaired,repairedHash,folder,"repair",4,6,firstMap,default);
    var patches=AudioDeltaStore.ReadPatches(AudioDeltaStore.WritePatches(new[]{patch}));
    var expected=await service.CutAsync(repaired,folder,.008,.010,default);
    var targetMap=TimelineMap.Delete(firstMap,8,10);
    File.Delete(first.OutputPath);File.Delete(repaired);
    var rebuilt=Path.Combine(folder,"rebuilt.wav");
    var rebuiltHash=await AudioDeltaStore.RebuildAsync(original,Hash(original),baseMap,targetMap,patches,folder,rebuilt,default);
    Assert(rebuiltHash==expected.OutputSha256,"compact rebuild hash differs from direct edits");
    Assert(File.ReadAllBytes(rebuilt).SequenceEqual(File.ReadAllBytes(expected.OutputPath)),"compact rebuild bytes differ");
    var audit=await AudioDeltaStore.VerifyAsync(original,Hash(original),baseMap,targetMap,patches,folder,
        rebuilt,rebuiltHash,default);
    Assert(audit.ComparedFrames==16&&audit.DeletedFrames==4&&audit.RepairedFrames==2&&audit.RepairPatchCount==1,
        "streaming audit reported the wrong operation coverage");
    using(var edit=File.Open(rebuilt,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
    {
        var layout=PcmWaveLayout.Read(edit);edit.Position=layout.DataOffset+2;edit.WriteByte(255);
    }
    var forgedHash=Hash(rebuilt);
    await Throws<InvalidDataException>(()=>AudioDeltaStore.VerifyAsync(original,Hash(original),baseMap,targetMap,
        patches,folder,rebuilt,forgedHash,default));
    Assert(Directory.GetFiles(folder,"*.wav",SearchOption.AllDirectories).Any(p=>p==Path.Combine(folder,patch.RelativeFileName.Replace('/',Path.DirectorySeparatorChar))));
});
await Check("recomposer restores an old deletion while preserving later edits",async folder=>
{
    var baseline=Path.Combine(folder,"baseline.wav");var pcm=Pattern(20*2);
    WriteWave(baseline,16,1,pcm,rate:1000);
    var currentMap=new[]{new SourceSpan(0,5),new SourceSpan(8,12),new SourceSpan(14,20)};
    var currentPcm=Enumerable.Range(0,20).Where(i=>i<5||(i>=8&&i<12)||i>=14)
        .SelectMany(i=>pcm.Skip(i*2).Take(2)).ToArray();
    currentPcm[6*2]=201;currentPcm[6*2+1]=202; // later edit at original frame 9
    var current=Path.Combine(folder,"current.wav");WriteWave(current,16,1,currentPcm,rate:1000);
    var targetMap=new[]{new SourceSpan(0,12),new SourceSpan(14,20)};
    var output=Path.Combine(folder,"restored.wav");
    await AudioRecomposer.RecomposeAsync(current,Hash(current),currentMap,baseline,Hash(baseline),
        [new SourceSpan(0,20)],targetMap,[new SourceSpan(5,8)],output,default);
    var expected=Enumerable.Range(0,20).Where(i=>i<12||i>=14)
        .SelectMany(i=>pcm.Skip(i*2).Take(2)).ToArray();
    expected[9*2]=201;expected[9*2+1]=202;
    Assert(ReadPcm(output).SequenceEqual(expected),"restored range or later edit changed");
});
await Check("recomposer removes one old AU repair while preserving a later repair",async folder=>
{
    var before=Path.Combine(folder,"before.wav");var original=Pattern(12*2);
    WriteWave(before,16,1,original,rate:1000);
    var edited=original.ToArray();edited[3*2]=151;edited[3*2+1]=152;edited[4*2]=201;edited[4*2+1]=202;
    var current=Path.Combine(folder,"current.wav");WriteWave(current,16,1,edited,rate:1000);
    var output=Path.Combine(folder,"without-first-repair.wav");var map=new[]{new SourceSpan(0,12)};
    await AudioRecomposer.RecomposeAsync(current,Hash(current),map,before,Hash(before),map,map,
        [new SourceSpan(3,4)],output,default);
    var expected=edited.ToArray();expected[3*2]=original[3*2];expected[3*2+1]=original[3*2+1];
    Assert(ReadPcm(output).SequenceEqual(expected),"unselected later repair changed");
});
await Check("indexed playback coordinate lookup stays bounded on 10000 cuts",folder=>
{
    var map=new OriginalAxisMap(Enumerable.Range(0,10000).Select(i=>new SourceSpan(i*2000,i*2000+1000)),1000);
    var watch=System.Diagnostics.Stopwatch.StartNew();double sum=0;
    for(int i=0;i<100000;i++)sum+=map.ToEdited(i%20000,out _)+map.ToOriginal(i%10000);
    watch.Stop();Assert(sum>0);Console.WriteLine($"100000 paired coordinate lookups: {watch.Elapsed.TotalMilliseconds:F1} ms (not an audio latency measurement)");
    return Task.CompletedTask;
});
await Check("cleanup protects active and referenced projects, warns for AU, and detects changes",folder=>
{
    var work=Path.Combine(folder,"AudioWork");Directory.CreateDirectory(work);
    string Project(string name,string? root=null){root??=work;Directory.CreateDirectory(root);var p=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(p);File.WriteAllText(Path.Combine(p,"current.syncaudio.json"),JsonSerializer.Serialize(new {subtitleFileName=name+".srt"}));return p;}
    var a=Project("甲");var b=Project("乙");var au=Project("送修");var ready=Project("完成");
    var importedRoot=Path.Combine(folder,"Imported-0.4");var imported=Project("舊版大檔",importedRoot);
    File.WriteAllBytes(Path.Combine(imported,"legacy.wav"),new byte[1024]);
    File.WriteAllText(Path.Combine(b,"ref.json"),JsonSerializer.Serialize(new {audioPath=Path.Combine(a,"audio.wav")}));
    File.WriteAllText(Path.Combine(au,"handoff.json"),"{}");
    var list=WorkCleanupCatalog.Scan(work,b);
    Assert(list.Single(e=>e.Directory==a).ProtectedReason!=null);
    Assert(list.Single(e=>e.Directory==b).ProtectedReason!=null);
    var auditionCandidate=list.Single(e=>e.Directory==au);
    Assert(auditionCandidate.ProtectedReason==null&&auditionCandidate.Warning!=null);
    WorkCleanupCatalog.ValidateSelection(auditionCandidate,work,b,[]);
    var candidate=list.Single(e=>e.Directory==ready);Assert(candidate.ProtectedReason==null&&candidate.Name=="完成");
    WorkCleanupCatalog.ValidateSelection(candidate,work,b,[]);
    File.WriteAllText(Path.Combine(ready,"new.wav"),"new");
    bool refused=false;try{WorkCleanupCatalog.ValidateSelection(candidate,work,b,[]);}catch(InvalidDataException){refused=true;}
    Assert(refused && Directory.Exists(ready));
    var roots=new[]{new CleanupRoot(work,"目前版本"),new CleanupRoot(importedRoot,"舊版 0.4",true)};
    var combined=WorkCleanupCatalog.Scan(roots,null);
    var importedEntry=combined.Single(entry=>entry.Directory==imported);
    Assert(importedEntry.SourceLabel=="舊版 0.4"&&importedEntry.IsImported&&importedEntry.RootDirectory==importedRoot);
    Assert(importedEntry.Warning?.Contains("舊版匯入專案")==true&&importedEntry.ToString().Contains("[舊版 0.4]"));
    WorkCleanupCatalog.ValidateSelection(importedEntry,null,[]);
    WorkCleanupCatalog.ValidateSelections([importedEntry],roots,null,[]);
    File.WriteAllText(Path.Combine(ready,"late-cross-root-ref.json"),JsonSerializer.Serialize(new {audioPath=Path.Combine(imported,"legacy.wav")}));
    refused=false;try{WorkCleanupCatalog.ValidateSelections([importedEntry],roots,null,[]);}catch(InvalidDataException){refused=true;}
    Assert(refused,"delete-time validation missed a new cross-root reference");
    File.Delete(Path.Combine(ready,"late-cross-root-ref.json"));
    var additionalImported=Path.Combine(folder,"Data","Imported-v1");Directory.CreateDirectory(additionalImported);
    var portableRoots=WorkCleanupCatalog.PortableRoots(new PortablePathMap(folder));
    Assert(portableRoots.Select(root=>root.Label).SequenceEqual(new[]{"目前版本","舊版 0.4","舊版 0.6","舊版 v1"}));
    return Task.CompletedTask;
});
await Check("cleanup isolates damaged JSON records without hiding healthy projects",folder=>
{
    var work=Path.Combine(folder,"AudioWork");Directory.CreateDirectory(work);
    string Project(string name){var p=Path.Combine(work,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(p);File.WriteAllText(Path.Combine(p,"current.syncaudio.json"),JsonSerializer.Serialize(new {subtitleFileName=name+".srt"}));return p;}
    var damaged=Project("損壞");var healthy=Project("可清理");
    var revision=Path.Combine(damaged,"revisions","one");Directory.CreateDirectory(revision);
    File.WriteAllBytes(Path.Combine(revision,"state.syncaudio.json"),new byte[128]);
    var list=WorkCleanupCatalog.Scan(work,null);
    var damagedEntry=list.Single(e=>e.Directory==damaged);
    Assert(damagedEntry.ProtectedReason?.Contains("專案紀錄損壞")==true,"damaged project was not protected");
    var healthyEntry=list.Single(e=>e.Directory==healthy);
    Assert(healthyEntry.ProtectedReason==null,"healthy project was hidden by another project's damaged JSON");
    WorkCleanupCatalog.ValidateSelection(healthyEntry,work,null,[]);
    return Task.CompletedTask;
});
Console.WriteLine($"{passed} passed, {failures} failed. Fixtures retained at {root}");
return failures == 0 ? 0 : 1;

// Seekable virtual RF64 fixture validates long-file arithmetic without allocating gigabytes on disk.
sealed class HeaderAndZerosStream(byte[] header, long dataLength) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => header.Length + dataLength;
    public override long Position { get; set; }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, Math.Max(0, Length - Position));
        var slice = buffer[..count]; slice.Clear();
        if (Position < header.Length)
        {
            var copied = (int)Math.Min(count, header.Length - Position);
            header.AsSpan((int)Position, copied).CopyTo(slice);
        }
        Position += count; return count;
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, SeekOrigin.End => Length + offset,
        _ => throw new ArgumentOutOfRangeException(nameof(origin))
    };
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

static class NativeSparse
{
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MarkSparse(SafeFileHandle device, uint code, IntPtr input,
        uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
