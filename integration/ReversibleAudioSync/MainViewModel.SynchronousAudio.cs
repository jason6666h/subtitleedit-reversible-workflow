using AudioWorkflow;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Nikse.SubtitleEdit.Features.Shared;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.Interfaces;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.Media;
using Nikse.SubtitleEdit.Logic.UndoRedo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main;

// Host adapter only: native SE commands, waveform selection and paired history.
// Audio bytes are processed by the independently testable AudioWorkflow.Core service.
public partial class MainViewModel
{
    [ObservableProperty]
    private bool _isSynchronousAudioRangeSelectionEnabled;

    partial void OnIsSynchronousAudioRangeSelectionEnabledChanged(bool value)
    {
        if (AudioVisualizer != null) AudioVisualizer.IsAudioRangeSelectionMode = value;
        ShowStatus(value
            ? "音訊區間選取已開啟：拖曳音訊範圍後按 Delete 同步刪除；Esc 清除選取。"
            : "已回到 SE 一般字幕拖曳模式。");
    }

    [RelayCommand]
    private void SynchronousAudioToggleRangeSelection()
    {
        if (_synchronousAudioBusy) return;
        IsSynchronousAudioRangeSelectionEnabled = !IsSynchronousAudioRangeSelectionEnabled;
    }

    private SubtitleLineViewModel? GetSynchronousAudioSelection() => AudioVisualizer?.IsAudioRangeSelectionMode == true
        ? AudioVisualizer.AudioRangeSelection : AudioVisualizer?.NewSelectionParagraph;

    // Personal audio shortcuts run before SE's shortcut manager. The handler is focus-aware,
    // so Delete/Escape remain ordinary editing keys outside the waveform and every binding can
    // be reassigned or cleared from the audio shortcut settings.
    internal bool TryHandleSynchronousAudioDelete(KeyEventArgs e) => TryHandleSynchronousAudioShortcut(e);

    private void ClearSynchronousAudioSelection()
    {
        _selectedAudioEditRegion = null;
        ClearNativeSynchronousAudioRange();
    }

    private void ClearNativeSynchronousAudioRange()
    {
        if (AudioVisualizer == null) return;
        AudioVisualizer.NewSelectionParagraph = null;
        AudioVisualizer.AudioRangeSelection = null;
        AudioVisualizer.InvalidateVisual();
    }

    private bool _synchronousAudioBusy;
    private SynchronousAudioState? _synchronousAudioState;
    public string SynchronousAudioProjectStatus => _synchronousAudioFaulted
        ? "⚠ 音檔編修異常｜請重新啟動恢復 ▾"
        : _synchronousAudioState == null
            ? "音訊剪修 ▾"
            : IsAuditionHandoffPending
                ? "● 音檔編修中｜AU 待接回（可取消） ▾"
                : "● 音檔編修中 ▾";
    public bool IsSynchronousAudioProjectActive => _synchronousAudioState != null;

    private void SetSynchronousAudioState(SynchronousAudioState? state)
    {
        _synchronousAudioState = state;
        OnPropertyChanged(nameof(SynchronousAudioProjectStatus));
        OnPropertyChanged(nameof(IsSynchronousAudioProjectActive));
        NotifySynchronousAudioModeChanged();
    }
    private string? _synchronousAudioDirectory;
    private double _synchronousAudioCutPosition;
    private bool _synchronousAudioFaulted;
    private int _synchronousAudioMediaOpenVersion;
    // Test seams keep validation and transaction handling real; no production caller sets them.
    internal Func<SynchronousAudioState, CancellationToken, Task<double>>? SynchronousAudioMediaLoadOverride { get; set; }
    internal Action? SynchronousAudioAfterHistoryPop { get; set; }
    private readonly System.Collections.Generic.Dictionary<string, double> _synchronousAudioCutPositions = new();

    private sealed record AudioCheckpoint(int SchemaVersion, string Kind, SynchronousAudioState Audio,
        string SubtitleFormat, string SubtitleFileName, string SubtitleNative, int SubtitleCount,
        string?[] Bookmarks, double CutPositionSeconds, string? ImmutableRevision = null);

    private SynchronousAudioState? CaptureSynchronousAudioState() => _synchronousAudioState == null ? null :
        _synchronousAudioState with { PositionSeconds = GetVideoPlayerControl()?.VideoPlayer.Position ?? _synchronousAudioState.PositionSeconds };

    private void EndSynchronousAudioSession()
    {
        ReleaseAudioRevisionSource();
        ReleaseSynchronousAudioValidationCache();
        _originalTimelinePreview = false;
        _editedTimelineReturn = null;
        _revisionFingerprint = null;
        _lastRevisionDirectory = null;
        SetSynchronousAudioState(null);
        _synchronousAudioDirectory = null;
        _synchronousAudioCutPosition = 0;
        // Old paired entries must never re-open a different project's media on Ctrl+Z.
        _undoRedoManager.Reset();
        ShowStatus("已離開音訊同步工作階段；先前版本與恢復檔仍保留。");
    }

    [RelayCommand]
    private async Task SynchronousAudioCut()
    {
        if (_synchronousAudioBusy || Window == null) return;
        if (BlockOriginalTimelineEdit()) return;
        if (BlockWhileAuditionPending()) return;
        try
        {
            EnsureSynchronousAudioTextFormat(SelectedSubtitleFormat);
            if (!File.Exists(_videoFileName) || !(Path.GetExtension(_videoFileName).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(_videoFileName).Equals(".wav", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("請先在 SE 載入本機 MP3 或 PCM WAV。");
            if (IsWaveformGenerating) throw new InvalidOperationException("請等目前的波形產生完成再剪輯。");
            if (_subtitleOriginal.Paragraphs.Count > 0 || Subtitles.Any(p => !string.IsNullOrEmpty(p.OriginalText)))
                throw new InvalidOperationException("請先關閉對照原文字幕；目前同步剪輯只處理工作字幕，不改動對照欄。");
            if (Se.Settings.General.CurrentVideoOffsetInMs != 0 || IsSmpteTimingEnabled)
                throw new InvalidOperationException("同步剪輯要求媒體時間偏移為 0，避免字幕與音訊使用不同座標。");
            var selection = GetSynchronousAudioSelection();
            if (selection == null || selection.EndTime.TotalSeconds <= selection.StartTime.TotalSeconds)
                throw new InvalidOperationException("請先在波形下方快捷列開啟「區間選取」，再拖曳要刪除的範圍；可以從字幕上開始並跨越多條字幕。");
            var start = selection.StartTime.TotalSeconds;
            var end = selection.EndTime.TotalSeconds;
            var sourcePath = _videoFileName;
            var identity = _subtitle;
            var format = SelectedSubtitleFormat;
            var initialHash = GetUndoRedoHash();
            var live = GetUpdateSubtitle();
            var snapshot = new Subtitle { Header = live.Header, Footer = live.Footer };
            snapshot.Paragraphs.AddRange(live.Paragraphs.Select(p => new Paragraph(p, false)));
            var crossing = snapshot.Paragraphs.Count(p => p.EndTime.TotalSeconds > start && p.StartTime.TotalSeconds < end &&
                (p.StartTime.TotalSeconds < start || p.EndTime.TotalSeconds > end));
            var answer = await MessageBox.Show(Window, "同步刪除音訊與字幕",
                $"刪除 {start:F3} ～ {end:F3} 秒（{end - start:F3} 秒）。\n\n音檔與目前尚未存檔的字幕將一起更新；跨切點字幕 {crossing} 條只調整時間，不拆段、不新增文字或書籤。完全位於刪除範圍內的字幕一併移除。\n原始音檔不覆寫，可用 SE 復原／重做一起還原。",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != MessageBoxResult.Yes) return;
            EnsureSynchronousAudioContext(identity, initialHash, sourcePath, format);
            if (GetSynchronousAudioSelection()?.StartTime.TotalSeconds != start ||
                GetSynchronousAudioSelection()?.EndTime.TotalSeconds != end)
                throw new InvalidOperationException("確認期間選取範圍已變更，請重新剪輯。");
            var directory = _synchronousAudioDirectory ?? NewAudioWorkDirectory();
            var firstManagedEdit = _synchronousAudioState == null || !HasCompactAudio(_synchronousAudioState);
            SynchronousAudioState? retiredAudio = null;
            await WithSynchronousAudioProgressAsync("正在剪輯音訊並準備同步字幕…", async token =>
            {
              await EnsureSynchronousAudioEditSpaceAsync(sourcePath, directory, firstManagedEdit, token);
              await RunSynchronousAudioTransactionAsync(async (before, rollbackMedia, ct) =>
              {
                var service = new AudioTimelineService(new WorkflowSettings(), AppContext.BaseDirectory);
                var sourceValidation = await GetValidatedAudioSourceAsync(sourcePath,
                    (rollbackMedia ?? throw new InvalidDataException("同步剪輯缺少已驗證來源。")).Sha256, ct);
                using var prepared = await service.CutValidatedAsync(sourceValidation,
                    directory, start, end, ct);
                var cut = prepared.Cut;
                if (!Path.GetExtension(cut.OutputPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("音訊長度修改必須建立新的 PCM WAV，未套用剪輯。");
                ct.ThrowIfCancellationRequested();
                EnsureSynchronousAudioContext(identity, initialHash, sourcePath, format);
                var transformed = SynchronousSubtitleCut.Remove(snapshot, cut.StartSeconds, cut.EndSeconds);
                var oldState = before.SynchronousAudio;
                var baseline = oldState ?? rollbackMedia!;
                var spans = ReadTimelineOrSingleSpan(baseline, cut.OriginalSamples);
                if (TimelineMap.Validate(spans) != cut.OriginalSamples) throw new InvalidDataException("原／修改時間軸長度不一致。");
                baseline = await EnsureCompactBaselineAsync(baseline, cut, spans, directory, ct);
                var originalStart = TimelineMap.ToOriginal(spans, cut.StartSample);
                var originalEnd = TimelineMap.ToOriginal(spans, cut.EndSample);
                baseline = baseline with { TimelineMapJson = System.Text.Json.JsonSerializer.Serialize(spans),
                    TimelineSampleRate = cut.SampleRate,
                    TimelineOriginalFrames = baseline.TimelineOriginalFrames > 0
                        ? baseline.TimelineOriginalFrames : spans[^1].End };
                var session = baseline.SessionId;
                var revisionId = Guid.NewGuid().ToString("N");
                var deletedRegion = new AudioEditRegion(revisionId, AudioEditRegionKind.Deleted,
                    (double)originalStart / cut.SampleRate, (double)originalEnd / cut.SampleRate);
                var target = baseline with { FileName = cut.OutputPath, Sha256 = cut.OutputSha256,
                    PositionSeconds = GetSynchronousAudioPreviewPosition(cut.StartSeconds), RevisionId = revisionId,
                    SessionId = session,
                    TimelineMapJson = System.Text.Json.JsonSerializer.Serialize(TimelineMap.Delete(spans, cut.StartSample, cut.EndSample)),
                    TimelineSampleRate = cut.SampleRate, AudioEditRegionsJson = AddAudioEditRegion(baseline, deletedRegion),
                    TimelineOriginalFrames = baseline.TimelineOriginalFrames };
                var baselineSnapshot = UndoRedoItem.Clone(before)!;
                baselineSnapshot.SynchronousAudio = baseline;
                // Recovery of the previous pair is available even if the process exits mid-switch.
                _synchronousAudioDirectory = directory;
                WriteAudioCheckpoint("before.syncaudio.json", baseline, snapshot: baselineSnapshot);
                    CacheValidatedAudioSource(prepared.DetachValidation());
                    await LoadSynchronousAudioAsync(target, ct);
                    ct.ThrowIfCancellationRequested();
                    EnsureSynchronousAudioContext(identity, initialHash, target.FileName, format);
                    ReplaceSubtitles(transformed.Paragraphs.Select(p => new SubtitleLineViewModel(p, SelectedSubtitleFormat)));
                    SetSynchronousAudioState(target);
                    _synchronousAudioCutPosition = cut.StartSeconds;
                    _updateAudioVisualizer = true;
                    ClearSynchronousAudioSelection();
                    if (Subtitles.Count > 0)
                        SelectAndScrollToRow(Math.Max(0, Subtitles.ToList().FindIndex(p => p.EndTime.TotalSeconds >= cut.StartSeconds)));
                    WriteAudioCheckpoint("current.syncaudio.json", target);
                    // Prior text-only history belongs to the same audio baseline. No hash change:
                    // baseline RevisionId is empty, just like pre-integration snapshots.
                    if (oldState == null)
                        foreach (var item in _undoRedoManager.UndoList.Concat(_undoRedoManager.RedoList))
                            item.SynchronousAudio ??= baseline;
                    if (_undoRedoManager.UndoList.LastOrDefault()?.Hash != before.Hash)
                        _undoRedoManager.Do(baselineSnapshot);
                    _undoRedoManager.Do(MakeUndoRedoObject($"同步刪除 {cut.EndSeconds - cut.StartSeconds:F3} 秒"));
                    _synchronousAudioCutPositions[target.RevisionId] = cut.StartSeconds;
                    retiredAudio = baseline;
                    ShowStatus($"同步剪輯完成；已回到剪接處前 2 秒。Ctrl+Z 同時復原音訊與字幕。工作檔：{cut.OutputPath}");
              }, token);
                return true;
            });
            TryRetireCompactWorkingAudio(retiredAudio);
        }
        catch (OperationCanceledException) { ShowStatus("已取消同步剪輯，原音檔與字幕未變更。"); }
        catch (Exception e) { await SynchronousAudioErrorAsync(e); }
    }

    private async Task<bool> PerformSynchronousAudioHistoryAsync(bool redo)
    {
        if (BlockOriginalTimelineEdit()) return false;
        if (_synchronousAudioBusy) return false;
        if (BlockWhileAuditionPending()) return false;
        var target = redo ? _undoRedoManager.PeekRedo() : _undoRedoManager.PeekUndo();
        if (target == null) return false;
        try
        {
            if (target.SynchronousAudio == null || target.SynchronousAudio.SessionId != _synchronousAudioState?.SessionId)
                throw new InvalidOperationException("此歷史項目不屬於目前音訊工作階段，未進行部分復原。");
            SynchronousAudioState? retiredAudio = null;
            await WithSynchronousAudioProgressAsync(redo ? "正在重做音訊與字幕…" : "正在復原音訊與字幕…", async token =>
            {
                await RunSynchronousAudioTransactionAsync(async (before, rollbackMedia, ct) =>
                {
                    if (target.SynchronousAudio.FileName != _videoFileName)
                        await LoadSynchronousAudioAsync(target.SynchronousAudio, ct);
                    else
                        await GetValidatedAudioSourceAsync(target.SynchronousAudio.FileName, target.SynchronousAudio.Sha256, ct);
                    ct.ThrowIfCancellationRequested();
                    var stillTarget = redo ? _undoRedoManager.PeekRedo() : _undoRedoManager.PeekUndo();
                    if (GetUndoRedoHash() != before.Hash || stillTarget?.Hash != target.Hash ||
                        stillTarget.SynchronousAudio != target.SynchronousAudio)
                        throw new InvalidOperationException("歷史或工作階段已變更，未提交過期復原。");
                    // The history stack is not popped until target audio has passed validation/open.
                    // Restore first only after capturing the manager's expected live hash is unsafe;
                    // Undo must see the original live state, so commit its stack immediately here.
                    var applied = redo ? _undoRedoManager.Redo() : _undoRedoManager.Undo();
                    if (applied == null) throw new InvalidOperationException("歷史項目已變更。");
                    SynchronousAudioAfterHistoryPop?.Invoke();
                    RestoreUndoRedoState(applied);
                    _synchronousAudioCutPosition = string.IsNullOrEmpty(applied.SynchronousAudio?.RevisionId) ? 0 :
                        _synchronousAudioCutPositions.GetValueOrDefault(applied.SynchronousAudio.RevisionId, _synchronousAudioCutPosition);
                    _updateAudioVisualizer = true;
                    ClearSynchronousAudioSelection();
                    TrySaveAudioCheckpoint();
                    retiredAudio = before.SynchronousAudio;
                    if (redo) ShowRedoStatus(); else ShowUndoStatus();
                }, token);
                return true;
            });
            TryRetireCompactWorkingAudio(retiredAudio);
            return true;
        }
        catch (OperationCanceledException) { ShowStatus("已取消歷史切換。"); return false; }
        catch (Exception e) { await SynchronousAudioErrorAsync(e); return false; }
    }

    internal static void EnsureSynchronousAudioTextFormat(SubtitleFormat format)
    {
        if (format is IBinaryPersistableSubtitle)
            throw new InvalidOperationException("音訊工作階段不能以文字恢復二進位字幕；請先明確轉換為 SRT 或 ASS。");
    }

    internal static double GetSynchronousAudioPreviewPosition(double cutSeconds) => Math.Max(0, cutSeconds - 2);

    private void EnsureSynchronousAudioContext(Subtitle identity, int hash, string mediaFile, SubtitleFormat format)
    {
        if (!ReferenceEquals(_subtitle, identity) || GetUndoRedoHash() != hash ||
            _videoFileName != mediaFile || SelectedSubtitleFormat != format)
            throw new InvalidOperationException("字幕或媒體已變更，未套用過期操作。");
    }

    private static async Task<(FileStream Stream, string Hash)> OpenSynchronousAudioReadGuardAsync(
        string fileName, string? expectedHash, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(fileName)) throw new InvalidDataException("工作階段音檔必須是本機絕對路徑。");
        var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        try
        {
            // Hash off the dispatcher even when reads complete synchronously from the OS cache.
            var hash = await Task.Run(() => AudioContentHash.ComputeAsync(stream, token), token).ConfigureAwait(false);
            if (expectedHash != null && !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("音檔已被外部修改，不能套用原來的字幕時間或歷史。");
            stream.Position = 0;
            return (stream, hash);
        }
        catch { stream.Dispose(); throw; }
    }

    internal static async Task<FileStream> OpenSynchronousAudioGuardAsync(SynchronousAudioState state, CancellationToken token)
    {
        if (!double.IsFinite(state.PositionSeconds) || state.PositionSeconds < 0 || string.IsNullOrWhiteSpace(state.Sha256))
            throw new InvalidDataException("音訊工作階段的雜湊或播放位置無效。");
        return (await OpenSynchronousAudioReadGuardAsync(state.FileName, state.Sha256, token)).Stream;
    }

    private static async Task ValidateSynchronousAudioAsync(SynchronousAudioState state, CancellationToken token)
    {
        using var guard = await OpenSynchronousAudioGuardAsync(state, token);
    }

    // The session cache retains the validated read handle through the subtitle/history commit
    // and across repeated original/edited timeline switches.
    private async Task LoadSynchronousAudioAsync(SynchronousAudioState state, CancellationToken token, string? directoryOverride = null)
    {
        using var loadTiming = AudioPerformanceLog.Measure("media.load-total");
        var compactDirectory = directoryOverride ?? _synchronousAudioDirectory;
        if (!File.Exists(state.FileName) && HasCompactAudio(state))
        {
            if (compactDirectory == null) throw new InvalidDataException("找不到節省空間音訊的專案資料夾。");
            using (AudioPerformanceLog.Measure("media.compact-rebuild"))
                await EnsureCompactAudioAvailableAsync(state, compactDirectory, token);
        }
        var cached = TryGetValidatedAudioSource(state.FileName, state.Sha256);
        if (cached == null)
        {
            using (AudioPerformanceLog.Measure("media.full-hash"))
                cached = await GetValidatedAudioSourceAsync(state.FileName, state.Sha256, token);
        }
        var expectedDuration = Path.GetExtension(state.FileName).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? WaveInfo.Read(state.FileName).DurationSeconds : (double?)null;
        try
        {
            _synchronousAudioMediaOpenVersion++;
            double duration;
            if (SynchronousAudioMediaLoadOverride != null)
            {
                duration = await SynchronousAudioMediaLoadOverride(state, token);
                _videoFileName = state.FileName;
            }
            else
            {
                var player = GetVideoPlayerControl() ?? throw new IOException("沒有可用的 SE 播放器。");
                RememberVerifiedWaveform();
                Pause();
                player.Close();
                // mpv CloseFile sends stop but observed duration is cleared asynchronously.
                // Wait for zero BEFORE loading: otherwise WAV LoadFile can reuse the old end bound.
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (player.VideoPlayer.Duration > 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(25, token);
                if (player.VideoPlayer.Duration > 0) throw new IOException("播放器未完成關閉，拒絕使用上一音檔的長度。");
                if (AudioVisualizer != null) { AudioVisualizer.WavePeaks = null; AudioVisualizer.SetSpectrogram(null); }
                token.ThrowIfCancellationRequested();
                // Only reuse a waveform after this session has generated it for this exact
                // validated audio hash. Switching back must not decode the entire file again.
                await VideoOpenFile(state.FileName, startPositionSeconds: state.PositionSeconds,
                    forceWaveform: !_verifiedWaveformSources.Contains(state.FileName+"|"+state.Sha256));
                deadline = DateTime.UtcNow.AddSeconds(5);
                do
                {
                    token.ThrowIfCancellationRequested();
                    duration = player.VideoPlayer.Duration;
                    if (double.IsFinite(duration) && duration > 0 &&
                        (!expectedDuration.HasValue || Math.Abs(duration - expectedDuration.Value) <= 0.05)) break;
                    await Task.Delay(25, token);
                } while (DateTime.UtcNow < deadline);
                Pause();
                if (!ReferenceEquals(player, GetVideoPlayerControl()) || player.VideoPlayer.FileName != state.FileName)
                    throw new IOException("載入期間播放器或媒體已變更。");
            }
            token.ThrowIfCancellationRequested();
            if (_videoFileName != state.FileName || !double.IsFinite(duration) || duration <= 0 ||
                (expectedDuration.HasValue && Math.Abs(duration - expectedDuration.Value) > 0.05))
                throw new IOException("播放器音檔長度未通過驗證，字幕與歷史未提交。");
        }
        catch { throw; }
    }

    private async Task RunSynchronousAudioTransactionAsync(
        Func<UndoRedoItem, SynchronousAudioState?, CancellationToken, Task> apply, CancellationToken token)
    {
        var before = MakeUndoRedoObject("同步操作前");
        var snapshot = CaptureSynchronousAudioHostSnapshot();
        FileStream? sourceGuard = null;
        var rollbackMedia = before.SynchronousAudio;
        try
        {
            // Pin the previous local media even for a first cut/open from a legacy document.
            if (!string.IsNullOrEmpty(snapshot.MediaFile))
            {
                if (rollbackMedia != null)
                    await GetValidatedAudioSourceAsync(snapshot.MediaFile, rollbackMedia.Sha256, token);
                else
                {
                    var source = await OpenSynchronousAudioReadGuardAsync(snapshot.MediaFile, null, token);
                    sourceGuard = source.Stream;
                    rollbackMedia = new SynchronousAudioState(snapshot.MediaFile, source.Hash,
                        GetVideoPlayerControl()?.Position ?? 0, "", Guid.NewGuid().ToString("N"));
                }
            }

            EnsureSynchronousAudioContext(snapshot.Identity, before.Hash, snapshot.MediaFile, snapshot.Format);
            token.ThrowIfCancellationRequested();
            try
            {
                await apply(before, rollbackMedia, token);
            }
            catch (Exception failure)
            {
                var attemptedDirectory = _synchronousAudioDirectory;
                try
                {
                    await snapshot.RestoreAsync(before, rollbackMedia);
                }
                catch (Exception rollbackFailure)
                {
                    _synchronousAudioFaulted = true;
                    NotifySynchronousAudioModeChanged();
                    throw new AggregateException(
                        "同步操作及回復都失敗；編輯已停用。請保留工作檔並重新啟動 SE 恢復。",
                        failure, rollbackFailure);
                }

                // Only publish a recovered pair once document AND media rollback succeeded.
                if (rollbackMedia != null && (snapshot.Directory ?? attemptedDirectory) is { } recoveryDirectory)
                {
                    try
                    {
                        WriteAudioCheckpoint("current.syncaudio.json", rollbackMedia, before, recoveryDirectory);
                    }
                    catch (Exception checkpointFailure)
                    {
                        throw new AggregateException(
                            "已還原編輯與歷史，但恢復檔寫入失敗；請勿使用該 current 檔恢復。",
                            failure, checkpointFailure);
                    }
                }
                throw;
            }
        }
        finally
        {
            sourceGuard?.Dispose();
        }
    }

    private async Task<T> WithSynchronousAudioProgressAsync<T>(string text, Func<CancellationToken, Task<T>> operation)
    {
        using var operationTiming = AudioPerformanceLog.Measure(text);
        if (_synchronousAudioBusy || Window == null) throw new InvalidOperationException("另一個同步操作仍在進行。");
        _synchronousAudioBusy = true;
        using var cancel = new CancellationTokenSource();
        var owner = Window;
        var ownerEnabled = owner.IsEnabled;
        Window? progress = null;
        Task? shown = null;
        var finished = false;
        try
        {
          _undoRedoManager.StopChangeDetection();
          var button = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Right };
          progress = new Window { Title = "SE 音訊同步剪修", Width = 480, Height = 185, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 16,
                Children = { new TextBlock { Text = text }, new ProgressBar { IsIndeterminate = true }, button } } };
        button.Click += (_, _) => { button.IsEnabled = false; cancel.Cancel(); };
        progress.Closing += (_, e) => { if (!finished) { e.Cancel = true; cancel.Cancel(); } };
          shown = progress.ShowDialog(owner);
          Pause();
          return await operation(cancel.Token);
        }
        finally
        {
            try
            {
                finished = true;
                progress?.Close();
                if (shown != null) await shown.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception cleanupFailure) { Se.LogError(cleanupFailure, "Synchronous audio progress cleanup"); }
            finally
            {
                _synchronousAudioBusy = _synchronousAudioFaulted;
                owner.IsEnabled = ownerEnabled && !_synchronousAudioFaulted;
                if (!_synchronousAudioFaulted) _undoRedoManager.StartChangeDetection();
            }
        }
    }

    [RelayCommand]
    private void SynchronousAudioPreview()
    {
        if (_synchronousAudioBusy || _synchronousAudioState == null) return;
        var player = GetVideoPlayerControl();
        if (player == null) return;
        player.Position = GetSynchronousAudioPreviewPosition(_synchronousAudioCutPosition);
        Play();
    }

    private void WriteAudioCheckpoint(string name, SynchronousAudioState state, UndoRedoItem? snapshot = null, string? directory = null)
    {
        directory ??= _synchronousAudioDirectory;
        if (directory == null) return;
        EnsureSynchronousAudioTextFormat(SelectedSubtitleFormat);
        var subtitle = snapshot == null ? GetUpdateSubtitle() : new Subtitle { Header = snapshot.SubtitleHeader, Footer = snapshot.SubtitleFooter };
        if (snapshot != null) subtitle.Paragraphs.AddRange(snapshot.Subtitles.Where(p => !p.IsReferenceOnly).Select(p => p.ToParagraph(SelectedSubtitleFormat)));
        var checkpoint = new AudioCheckpoint(1, "se-synchronous-audio", state, SelectedSubtitleFormat.Name,
            snapshot?.SubtitleFileName ?? _subtitleFileName ?? "", subtitle.ToText(SelectedSubtitleFormat),
            subtitle.Paragraphs.Count, subtitle.Paragraphs.Select(p => p.Bookmark).ToArray(), _synchronousAudioCutPosition);
        if (!_originalTimelinePreview) SaveImmutableAudioRevision(checkpoint, subtitle, directory, name.StartsWith("before", StringComparison.Ordinal) ? "before" : "edit");
        JsonStore.Write(Path.Combine(directory, name), checkpoint with { ImmutableRevision = _lastRevisionDirectory });
    }

    private void TrySaveAudioCheckpoint()
    {
        if (_synchronousAudioState == null) return;
        try { WriteAudioCheckpoint("current.syncaudio.json", CaptureSynchronousAudioState()!); }
        catch (Exception e) { Se.LogError(e, "Audio checkpoint"); ShowStatus("目前編輯已套用，但工作階段保存失敗：" + e.Message); }
    }

    [RelayCommand]
    private void SynchronousAudioSave()
    {
        if (BlockOriginalTimelineEdit()) return;
        if (_synchronousAudioBusy || _synchronousAudioState == null) { ShowStatus("尚未建立音訊同步工作階段。"); return; }
        try { WriteAudioCheckpoint("current.syncaudio.json", CaptureSynchronousAudioState()!); ShowStatus("已保存音檔版本及目前字幕：" + Path.Combine(_synchronousAudioDirectory!, "current.syncaudio.json")); }
        catch (Exception e) { ShowStatus("保存失敗：" + e.Message); }
    }

    [RelayCommand]
    private async Task SynchronousAudioOpen()
    {
        if (BlockOriginalTimelineEdit()) return;
        if (BlockWhileAuditionPending()) return;
        if (_synchronousAudioBusy || Window == null) return;
        try
        {
            var file = await _fileHelper.PickOpenFile(Window, "開啟音訊／字幕工作階段", "SE 音訊工作階段", "*.syncaudio.json", _synchronousAudioDirectory ?? Se.DataFolder);
            if (string.IsNullOrEmpty(file) || !await HasChangesContinue()) return;
            await RestoreAudioCheckpointAsync(file);
        }
        catch (OperationCanceledException) { ShowStatus("已取消恢復。"); }
        catch (Exception e) { await SynchronousAudioErrorAsync(e); }
    }

    internal async Task ApplySynchronousAudioOpenAsync(Subtitle subtitle, SubtitleFormat format,
        SynchronousAudioState audio, string subtitleFileName, string directory, double cutPosition, CancellationToken token)
    {
        EnsureSynchronousAudioTextFormat(format);
        // Checkpoints use media-relative seconds. Do not silently carry another project's offset.
        if (Se.Settings.General.CurrentVideoOffsetInMs != 0 || IsSmpteTimingEnabled)
            throw new InvalidOperationException("請先將媒體時間偏移設為 0 並關閉 SMPTE 模式，再恢復音訊工作階段。");
        await RunSynchronousAudioTransactionAsync(async (before, rollbackMedia, ct) =>
        {
                var identity = _subtitle;
                var oldFormat = SelectedSubtitleFormat;
                await LoadSynchronousAudioAsync(audio, ct, directory);
                ct.ThrowIfCancellationRequested();
                EnsureSynchronousAudioContext(identity, before.Hash, audio.FileName, oldFormat);
                _changingFormatProgrammatically = true;
                try { SelectedSubtitleFormat = format; }
                finally { _changingFormatProgrammatically = false; }
                _subtitleOriginal = new Subtitle();
                _subtitleFileNameOriginal = string.Empty;
                _subtitleOriginalBeforeEditMode = null;
                IsEditOriginalMode = false;
                ShowColumnOriginalText = false;
                IsOriginalReadOnly = false;
                IsShowingOriginalNonMatchingLines = false;
                ClearSecondarySubtitle();
                _subtitle = subtitle;
                _subtitleFileName = subtitleFileName;
                _converted = false;
                _saveAsFileNameSuggestion = null;
                ReplaceSubtitles(subtitle.Paragraphs.Select(p => new SubtitleLineViewModel(p, format)));
                SetSynchronousAudioState(audio);
                _synchronousAudioDirectory = directory;
                _synchronousAudioCutPosition = cutPosition;
                _undoRedoManager.Reset();
                _undoRedoManager.Do(MakeUndoRedoObject("恢復音訊工作階段"));
                _synchronousAudioCutPositions[audio.RevisionId] = cutPosition;
                _updateAudioVisualizer = true;
                ClearSynchronousAudioSelection();
                if (Subtitles.Count > 0) SelectAndScrollToRow(0);
                ShowStatus("已恢復配對工作階段；重新開啟後的復原歷史從這裡開始。");
        }, token);
    }

    [RelayCommand]
    private async Task SynchronousAudioExport()
    {
        if (BlockOriginalTimelineEdit() || BlockWhileAuditionPending()) return;
        if (_synchronousAudioBusy || _synchronousAudioState == null || Window == null) return;
        try
        {
            WriteAudioCheckpoint("current.syncaudio.json", CaptureSynchronousAudioState()!);
            var output = await _fileHelper.PickSaveFile(Window, ".wav", "FINAL.wav", "輸出配對 WAV 與字幕（不覆寫既有檔案）");
            if (string.IsNullOrEmpty(output)) return;
            if (SelectedSubtitleFormat is IBinaryPersistableSubtitle)
                throw new InvalidOperationException("配對輸出目前支援文字字幕格式；請先用 SE 轉換為 SRT 或 ASS，再輸出配對成品。");
            var state = CaptureSynchronousAudioState()!;
            var live = GetUpdateSubtitle();
            if (Path.GetExtension(state.FileName).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                var wave = WaveInfo.Read(state.FileName);
                if (state.TimelineMapJson != null && TimelineMap.Validate(ReadTimeline(state)) != wave.SampleCount)
                    throw new InvalidDataException("時間軸映射與輸出音訊長度不一致，拒絕輸出。");
                if (live.Paragraphs.Any(p => p.StartTime.TotalMilliseconds < 0 || p.EndTime.TotalMilliseconds <= p.StartTime.TotalMilliseconds ||
                    p.EndTime.TotalSeconds > wave.DurationSeconds + 0.001))
                    throw new InvalidDataException("字幕包含負時間、零／負長度，或超出音訊結尾。請先在 SE 修正後再輸出。");
            }
            var subtitle = live.ToText(SelectedSubtitleFormat);
            var extension = SelectedSubtitleFormat.Extension;
            var checkpoint = new AudioCheckpoint(1, "se-synchronous-audio", state,
                SelectedSubtitleFormat.Name, _subtitleFileName ?? "", subtitle, live.Paragraphs.Count,
                live.Paragraphs.Select(p => p.Bookmark).ToArray(), _synchronousAudioCutPosition);
            var checkpointJson = System.Text.Json.JsonSerializer.Serialize(checkpoint);
            await WithSynchronousAudioProgressAsync("正在逐樣本驗證並輸出配對成品…", async token =>
            {
                await VerifyCurrentSynchronousAudioAsync(state, _synchronousAudioDirectory!, token);
                var service = new AudioTimelineService(new WorkflowSettings(), AppContext.BaseDirectory);
                var result = await service.ExportPairAsync(new AudioExportRequest(state.FileName,
                    Path.GetDirectoryName(output)!, subtitle, extension, state.Sha256, checkpointJson,
                    Path.GetFileNameWithoutExtension(output),
                    live.Paragraphs.Select(p => new SubtitleInterval(p.StartTime.TotalSeconds, p.EndTime.TotalSeconds)).ToArray(),
                    state.TimelineMapJson == null ? null : TimelineMap.Validate(ReadTimeline(state)),
                    state.TimelineSampleRate > 0 ? state.TimelineSampleRate : null), token);
                ShowStatus("已輸出配對成品（含驗證報告）：" + result.DirectoryPath);
                return true;
            });
        }
        catch (OperationCanceledException) { ShowStatus("已取消輸出；未完成的 .pending 資料夾不視為成品。"); }
        catch (Exception e) { await SynchronousAudioErrorAsync(e); }
    }

    private async Task SynchronousAudioErrorAsync(Exception e)
    {
        Se.LogError(e, "SE synchronous audio");
        ShowStatus("音訊同步剪修：" + e.Message);
        if (Window != null) await MessageBox.Show(Window, "音訊同步剪修", e.Message, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
