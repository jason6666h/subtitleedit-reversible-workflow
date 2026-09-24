using AudioWorkflow;
using CommunityToolkit.Mvvm.Input;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.UndoRedo;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private string? _revisionFingerprint;
    private string? _lastRevisionDirectory;
    private bool _originalTimelinePreview;
    private string? _editedTimelineReturn;
    private DateTime _revisionDue;
    private int _revisionObservedHash;
    private AudioRevisionSource? _revisionAudioSource;

    internal void ReleaseAudioRevisionSource()
    {
        _revisionAudioSource?.Dispose();
        _revisionAudioSource = null;
    }

    [RelayCommand]
    private async Task SynchronousAudioBeginProject()
    {
        if (_synchronousAudioBusy || Window == null || _synchronousAudioState != null) return;
        try
        {
            EnsureSynchronousAudioTextFormat(SelectedSubtitleFormat);
            if (!File.Exists(_videoFileName) || (!Path.GetExtension(_videoFileName).Equals(".mp3", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetExtension(_videoFileName).Equals(".wav", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("請先載入本機 MP3／WAV 及其原時間軸字幕，再建立專案。");
            await WithSynchronousAudioProgressAsync("正在保留原音訊與原字幕…", async token =>
            {
                await RunSynchronousAudioTransactionAsync(async (before, baseline, ct) =>
                {
                    _synchronousAudioDirectory ??= NewAudioWorkDirectory();
                    var ownedBaseline = await EnsureOwnedRevisionBaselineAsync(baseline!, _synchronousAudioDirectory, ct);
                    WriteAudioCheckpoint("before.syncaudio.json", ownedBaseline, before);
                    var owned = ReadAudioCheckpoint(Path.Combine(_lastRevisionDirectory!, "state.syncaudio.json")).Audio;
                    await LoadSynchronousAudioAsync(owned, ct);
                    ct.ThrowIfCancellationRequested();
                    if (GetUndoRedoHash() != before.Hash) throw new InvalidOperationException("字幕在建立專案期間已改變，請重試。");
                    SetSynchronousAudioState(owned);
                    foreach (var entry in _undoRedoManager.UndoList.Concat(_undoRedoManager.RedoList)) entry.SynchronousAudio ??= owned;
                    WriteAudioCheckpoint("current.syncaudio.json", owned);
                }, token);
                return true;
            });
            ShowStatus("可逆專案已建立；Ctrl+S 會寫回目前字幕檔，並同步保存不可覆寫的音訊／字幕版本。" );
        }
        catch (Exception e) { await SynchronousAudioErrorAsync(e); }
    }

    private static string NewAudioWorkDirectory() => Path.Combine(PortablePaths.WorkRoot, Guid.NewGuid().ToString("N"));

    private string RevisionRoot(SynchronousAudioState state, string directory) =>
        Path.Combine(directory, "revisions", state.SessionId);

    private void SaveImmutableAudioRevision(AudioCheckpoint checkpoint, Subtitle subtitle, string directory, string kind)
    {
        var state = checkpoint.Audio;
        var root = RevisionRoot(state, directory);
        Directory.CreateDirectory(root);
        // Hash excludes playhead position: listening alone must not create revisions.
        var srt = subtitle.ToText(new SubRip());
        var identity = JsonSerializer.Serialize(new { state.FileName, state.Sha256, state.RevisionId,
            state.TimelineMapJson, state.AudioEditRegionsJson, state.TimelineOriginalFrames,
            state.RenderBaselineFileName, state.RenderBaselineSha256, state.RenderBaselineTimelineMapJson,
            state.AudioRepairPatchesJson,
            checkpoint.SubtitleNative, checkpoint.Bookmarks });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var cachedStateSource = TryGetValidatedAudioSource(state.FileName, state.Sha256);
        var stateSource = cachedStateSource ?? (_revisionAudioSource?.Matches(state.FileName, state.Sha256) == true ? _revisionAudioSource : null);
        if (_revisionFingerprint == fingerprint && stateSource != null &&
            _lastRevisionDirectory != null && File.Exists(Path.Combine(_lastRevisionDirectory, "revision.json"))) return;
        var first = !Directory.EnumerateDirectories(root).Any(p => !Path.GetFileName(p).StartsWith(".pending-", StringComparison.Ordinal));
        if (first && !HasCompactAudio(state) && !IsManagedAudioPath(state.FileName, directory))
        {
            // Legacy fallback only. Normal entry points asynchronously own the first baseline
            // before reaching this synchronous snapshot writer.
            var original = Path.Combine(directory, "original-" + Guid.NewGuid().ToString("N") + Path.GetExtension(state.FileName));
            using (var input = new FileStream(state.FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(original, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            var baseline = state with { FileName = original };
            checkpoint = checkpoint with { Audio = baseline };
            kind = "original";
        }
        else if (first) kind = "original";
        var revisionAudioPath = HasCompactAudio(checkpoint.Audio)
            ? checkpoint.Audio.RenderBaselineFileName!
            : checkpoint.Audio.FileName;
        var revisionAudioHash = HasCompactAudio(checkpoint.Audio)
            ? checkpoint.Audio.RenderBaselineSha256!
            : checkpoint.Audio.Sha256;
        var cachedCheckpointSource = TryGetValidatedAudioSource(revisionAudioPath, revisionAudioHash);
        AudioRevisionSource saveSource;
        if (cachedCheckpointSource != null)
        {
            ReleaseAudioRevisionSource();
            saveSource = cachedCheckpointSource;
        }
        else
        {
            if (_revisionAudioSource?.Matches(revisionAudioPath, revisionAudioHash) != true)
            {
                ReleaseAudioRevisionSource();
                _revisionAudioSource = new AudioRevisionSource(revisionAudioPath, revisionAudioHash);
            }
            saveSource = _revisionAudioSource;
        }
        var saved = AudioRevisionStore.Save(root,
            _lastRevisionDirectory == null ? null : Path.GetFileName(_lastRevisionDirectory), kind,
            revisionAudioPath, revisionAudioHash, srt, JsonSerializer.Serialize(checkpoint), saveSource);
        _lastRevisionDirectory = saved;
        _revisionFingerprint = fingerprint;
    }

    private bool BlockDetachedRevisionSave()
    {
        if (string.IsNullOrEmpty(_subtitleFileName) || !string.Equals(Path.GetFileName(_subtitleFileName), "subtitles.srt", StringComparison.OrdinalIgnoreCase)) return false;
        var directory = Path.GetDirectoryName(_subtitleFileName)!;
        if (!File.Exists(Path.Combine(directory, "revision.json")) || !File.Exists(Path.Combine(directory, "state.syncaudio.json"))) return false;
        ShowStatus("這是不可覆寫的歷史 SRT。請從「更多 → 恢復歷史音訊／字幕版本」開啟配對版本後繼續編修。");
        return true;
    }

    private void AudioRevisionTick()
    {
        if (_synchronousAudioBusy || _originalTimelinePreview || _synchronousAudioState == null || IsUserEditing()) return;
        // Undo change detection already hashes the complete subtitle (including original text,
        // bookmarks and the audio revision) every 333 ms. Re-hashing every line again here on
        // the UI timer caused periodic playback stalls on long subtitles. A checkpoint may wait
        // one change-detection tick, which is still well inside this method's one-second debounce.
        var trackedHash = _undoRedoManager.LatestUndoHash;
        if (!trackedHash.HasValue) return;
        var hash = trackedHash.Value;
        if (hash != _revisionObservedHash)
        {
            _revisionObservedHash = hash;
            _revisionDue = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        if (_revisionDue == default || DateTime.UtcNow < _revisionDue) return;
        _revisionDue = default;
        TrySaveAudioCheckpoint();
    }

    private bool BlockOriginalTimelineEdit()
    {
        if (!_originalTimelinePreview) return false;
        ShowStatus("目前為原時間軸聽校。請先按「返回修改後時間軸」再剪修、修音或輸出。");
        return true;
    }

    [RelayCommand]
    private async Task SynchronousAudioOriginalTimeline()
    {
        if (_synchronousAudioBusy || _synchronousAudioState == null || BlockWhileAuditionPending() || _originalTimelinePreview) return;
        try
        {
            WriteAudioCheckpoint("current.syncaudio.json", CaptureSynchronousAudioState()!);
            var root = RevisionRoot(_synchronousAudioState, _synchronousAudioDirectory!);
            var original = Directory.EnumerateDirectories(root).Where(p => !Path.GetFileName(p).StartsWith(".pending-", StringComparison.Ordinal))
                .OrderBy(p => p, StringComparer.Ordinal).First();
            var returnTo = _lastRevisionDirectory!;
            var position = GetVideoPlayerControl()?.VideoPlayer.Position ?? 0;
            var mapState = _synchronousAudioState;
            _comparisonEditedState = mapState;
            if (mapState.TimelineMapJson != null && mapState.TimelineSampleRate > 0)
            {
                var spans = ReadTimeline(mapState);
                position = (double)TimelineMap.ToOriginal(spans, Math.Clamp((long)Math.Round(position * mapState.TimelineSampleRate), 0, TimelineMap.Validate(spans))) / mapState.TimelineSampleRate;
            }
            await OpenAudioRevisionAsync(original, position);
            _editedTimelineReturn = returnTo;
            _originalTimelinePreview = true;
            NotifySynchronousAudioModeChanged();
            if(AudioVisualizer!=null)AudioVisualizer.IsReadOnly=true;
            ShowStatus("原時間軸聽校：原音訊與原字幕已一起載入。任何臨時文字編輯不會寫入原版；返回時捨棄。");
        }
        catch (Exception e) { await SynchronousAudioErrorAsync(e); }
    }

    [RelayCommand]
    private async Task SynchronousAudioEditedTimeline()
    {
        if (_synchronousAudioBusy || !_originalTimelinePreview || _editedTimelineReturn == null) return;
        try
        {
            var saved = ReadAudioCheckpoint(Path.Combine(_editedTimelineReturn, "state.syncaudio.json"));
            var position = GetVideoPlayerControl()?.VideoPlayer.Position ?? 0;
            if (saved.Audio.TimelineMapJson != null && saved.Audio.TimelineSampleRate > 0)
                position = (double)TimelineMap.ToEdited(ReadTimeline(saved.Audio),
                    Math.Max(0, (long)Math.Round(position * saved.Audio.TimelineSampleRate))) / saved.Audio.TimelineSampleRate;
            await OpenAudioRevisionAsync(_editedTimelineReturn, position);
            _originalTimelinePreview = false;
            NotifySynchronousAudioModeChanged();
            if(AudioVisualizer!=null)AudioVisualizer.IsReadOnly=Se.Settings.General.LockTimeCodes;
            _editedTimelineReturn = null;
            ShowStatus("已恢復修改後音訊與字幕，可接續聽校。");
        }
        catch (Exception e) { await SynchronousAudioErrorAsync(e); }
    }

    private async Task OpenAudioRevisionAsync(string revisionDirectory, double? position = null)
    {
        await WithSynchronousAudioProgressAsync("正在驗證並切換完整音訊／字幕版本…", async token =>
        {
        string verified;
        using (AudioPerformanceLog.Measure("revision.full-validation"))
        {
            var manifest = await Task.Run(() => AudioRevisionStore.ReadManifest(revisionDirectory), token);
            var audio = await GetValidatedAudioSourceAsync(manifest.AudioPath, manifest.AudioSha256, token);
            verified = await Task.Run(() => AudioRevisionStore.ReadVerifiedCheckpoint(revisionDirectory, audio), token);
        }
        var checkpoint = ReadAudioCheckpointJson(verified, revisionDirectory);
        var format = SubtitleFormats.FirstOrDefault(f => f.Name == checkpoint.SubtitleFormat)
            ?? throw new InvalidDataException("版本字幕格式不支援。");
        var subtitle = new Subtitle();
        format.LoadSubtitle(subtitle, checkpoint.SubtitleNative.Replace("\r", "").Split('\n').ToList(), "revision" + format.Extension);
        if (subtitle.Paragraphs.Count != checkpoint.SubtitleCount) throw new InvalidDataException("版本字幕筆數不一致。");
        for (var i = 0; i < subtitle.Paragraphs.Count; i++) subtitle.Paragraphs[i].Bookmark = checkpoint.Bookmarks.ElementAtOrDefault(i);
        var directory = Directory.GetParent(revisionDirectory)!.Parent!.Parent!.FullName;
            await ApplySynchronousAudioOpenAsync(subtitle, format, position.HasValue ? checkpoint.Audio with { PositionSeconds = position.Value } : checkpoint.Audio, checkpoint.SubtitleFileName,
                directory, checkpoint.CutPositionSeconds, token);
            return true;
        });
        _lastRevisionDirectory = revisionDirectory;
        _revisionFingerprint = null;
    }

    [RelayCommand]
    private async Task SynchronousAudioOpenRevision()
    {
        if (_synchronousAudioBusy || Window == null || BlockWhileAuditionPending() || BlockOriginalTimelineEdit()) return;
        try
        {
            var revisionDirectory = _synchronousAudioState != null && !string.IsNullOrEmpty(_synchronousAudioDirectory)
                ? await PickAudioRevisionAsync()
                : null;
            if (revisionDirectory == null && _synchronousAudioState == null)
            {
                var file = await _fileHelper.PickOpenFile(Window, "開啟歷史版本", "版本", "revision.json",
                    _lastRevisionDirectory ?? _synchronousAudioDirectory ?? Se.DataFolder);
                revisionDirectory = string.IsNullOrEmpty(file) ? null : Path.GetDirectoryName(file);
            }
            if (string.IsNullOrEmpty(revisionDirectory)) return;
            if (_synchronousAudioState != null) WriteAudioCheckpoint("current.syncaudio.json", CaptureSynchronousAudioState()!);
            if (!await HasChangesContinue()) return;
            await OpenAudioRevisionAsync(revisionDirectory);
        }
        catch (Exception e) { await SynchronousAudioErrorAsync(e); }
    }
}
