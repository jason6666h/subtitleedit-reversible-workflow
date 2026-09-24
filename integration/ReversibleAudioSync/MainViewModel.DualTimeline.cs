using AudioWorkflow;
using CommunityToolkit.Mvvm.Input;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Features.Shared;
using Nikse.SubtitleEdit.Logic.UndoRedo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private SynchronousAudioState? _comparisonEditedState;
    private bool _timelineSwitching;
    private readonly System.Collections.Generic.HashSet<string> _verifiedWaveformSources=new(StringComparer.OrdinalIgnoreCase);
    private void RememberVerifiedWaveform()
    {
        if(_synchronousAudioState is {} current && AudioVisualizer?.WavePeaks!=null && !IsWaveformGenerating &&
            string.Equals(_videoFileName,current.FileName,StringComparison.OrdinalIgnoreCase))
            _verifiedWaveformSources.Add(current.FileName+"|"+current.Sha256);
    }
    private string? _axisJson;
    private int _axisRate;
    private OriginalAxisMap? _axisMap;
    private string? _comparisonRoot;
    private string? _comparisonOriginal;
    private AudioEditRegion? _selectedAudioEditRegion;
    internal AudioEditRegion? SelectedAudioEditRegion => _selectedAudioEditRegion;

    internal static string AddAudioEditRegion(SynchronousAudioState state, AudioEditRegion region)
    {
        region.Validate();
        return JsonSerializer.Serialize(ReadAudioEditRegions(state).Append(region).ToArray());
    }

    internal static (double Start, double End) ToOriginalAudioRegion(
        SynchronousAudioState state, long editedStart, long editedEnd)
    {
        if (state.TimelineSampleRate <= 0)
            throw new InvalidDataException("音訊編輯區缺少時間軸取樣率。");
        if (state.TimelineMapJson == null)
            return ((double)editedStart / state.TimelineSampleRate, (double)editedEnd / state.TimelineSampleRate);
        var spans = ReadTimeline(state);
        var length = TimelineMap.Validate(spans);
        var start = TimelineMap.ToOriginal(spans, Math.Clamp(editedStart, 0, length));
        var end = TimelineMap.ToOriginal(spans, Math.Clamp(editedEnd, 0, length));
        return ((double)start / state.TimelineSampleRate, (double)end / state.TimelineSampleRate);
    }

    internal IReadOnlyList<AudioEditRegion> DisplayAudioEditRegions
    {
        get
        {
            var state = _originalTimelinePreview ? _comparisonEditedState : _synchronousAudioState;
            if (state == null) return [];
            var result = ReadAudioEditRegions(state).ToList();
            if (state.TimelineMapJson != null && state.TimelineSampleRate > 0)
            {
                var spans = ReadTimeline(state);
                TimelineMap.Validate(spans);
                var previous = 0L;
                foreach (var span in spans)
                {
                    AddLegacyGap(previous, span.Start);
                    previous = span.End;
                }
                AddLegacyGap(previous, Math.Max(previous, state.TimelineOriginalFrames));

                void AddLegacyGap(long start, long end)
                {
                    if (end <= start) return;
                    var a = (double)start / state.TimelineSampleRate;
                    var b = (double)end / state.TimelineSampleRate;
                    var middle = (a + b) / 2;
                    if (result.Any(r => r.Kind == AudioEditRegionKind.Deleted &&
                        middle >= r.StartSeconds && middle < r.EndSeconds)) return;
                    result.Add(new AudioEditRegion($"legacy-delete-{start}-{end}",
                        AudioEditRegionKind.Deleted, a, b));
                }
            }
            if (_pendingAudition?.Files.Region is { } pending)
            {
                var range = state.TimelineSampleRate > 0
                    ? ToOriginalAudioRegion(state, pending.StartSample, checked(pending.StartSample + pending.RepairSamples))
                    : (Start: (double)pending.StartSample / _pendingAudition.Files.Timeline.SampleRate,
                       End: (double)(pending.StartSample + pending.RepairSamples) / _pendingAudition.Files.Timeline.SampleRate);
                result.Add(new AudioEditRegion("audition-pending", AudioEditRegionKind.AuditionPending,
                    range.Start, range.End));
            }
            return result.OrderBy(r => r.StartSeconds).ThenBy(r => r.EndSeconds).ToArray();
        }
    }

    internal void SelectAudioEditRegion(AudioEditRegion? region)
    {
        _selectedAudioEditRegion = region;
        if (region == null) return;
        ClearNativeSynchronousAudioRange();
        var label = region.Kind switch
        {
            AudioEditRegionKind.Deleted => "已刪除區",
            AudioEditRegionKind.AuditionRepair => "AU 修音區",
            _ => "AU 待接回區",
        };
        ShowStatus($"已選取{label} {region.StartSeconds:F3}～{region.EndSeconds:F3} 秒；按 Delete 移除此標記並恢復對應波形。");
    }

    [RelayCommand]
    private async Task SynchronousAudioCancelSelectedRegion()
    {
        var selected = _selectedAudioEditRegion;
        if (selected == null || _synchronousAudioBusy || Window == null || _originalTimelinePreview) return;
        if (selected.Kind == AudioEditRegionKind.AuditionPending)
        {
            SynchronousAudioCancelAudition();
            _selectedAudioEditRegion = null;
            return;
        }
        if (_pendingAudition != null)
        {
            ShowStatus("請先完成或取消目前的 AU 交接，再取消既有音訊標記。");
            return;
        }
        var label = selected.Kind == AudioEditRegionKind.Deleted ? "同步刪除" : "AU 局部修音";
        var answer = await MessageBox.Show(Window, "取消音訊操作",
            $"移除此{label}標記（{selected.StartSeconds:F3}～{selected.EndSeconds:F3} 秒）？\n\n只取消這一區，其他較早或較晚的剪輯／修音都會保留；波形與字幕會一起更新。這次取消本身也可用 Ctrl+Z 復原。",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != MessageBoxResult.Yes || _selectedAudioEditRegion?.Id != selected.Id) return;
        await CancelAudioEditRegionAsync(selected);
    }

    private async Task CancelAudioEditRegionAsync(AudioEditRegion selected)
    {
        var label = selected.Kind == AudioEditRegionKind.Deleted ? "同步刪除" : "AU 局部修音";
        var current = _synchronousAudioState ?? throw new InvalidOperationException("尚未建立可逆音訊專案。");
        var directory = _synchronousAudioDirectory ?? throw new InvalidDataException("缺少可逆音訊專案目錄。");
        var identity = _subtitle;
        var format = SelectedSubtitleFormat;
        var initialHash = GetUndoRedoHash();
        var live = GetUpdateSubtitle();
        var snapshot = new Subtitle { Header = live.Header, Footer = live.Footer };
        snapshot.Paragraphs.AddRange(live.Paragraphs.Select(p => new Paragraph(p, false)));
        SynchronousAudioState? retired = null;
        await WithSynchronousAudioProgressAsync("正在移除選取標記並恢復波形…", async token =>
        {
            await RunSynchronousAudioTransactionAsync(async (before, rollbackMedia, ct) =>
            {
                if (_synchronousAudioState?.SessionId != current.SessionId || GetUndoRedoHash() != initialHash)
                    throw new InvalidOperationException("確認期間音訊或字幕已改變，請重新選取色塊。");
                await EnsureCompactAudioAvailableAsync(current, directory, ct);
                var rate = current.TimelineSampleRate;
                if (rate <= 0) throw new InvalidDataException("音訊標記缺少原時間軸取樣率。");
                var currentTimeline = ReadTimeline(current);
                var start = Math.Max(0, (long)Math.Round(selected.StartSeconds * rate));
                var end = Math.Min(current.TimelineOriginalFrames > 0 ? current.TimelineOriginalFrames : currentTimeline[^1].End,
                    (long)Math.Round(selected.EndSeconds * rate));
                if (end <= start) throw new InvalidDataException("音訊標記範圍無效。");
                AudioCheckpoint? parent = null;
                if (selected.Kind == AudioEditRegionKind.Deleted)
                    parent = await ReadOperationParentAsync(current, selected.Id, ct);
                var restoreRanges = selected.Kind == AudioEditRegionKind.Deleted
                    ? parent == null
                        ? new[] { new SourceSpan(start, end) }
                        : IntersectTimeline(ReadTimeline(parent.Audio), start, end)
                    : new[] { new SourceSpan(start, end) };
                if (restoreRanges.Length == 0) throw new InvalidDataException("此刪除標記在前一版本中沒有可恢復的音訊。");
                var targetTimeline = selected.Kind == AudioEditRegionKind.Deleted
                    ? RestoreTimelineRanges(currentTimeline, restoreRanges)
                    : currentTimeline;
                var patches = ReadAudioRepairPatches(current)
                    .Where(p => selected.Kind != AudioEditRegionKind.AuditionRepair || p.Id != selected.Id).ToArray();
                var regions = NormalizeDeletedRegions(
                    ReadAudioEditRegions(current).Where(r => r.Id != selected.Id), targetTimeline,
                    current.TimelineOriginalFrames > 0 ? current.TimelineOriginalFrames : currentTimeline[^1].End, rate);
                var output = Path.Combine(directory, "restored-" + Guid.NewGuid().ToString("N") + ".wav");
                string outputHash;
                var compactBaselineTimeline = HasCompactAudio(current)
                    ? ReadRenderBaselineTimeline(current)
                    : null;
                if (compactBaselineTimeline != null && TimelineContains(compactBaselineTimeline, targetTimeline))
                {
                    outputHash = await AudioDeltaStore.RebuildAsync(current.RenderBaselineFileName!,
                        current.RenderBaselineSha256!, compactBaselineTimeline, targetTimeline, patches,
                        directory, output, ct);
                }
                else
                {
                    parent ??= await ReadOperationParentAsync(current, selected.Id, ct)
                        ?? throw new InvalidDataException("找不到此舊標記之前的音訊版本，無法安全移除。");
                    var alternate = await PrepareLegacyAlternateAsync(parent.Audio, directory, ct);
                    var alternateTimeline = ReadTimeline(alternate);
                    try
                    {
                        outputHash = await AudioRecomposer.RecomposeAsync(current.FileName, current.Sha256,
                            currentTimeline, alternate.FileName, alternate.Sha256, alternateTimeline,
                            targetTimeline, restoreRanges, output, ct);
                    }
                    finally
                    {
                        if (!string.Equals(alternate.FileName, parent.Audio.FileName, StringComparison.OrdinalIgnoreCase) &&
                            IsManagedAudioPath(alternate.FileName, directory) && File.Exists(alternate.FileName))
                            File.Delete(alternate.FileName);
                    }
                }

                var revisionId = Guid.NewGuid().ToString("N");
                var target = current with
                {
                    FileName = output,
                    Sha256 = outputHash,
                    RevisionId = revisionId,
                    PositionSeconds = (double)TimelineMap.ToEdited(targetTimeline, start) / rate,
                    TimelineMapJson = JsonSerializer.Serialize(targetTimeline),
                    AudioEditRegionsJson = JsonSerializer.Serialize(regions),
                    AudioRepairPatchesJson = AudioDeltaStore.WritePatches(patches)
                };
                var transformed = RemapSubtitles(snapshot, currentTimeline, targetTimeline, rate,
                    selected.Kind == AudioEditRegionKind.Deleted ? parent : null, start, end);
                WriteAudioCheckpoint("before.syncaudio.json", current, before);
                await LoadSynchronousAudioAsync(target, ct);
                ct.ThrowIfCancellationRequested();
                EnsureSynchronousAudioContext(identity, initialHash, target.FileName, format);
                ReplaceSubtitles(transformed.Paragraphs.Select(p => new SubtitleLineViewModel(p, format)));
                SetSynchronousAudioState(target);
                _synchronousAudioCutPosition = target.PositionSeconds;
                _updateAudioVisualizer = true;
                ClearSynchronousAudioSelection();
                WriteAudioCheckpoint("current.syncaudio.json", target);
                if (_undoRedoManager.UndoList.LastOrDefault()?.Hash != before.Hash) _undoRedoManager.Do(before);
                _undoRedoManager.Do(MakeUndoRedoObject($"移除{label}標記"));
                _synchronousAudioCutPositions[target.RevisionId] = target.PositionSeconds;
                retired = current;
            }, token);
            return true;
        });
        TryRetireCompactWorkingAudio(retired);
        ShowStatus($"已移除{label}標記並恢復波形；其他剪輯與修音均保留。Ctrl+Z 可復原這次取消。");
    }

    private static SourceSpan[] IntersectTimeline(IReadOnlyList<SourceSpan> timeline, long start, long end) =>
        timeline.Select(span => new SourceSpan(Math.Max(span.Start, start), Math.Min(span.End, end)))
            .Where(span => span.End > span.Start).ToArray();

    private static SourceSpan[] RestoreTimelineRanges(IReadOnlyList<SourceSpan> current,
        IReadOnlyList<SourceSpan> restored)
    {
        var ordered = current.Concat(restored).OrderBy(s => s.Start).ThenBy(s => s.End);
        var result = new List<SourceSpan>();
        foreach (var span in ordered)
        {
            if (result.Count == 0 || span.Start > result[^1].End) result.Add(span);
            else result[^1] = new SourceSpan(result[^1].Start, Math.Max(result[^1].End, span.End));
        }
        TimelineMap.Validate(result);
        return result.ToArray();
    }

    private static bool TimelineContains(IReadOnlyList<SourceSpan> available, IReadOnlyList<SourceSpan> target)
    {
        foreach (var requested in target)
        {
            if (!available.Any(span => requested.Start >= span.Start && requested.End <= span.End)) return false;
        }
        return true;
    }

    private static AudioEditRegion[] NormalizeDeletedRegions(IEnumerable<AudioEditRegion> regions,
        IReadOnlyList<SourceSpan> timeline, long originalFrames, int rate)
    {
        var source = regions.ToArray();
        var result = source.Where(r => r.Kind != AudioEditRegionKind.Deleted).ToList();
        var gaps = new List<SourceSpan>();
        long previous = 0;
        foreach (var span in timeline)
        {
            if (span.Start > previous) gaps.Add(new SourceSpan(previous, span.Start));
            previous = span.End;
        }
        if (originalFrames > previous) gaps.Add(new SourceSpan(previous, originalFrames));
        foreach (var region in source.Where(r => r.Kind == AudioEditRegionKind.Deleted))
        {
            var start = Math.Max(0, (long)Math.Round(region.StartSeconds * rate));
            var end = Math.Min(originalFrames, (long)Math.Round(region.EndSeconds * rate));
            foreach (var gap in gaps)
            {
                var a = Math.Max(start, gap.Start);
                var b = Math.Min(end, gap.End);
                if (b > a) result.Add(new AudioEditRegion(region.Id, region.Kind,
                    (double)a / rate, (double)b / rate));
            }
        }
        return result.OrderBy(r => r.StartSeconds).ThenBy(r => r.EndSeconds).ToArray();
    }

    private async Task<SynchronousAudioState> PrepareLegacyAlternateAsync(SynchronousAudioState state,
        string directory, CancellationToken token)
    {
        await EnsureCompactAudioAvailableAsync(state, directory, token);
        if (Path.GetExtension(state.FileName).Equals(".wav", StringComparison.OrdinalIgnoreCase)) return state;
        if (!Path.GetExtension(state.FileName).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("舊音訊版本不是可支援的 MP3 或 PCM WAV。");
        var decoded = Path.Combine(directory, "legacy-baseline-" + Guid.NewGuid().ToString("N") + ".wav");
        await new FfmpegService(new WorkflowSettings(), AppContext.BaseDirectory)
            .DecodeAsync(state.FileName, decoded, token);
        using var verified = await AudioRevisionSource.OpenAndHashAsync(decoded, token);
        var timeline = ReadTimeline(state);
        if (WaveInfo.Read(decoded).SampleCount != TimelineMap.Validate(timeline))
            throw new InvalidDataException("舊 MP3 解碼後長度與保存的原時間軸不一致。");
        return state with { FileName = decoded, Sha256 = verified.Sha256 };
    }

    private async Task<AudioCheckpoint?> ReadOperationParentAsync(SynchronousAudioState current,
        string operationId, CancellationToken token)
    {
        var root = RevisionRoot(current, _synchronousAudioDirectory!);
        if (!Directory.Exists(root)) return null;
        string? operationDirectory = null;
        foreach (var directory in Directory.EnumerateDirectories(root)
                     .Where(p => !Path.GetFileName(p).StartsWith(".pending-", StringComparison.Ordinal))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var candidate = ReadAudioCheckpoint(Path.Combine(directory, "state.syncaudio.json"));
                if (candidate.Audio.RevisionId == operationId) { operationDirectory = directory; break; }
            }
            catch { /* A corrupt candidate is rejected if it becomes the selected parent. */ }
        }
        if (operationDirectory == null && operationId.StartsWith("legacy-delete-", StringComparison.Ordinal))
        {
            var originalDirectory = Directory.EnumerateDirectories(root)
                .Where(p => !Path.GetFileName(p).StartsWith(".pending-", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
            if (originalDirectory == null) return null;
            var originalRaw = await Task.Run(() => AudioRevisionStore.ReadVerifiedCheckpoint(originalDirectory), token);
            return ReadAudioCheckpointJson(originalRaw, originalDirectory);
        }
        if (operationDirectory == null) return null;
        var operation = await Task.Run(() => AudioRevisionStore.ReadManifest(operationDirectory), token);
        if (string.IsNullOrWhiteSpace(operation.ParentId)) return null;
        var parentDirectory = Path.Combine(root, operation.ParentId);
        var raw = await Task.Run(() => AudioRevisionStore.ReadVerifiedCheckpoint(parentDirectory), token);
        return ReadAudioCheckpointJson(raw, parentDirectory);
    }

    private Subtitle RemapSubtitles(Subtitle currentSubtitle, IReadOnlyList<SourceSpan> currentTimeline,
        IReadOnlyList<SourceSpan> targetTimeline, int rate, AudioCheckpoint? parent, long restoredStart, long restoredEnd)
    {
        var result = new Subtitle { Header = currentSubtitle.Header, Footer = currentSubtitle.Footer };
        foreach (var paragraph in currentSubtitle.Paragraphs)
        {
            var mapped = MapParagraph(paragraph, currentTimeline, targetTimeline, rate);
            if (mapped.EndTime.TotalMilliseconds > mapped.StartTime.TotalMilliseconds) result.Paragraphs.Add(mapped);
        }
        if (parent != null)
        {
            var parentFormat = SubtitleFormats.FirstOrDefault(f => f.Name == parent.SubtitleFormat)
                ?? throw new InvalidDataException("音訊標記之前的字幕格式不支援。");
            var source = new Subtitle();
            parentFormat.LoadSubtitle(source, parent.SubtitleNative.Replace("\r", "").Split('\n').ToList(),
                "parent" + parentFormat.Extension);
            var parentTimeline = ReadTimeline(parent.Audio);
            foreach (var paragraph in source.Paragraphs)
            {
                var originalStart = ToOriginalBoundary(parentTimeline,
                    Math.Max(0, (long)Math.Round(paragraph.StartTime.TotalSeconds * rate)), false);
                var originalEnd = ToOriginalBoundary(parentTimeline,
                    Math.Max(0, (long)Math.Round(paragraph.EndTime.TotalSeconds * rate)), true);
                if (originalStart < restoredStart || originalEnd > restoredEnd || originalEnd <= originalStart) continue;
                var restored = new Paragraph(paragraph);
                restored.StartTime.TotalMilliseconds = 1000d * TimelineMap.ToEdited(targetTimeline, originalStart) / rate;
                restored.EndTime.TotalMilliseconds = 1000d * TimelineMap.ToEdited(targetTimeline, originalEnd) / rate;
                if (!result.Paragraphs.Any(p => p.Text == restored.Text &&
                    Math.Abs(p.StartTime.TotalMilliseconds - restored.StartTime.TotalMilliseconds) < 1))
                    result.Paragraphs.Add(restored);
            }
        }
        result.Paragraphs.Sort((a, b) => a.StartTime.TotalMilliseconds.CompareTo(b.StartTime.TotalMilliseconds));
        result.Renumber();
        return result;
    }

    private static Paragraph MapParagraph(Paragraph paragraph, IReadOnlyList<SourceSpan> from,
        IReadOnlyList<SourceSpan> to, int rate)
    {
        var fromLength = TimelineMap.Validate(from);
        var startFrame = Math.Clamp((long)Math.Round(paragraph.StartTime.TotalSeconds * rate), 0, fromLength);
        var endFrame = Math.Clamp((long)Math.Round(paragraph.EndTime.TotalSeconds * rate), 0, fromLength);
        var originalStart = ToOriginalBoundary(from, startFrame, false);
        var originalEnd = ToOriginalBoundary(from, endFrame, true);
        var result = new Paragraph(paragraph);
        result.StartTime.TotalMilliseconds = 1000d * TimelineMap.ToEdited(to, originalStart) / rate;
        result.EndTime.TotalMilliseconds = 1000d * TimelineMap.ToEdited(to, originalEnd) / rate;
        return result;
    }

    private static long ToOriginalBoundary(IReadOnlyList<SourceSpan> timeline, long edited, bool leftBias)
    {
        var total = TimelineMap.Validate(timeline);
        edited = Math.Clamp(edited, 0, total);
        long cursor = 0;
        foreach (var span in timeline)
        {
            var next = cursor + span.End - span.Start;
            if (edited < next || (leftBias && edited == next)) return span.Start + edited - cursor;
            cursor = next;
        }
        return timeline[^1].End;
    }

    private static bool IsOriginalPointDeleted(SynchronousAudioState state, double seconds)
    {
        if (state.TimelineMapJson == null || state.TimelineSampleRate <= 0) return false;
        var map = new OriginalAxisMap(ReadTimeline(state),
            state.TimelineSampleRate);
        _ = map.ToEdited(seconds, out var removed);
        return removed;
    }
    internal OriginalAxisMap? DisplayAxisMap
    {
        get
        {
            var s=_originalTimelinePreview?_comparisonEditedState:_synchronousAudioState;
            if(s?.TimelineMapJson!=_axisJson || (s?.TimelineSampleRate??0)!=_axisRate)
            {
                _axisJson=s?.TimelineMapJson;_axisRate=s?.TimelineSampleRate??0;
                _axisMap=_axisJson==null||_axisRate<=0?null:new OriginalAxisMap(ParseTimelineJson(_axisJson,"缺少顯示時間軸。"),_axisRate);
            }
            return _axisMap;
        }
    }
    internal async Task PlayOriginalAxisAsync(bool original,double originalSeconds)
    {
        if(IsAudioTimelineBusy) return;
        var player=GetVideoPlayerControl();
        var speed=player?.VideoPlayer.Speed??1;
        var position=original?originalSeconds:DisplayAxisMap?.ToEdited(originalSeconds,out _)??originalSeconds;
        await SwitchAudioTimelineAsync(original,position);
        if(IsOriginalAudioTimeline!=original || IsAudioTimelineBusy) return;
        player=GetVideoPlayerControl();
        if(player!=null) { player.SetSpeed(speed);player.VideoPlayer.Play();player.SetPlayPauseIcon(true); }
    }
    internal void SelectOriginalAxisRange(double start,double end)
    {
        if(IsAudioTimelineBusy || IsOriginalAudioTimeline || AudioVisualizer==null)return;
        _selectedAudioEditRegion=null;
        var map=DisplayAxisMap;
        var a=map?.ToEdited(Math.Min(start,end),out _)??Math.Min(start,end);
        var b=map?.ToEdited(Math.Max(start,end),out _)??Math.Max(start,end);
        IsSynchronousAudioRangeSelectionEnabled=true;
        AudioVisualizer.AudioRangeSelection=b>a?new SubtitleLineViewModel(new Paragraph("",a*1000,b*1000),SelectedSubtitleFormat):null;
    }
    internal bool IsOriginalAudioTimeline => _originalTimelinePreview;
    internal bool IsAudioTimelineBusy => _synchronousAudioBusy || _pendingAudition != null || _timelineSwitching;
    internal string? ComparisonRevisionDirectory()
    {
        if (_synchronousAudioState == null || _synchronousAudioDirectory == null) return null;
        if (_originalTimelinePreview) return _editedTimelineReturn;
        var root = RevisionRoot(_synchronousAudioState,_synchronousAudioDirectory);
        if(root!=_comparisonRoot || _comparisonOriginal==null)
        {
            _comparisonRoot=root;
            _comparisonOriginal=Directory.Exists(root) ? Directory.EnumerateDirectories(root).Where(p=>!Path.GetFileName(p).StartsWith(".pending-",StringComparison.Ordinal)).OrderBy(p=>p,StringComparer.Ordinal).FirstOrDefault() : null;
        }
        return _comparisonOriginal;
    }
    internal async Task<(string Path,string Hash,SubtitleLineViewModel[] Lines)> ReadComparisonRevisionAsync(
        string directory, CancellationToken token)
    {
        var manifest = await Task.Run(() => AudioRevisionStore.ReadManifest(directory), token);
        var audio = await GetValidatedAudioSourceAsync(manifest.AudioPath, manifest.AudioSha256, token);
        var raw = await Task.Run(() => AudioRevisionStore.ReadVerifiedCheckpoint(directory, audio), token);
        var checkpoint = ReadAudioCheckpointJson(raw, directory);
        var format=SubtitleFormats.FirstOrDefault(f=>f.Name==checkpoint.SubtitleFormat) ?? throw new InvalidDataException("Unsupported timeline subtitle format.");
        var subtitle=new Subtitle();
        format.LoadSubtitle(subtitle,checkpoint.SubtitleNative.Replace("\r","").Split('\n').ToList(),"timeline"+format.Extension);
        if(subtitle.Paragraphs.Count!=checkpoint.SubtitleCount) throw new InvalidDataException("Timeline subtitle count mismatch.");
        return (checkpoint.Audio.FileName,checkpoint.Audio.Sha256,subtitle.Paragraphs.Select(p=>new SubtitleLineViewModel(p,format)).ToArray());
    }
    internal double ComparisonPosition(double seconds)
    {
        var state=_originalTimelinePreview?_comparisonEditedState:_synchronousAudioState;
        if(state?.TimelineMapJson==null || state.TimelineSampleRate<=0) return Math.Max(0,seconds);
        var spans=ReadTimeline(state);
        var frame=Math.Max(0,(long)Math.Round(seconds*state.TimelineSampleRate));
        return (double)(_originalTimelinePreview?TimelineMap.ToEdited(spans,frame):TimelineMap.ToOriginal(spans,Math.Min(frame,TimelineMap.Validate(spans))))/state.TimelineSampleRate;
    }
    internal async Task SwitchAudioTimelineAsync(bool original, double? position=null)
    {
        if(IsAudioTimelineBusy) { ShowStatus("請先完成或取消 AU 交接，再切換聽校時間軸。"); return; }
        _timelineSwitching=true;
        try
        {
        if(original!=_originalTimelinePreview)
        {
            if(original) await SynchronousAudioOriginalTimeline();
            else await SynchronousAudioEditedTimeline();
        }
        if(original==_originalTimelinePreview && position.HasValue) GetVideoPlayerControl()?.SeekTo(Math.Max(0,position.Value));
        }
        finally { _timelineSwitching=false; }
    }
}
