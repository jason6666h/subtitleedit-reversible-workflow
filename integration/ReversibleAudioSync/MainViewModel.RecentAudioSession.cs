using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AudioWorkflow;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Logic.Config;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    internal string? RecentAudioWorkRootOverride { get; set; }
    internal static string? FindManagedAudioCheckpoint(string? audioFile, string workRoot)
    {
        if (string.IsNullOrWhiteSpace(audioFile) || !Path.IsPathFullyQualified(audioFile)) return null;
        var root = Path.GetFullPath(workRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(audioFile);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        var relative = Path.GetRelativePath(root, full);
        var projectId = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (!Guid.TryParseExact(projectId, "N", out _))
            throw new InvalidDataException("剪修音檔的專案位置不明；未載入來源字幕。");
        return Path.Combine(root, projectId, "current.syncaudio.json");
    }

    private string? RecentAudioCheckpoint(string? audio)
    {
        var checkpoint = FindManagedAudioCheckpoint(audio, RecentAudioWorkRootOverride ?? PortablePaths.WorkRoot);
        if (checkpoint != null) return checkpoint;
        if (PortablePaths.Current is { } map)
            foreach (var imported in new[] { "Imported-0.4", "Imported-0.6" })
            {
                checkpoint = FindManagedAudioCheckpoint(audio, Path.Combine(map.Root, "Data", imported));
                if (checkpoint != null) return checkpoint;
            }
        return null;
    }

    private bool IsManagedRecentAudio(string? audio)
    {
        try { return RecentAudioCheckpoint(audio) != null; }
        catch { return true; } // malformed managed path must not silently open an old SRT
    }

    // true means handled OR safely blocked: callers must never fall back to source SRT + edited WAV.
    internal async Task<bool> TryRestoreRecentAudioSessionAsync(string subtitleFile, string? audioFile)
    {
        try
        {
            var file = RecentAudioCheckpoint(audioFile);
            if (file == null) return false;
            if (_synchronousAudioBusy || Window == null || BlockWhileAuditionPending()) return true;
            if (!File.Exists(file)) throw new InvalidDataException("找不到剪修工作階段。為避免舊字幕配上新音檔，已停止開啟。請由「開啟音訊／字幕工作階段」選擇保存版本。");
            await RestoreAudioCheckpointAsync(file, subtitleFile);
            ShowStatus("已恢復配對音訊與修改後字幕；未重新載入來源 SRT。");
            return true;
        }
        catch (OperationCanceledException) { ShowStatus("已取消恢復配對工作階段；未載入來源字幕。"); return true; }
        catch (Exception e) { await SynchronousAudioErrorAsync(e); return true; }
    }

    internal async Task RestoreAudioCheckpointAsync(string file, string? expectedSubtitle = null)
    {
        await WithSynchronousAudioProgressAsync("正在驗證並恢復配對音訊與字幕…", async token =>
        {
            var restored = await Task.Run(() =>
            {
                if (new FileInfo(file).Length > 64 * 1024 * 1024) throw new InvalidDataException("工作階段資料過大。");
                var pointer = ReadAudioCheckpoint(file);
                if (pointer.ImmutableRevision is { } immutable)
                {
                    var verified = ReadAudioCheckpointJson(AudioRevisionStore.ReadVerifiedCheckpoint(immutable), immutable);
                    // Only the listening position may come from the mutable pointer. Never its subtitles/map.
                    if (pointer.Audio is { } latest && verified.Audio is { } saved &&
                        latest.SessionId == saved.SessionId && latest.RevisionId == saved.RevisionId &&
                        string.Equals(latest.Sha256, saved.Sha256, StringComparison.OrdinalIgnoreCase) &&
                        latest.TimelineMapJson == saved.TimelineMapJson && latest.TimelineSampleRate == saved.TimelineSampleRate &&
                        latest.AudioEditRegionsJson == saved.AudioEditRegionsJson &&
                        latest.TimelineOriginalFrames == saved.TimelineOriginalFrames &&
                        latest.RenderBaselineFileName == saved.RenderBaselineFileName &&
                        latest.RenderBaselineSha256 == saved.RenderBaselineSha256 &&
                        latest.RenderBaselineTimelineMapJson == saved.RenderBaselineTimelineMapJson &&
                        latest.AudioRepairPatchesJson == saved.AudioRepairPatchesJson)
                    {
                        if (!double.IsFinite(latest.PositionSeconds) || latest.PositionSeconds < 0)
                            throw new InvalidDataException("保存的續播位置無效。");
                        var savedDuration = saved.TimelineMapJson != null && saved.TimelineSampleRate > 0
                            ? (double)TimelineMap.Validate(ReadTimeline(saved)) / saved.TimelineSampleRate
                            : Path.GetExtension(saved.FileName).Equals(".wav", StringComparison.OrdinalIgnoreCase) && File.Exists(saved.FileName)
                                ? WaveInfo.Read(saved.FileName).DurationSeconds : double.PositiveInfinity;
                        if (latest.PositionSeconds > savedDuration + 0.001)
                            throw new InvalidDataException("保存的續播位置超出音訊長度。");
                        verified = verified with { Audio = saved with { PositionSeconds = latest.PositionSeconds } };
                    }
                    return (Checkpoint: verified, Revision: (string?)immutable);
                }
                return (Checkpoint: pointer, Revision: (string?)null); // legacy: full audio validation during media load
            }, token);
            var checkpoint = restored.Checkpoint;
            token.ThrowIfCancellationRequested();
            if (checkpoint.SchemaVersion != 1 || checkpoint.Kind != "se-synchronous-audio" || checkpoint.Audio == null ||
                checkpoint.Bookmarks == null || !double.IsFinite(checkpoint.CutPositionSeconds) || checkpoint.CutPositionSeconds < 0)
                throw new InvalidDataException("不是完整的 SE 音訊工作階段。");
            if (expectedSubtitle != null && !string.Equals(Path.GetFullPath(checkpoint.SubtitleFileName), Path.GetFullPath(expectedSubtitle), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("最近開啟的字幕與剪修專案不一致，已停止混用。請手動開啟正確工作階段。");
            var format = SubtitleFormats.FirstOrDefault(f => f.Name == checkpoint.SubtitleFormat)
                ?? throw new InvalidDataException("不支援此字幕格式。");
            EnsureSynchronousAudioTextFormat(format);
            var subtitle = new Subtitle();
            format.LoadSubtitle(subtitle, checkpoint.SubtitleNative.Replace("\r", "").Split('\n').ToList(), file);
            if (subtitle.Paragraphs.Count != checkpoint.SubtitleCount) throw new InvalidDataException("恢復字幕筆數不一致。");
            for (var i = 0; i < subtitle.Paragraphs.Count; i++) subtitle.Paragraphs[i].Bookmark = checkpoint.Bookmarks.ElementAtOrDefault(i);
            await ApplySynchronousAudioOpenAsync(subtitle, format, checkpoint.Audio, checkpoint.SubtitleFileName,
                Path.GetDirectoryName(file)!, checkpoint.CutPositionSeconds, token);
            _lastRevisionDirectory = restored.Revision;
            _revisionFingerprint = null;
            ReleaseAudioRevisionSource();
            _originalTimelinePreview = false;
            NotifySynchronousAudioModeChanged();
            _editedTimelineReturn = null;
            _comparisonEditedState = null;
            if (AudioVisualizer != null) AudioVisualizer.IsReadOnly = Se.Settings.General.LockTimeCodes;
            return true;
        });
        await TryOfferPendingAuditionRecoveryAsync();
    }
}
