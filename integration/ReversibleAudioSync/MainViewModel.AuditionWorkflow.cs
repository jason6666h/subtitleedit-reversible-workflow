using AudioWorkflow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Features.Shared;
using Nikse.SubtitleEdit.Logic.UndoRedo;
using Nikse.SubtitleEdit.Logic.Config;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main;

/// <summary>Explicit round trip: immutable SE baseline → independent AU copy → new SE revision.</summary>
public partial class MainViewModel
{
    private sealed record PendingAudition(AuditionHandoff Files, Subtitle Document,
        SynchronousAudioState Baseline, double Position, int Row);

    private PendingAudition? _pendingAudition;
    [ObservableProperty] private bool _isAuditionHandoffPending;
    partial void OnIsAuditionHandoffPendingChanged(bool value) =>
        NotifySynchronousAudioModeChanged();
    internal Action<string, string>? AuditionLaunchOverride { get; set; }
    internal SettingsManager? AuditionSettingsManagerOverride { get; set; }
    internal AuditionHandoff? PendingAuditionFiles => _pendingAudition?.Files;

    private SettingsManager GetAuditionSettingsManager() =>
        AuditionSettingsManagerOverride ?? new SettingsManager(string.Empty);

    [RelayCommand]
    private async Task SynchronousAudioRecoverAudition()
    {
        if (_synchronousAudioBusy || _pendingAudition != null || Window == null || BlockOriginalTimelineEdit()) return;
        try
        {
            var file=await _fileHelper.PickOpenFile(Window,"接回先前已儲存的 AU 局部修音（先開啟原工作階段）","AU 交接紀錄","handoff.json",_synchronousAudioDirectory ?? Se.DataFolder);
            if(string.IsNullOrEmpty(file))return;
            await RecoverAuditionHandoffAsync(file);
            ShowStatus("已恢復 AU 交接。請按「完成修音，繼續聽校」；不會重新建立或覆蓋修音片段。");
        }
        catch(Exception e){await SynchronousAudioErrorAsync(e);}
    }

    internal async Task RecoverAuditionHandoffAsync(string file)
    {
        if (_synchronousAudioBusy || _pendingAudition != null || _originalTimelinePreview) throw new InvalidOperationException("請先完成目前操作。");
        var baseline=CaptureSynchronousAudioState() ?? throw new InvalidOperationException("請先開啟交給 AU 前的音訊／字幕工作階段。");
        var files=JsonStore.Read<AuditionHandoff>(file);
        if(files.Region==null || !baseline.Sha256.Equals(files.BeforeHash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目前音訊與 AU 交接基準不同，不能套用這段修音。請先恢復交接前的 current.syncaudio.json。");
        using var guard=await AudioTimelineService.AcquireFileValidationAsync(files.BeforePath,files.BeforeHash,CancellationToken.None);
        if(!File.Exists(files.EditPath))throw new FileNotFoundException("找不到 AU 修音片段。",files.EditPath);
        TryDeleteAuditionDismissedMarker(file);
        _pendingAudition=new PendingAudition(files,_subtitle,baseline,(double)files.Region.StartSample/files.Timeline.SampleRate,SelectedSubtitleIndex??0);
        IsAuditionHandoffPending=true;
    }

    private static string AuditionDismissedMarker(string handoffFile) =>
        Path.Combine(Path.GetDirectoryName(handoffFile)!, "handoff.dismissed");

    private static void TryDeleteAuditionDismissedMarker(string handoffFile)
    {
        try
        {
            var marker = AuditionDismissedMarker(handoffFile);
            if (File.Exists(marker)) File.Delete(marker);
        }
        catch { /* marker is advisory only; manual recovery must still work */ }
    }

    private static void MarkAuditionRecoveryDismissed(AuditionHandoff files)
    {
        try
        {
            var handoff = Path.Combine(Path.GetDirectoryName(files.EditPath)!, "handoff.json");
            File.WriteAllText(AuditionDismissedMarker(handoff), DateTime.UtcNow.ToString("O"));
        }
        catch { /* never block cancellation because an advisory marker could not be written */ }
    }

    internal async Task TryOfferPendingAuditionRecoveryAsync()
    {
        if (Window == null || _pendingAudition != null || _originalTimelinePreview ||
            _synchronousAudioState == null || string.IsNullOrEmpty(_synchronousAudioDirectory) ||
            !Directory.Exists(_synchronousAudioDirectory)) return;

        string? candidate = null;
        DateTime newest = DateTime.MinValue;
        foreach (var folder in Directory.EnumerateDirectories(_synchronousAudioDirectory, "audition-*",
                     SearchOption.TopDirectoryOnly))
        {
            var handoff = Path.Combine(folder, "handoff.json");
            if (!File.Exists(handoff) || File.Exists(AuditionDismissedMarker(handoff))) continue;
            try
            {
                var files = JsonStore.Read<AuditionHandoff>(handoff);
                if (files.Region == null || !File.Exists(files.EditPath) || !File.Exists(files.BeforePath) ||
                    !string.Equals(files.BeforeHash, _synchronousAudioState.Sha256,
                        StringComparison.OrdinalIgnoreCase)) continue;
                var time = File.GetLastWriteTimeUtc(handoff);
                if (time <= newest) continue;
                newest = time;
                candidate = handoff;
            }
            catch
            {
                // A malformed stale handoff is ignored here. Manual recovery still reports its exact error.
            }
        }

        if (candidate == null) return;
        var pending = JsonStore.Read<AuditionHandoff>(candidate);
        var start = pending.Region == null ? 0d :
            (double)pending.Region.StartSample / pending.Timeline.SampleRate;
        var end = pending.Region == null ? 0d :
            (double)(pending.Region.StartSample + pending.Region.RepairSamples) / pending.Timeline.SampleRate;
        var answer = await MessageBox.Show(Window, "發現未完成的 AU 修音",
            $"找到與目前音訊完全相符的 AU 修音交接。\n\n修音範圍：{start:F3} ～ {end:F3} 秒\n修音副本仍存在。\n\n要接回這次修音並繼續嗎？\n選「否」只略過這次提示，不會刪除任何檔案。",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != MessageBoxResult.Yes) return;
        await RecoverAuditionHandoffAsync(candidate);
        ShowStatus("已恢復上次未完成的 AU 修音。請按「完成 AU 修音，繼續聽校」；原音訊與字幕尚未變更。");
    }

    internal (double Start, double End) GetAuditionLocalRange()
    {
        if (Se.Settings.General.CurrentVideoOffsetInMs != 0 || IsSmpteTimingEnabled)
            throw new InvalidOperationException("局部修音要求媒體時間偏移為 0 並關閉 SMPTE，以免範圍錯位。");
        var range = GetSynchronousAudioSelection();
        if (range != null && range.EndTime.TotalSeconds != range.StartTime.TotalSeconds)
            return (range.StartTime.TotalSeconds, range.EndTime.TotalSeconds);
        var row = SelectedSubtitleIndex;
        if (row.HasValue && row >= 0 && row < Subtitles.Count)
            return (Subtitles[row.Value].StartTime.TotalSeconds, Subtitles[row.Value].EndTime.TotalSeconds);
        throw new InvalidOperationException("請先選取音訊區間，或選取一條字幕，再按「交由 AU 局部修音」。");
    }

    [RelayCommand]
    private async Task SynchronousAudioSetAuditionPath()
    {
        if (_synchronousAudioBusy || Window == null) return;
        try
        {
            var path = await PickAuditionPathAsync();
            if (!string.IsNullOrEmpty(path)) ShowStatus("已設定 Adobe Audition：" + path);
        }
        catch (Exception e) { await SynchronousAudioErrorAsync(e); }
    }

    private async Task<string?> PickAuditionPathAsync()
    {
        if (Window == null) return null;
        var manager = GetAuditionSettingsManager();
        var settings = manager.Load();
        var startFolder = File.Exists(settings.AuditionPath)
            ? Path.GetDirectoryName(settings.AuditionPath)
            : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var path = await _fileHelper.PickOpenFile(Window, "選擇 Adobe Audition.exe", "執行檔", "*.exe",
            suggestedStartFolder: startFolder);
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !Path.GetFileName(path).Contains("Audition", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("請選擇 Adobe Audition 的 Audition.exe。", path);
        settings.AuditionPath = Path.GetFullPath(path);
        manager.Save(settings);
        return settings.AuditionPath;
    }


    [RelayCommand]
    private async Task SynchronousAudioOpenInAudition()
    {
        if (BlockOriginalTimelineEdit()) return;
        if (_synchronousAudioBusy || Window == null) return;
        if (_pendingAudition != null)
        {
            ShowStatus("已有 AU 修音待接回；請先完成或取消交接。修音檔：" + _pendingAudition.Files.EditPath);
            return;
        }
        try
        {
            EnsureSynchronousAudioTextFormat(SelectedSubtitleFormat);
            if (!File.Exists(_videoFileName) || !(Path.GetExtension(_videoFileName).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(_videoFileName).Equals(".wav", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("請先在 SE 載入 MP3 或整數 PCM WAV；可直接使用 SE 已剪修的版本。");
            if (IsWaveformGenerating) throw new InvalidOperationException("請等波形產生完成後再交給 AU。");
            var settings = GetAuditionSettingsManager().Load();
            if (!File.Exists(settings.AuditionPath))
            {
                if (await PickAuditionPathAsync() == null) return;
                settings = GetAuditionSettingsManager().Load();
            }
            var identity = _subtitle;
            var source = _videoFileName;
            var format = SelectedSubtitleFormat;
            var hash = GetUndoRedoHash();
            var position = GetVideoPlayerControl()?.VideoPlayer.Position ?? 0;
            var row = SelectedSubtitleIndex ?? 0;
            var range = GetAuditionLocalRange();
            var directory = _synchronousAudioDirectory ?? NewAudioWorkDirectory();
            await WithSynchronousAudioProgressAsync("正在擷取 AU 局部修音片段（前後各最多 2 秒）…", async token =>
            {
                await EnsureSynchronousAudioEditSpaceAsync(source, directory, firstManagedEdit: false, token);
                await RunSynchronousAudioTransactionAsync(async (before, rollbackMedia, ct) =>
                {
                    var original = before.SynchronousAudio ?? rollbackMedia!;
                    var service = new AuditionHandoffService(settings, AppContext.BaseDirectory);
                    AuditionHandoff files;
                    var reuseManagedBaseline = before.SynchronousAudio != null &&
                        Path.GetExtension(source).Equals(".wav", StringComparison.OrdinalIgnoreCase) &&
                        IsManagedAudioPath(source, directory);
                    if (reuseManagedBaseline)
                    {
                        var validated = await GetValidatedAudioSourceAsync(original.FileName, original.Sha256, ct);
                        files = await Task.Run(() => service.CreateLocalFromValidatedAsync(validated, directory,
                            range.Start, range.End, ct), ct);
                    }
                    else
                        files = await Task.Run(() => service.CreateLocalAsync(source, directory,
                            range.Start, range.End, ct), ct);
                    ct.ThrowIfCancellationRequested();
                    EnsureSynchronousAudioContext(identity, hash, source, format);
                    var baseline = original with { FileName = files.BeforePath, Sha256 = files.BeforeHash, PositionSeconds = position };
                    if (baseline.TimelineMapJson == null || baseline.TimelineSampleRate <= 0)
                        baseline = baseline with {
                            TimelineMapJson = System.Text.Json.JsonSerializer.Serialize(new[] { new SourceSpan(0, files.Timeline.SampleCount) }),
                            TimelineSampleRate = files.Timeline.SampleRate,
                            TimelineOriginalFrames = files.Timeline.SampleCount };
                    var snapshot = UndoRedoItem.Clone(before)!;
                    snapshot.SynchronousAudio = baseline;
                    _synchronousAudioDirectory = directory;
                    await GetValidatedAudioSourceAsync(baseline.FileName, baseline.Sha256, ct);
                    WriteAudioCheckpoint("before.syncaudio.json", baseline, snapshot);
                    if (!reuseManagedBaseline)
                    {
                        await LoadSynchronousAudioAsync(baseline, ct);
                        ct.ThrowIfCancellationRequested();
                        EnsureSynchronousAudioContext(identity, hash, baseline.FileName, format);
                    }
                    else EnsureSynchronousAudioContext(identity, hash, source, format);
                    SetSynchronousAudioState(baseline);
                    // Repoint only this exact audio version, keeping each history entry's text and revision.
                    if (!reuseManagedBaseline)
                    foreach (var item in _undoRedoManager.UndoList.Concat(_undoRedoManager.RedoList))
                    {
                        if (item.SynchronousAudio == null && before.SynchronousAudio == null)
                            item.SynchronousAudio = baseline;
                        else if (item.SynchronousAudio is { } state && state.SessionId == original.SessionId &&
                            state.FileName == original.FileName && state.Sha256 == original.Sha256)
                            item.SynchronousAudio = state with { FileName = baseline.FileName, Sha256 = baseline.Sha256 };
                    }
                    WriteAudioCheckpoint("current.syncaudio.json", baseline);
                    if (AuditionLaunchOverride != null) AuditionLaunchOverride(files.EditPath, settings.AuditionPath);
                    else new AuditionLauncher().OpenFile(files.EditPath, settings.AuditionPath);
                    _pendingAudition = new PendingAudition(files, identity, baseline,
                        (double)files.Region!.StartSample / files.Timeline.SampleRate, row);
                    IsAuditionHandoffPending = true;
                }, token);
                return true;
            });
            var sent = _pendingAudition!.Files;
            var localStart = (double)sent.Region!.LocalStartSample / sent.Timeline.SampleRate;
            var localEnd = localStart + (double)sent.Region.RepairSamples / sent.Timeline.SampleRate;
            ShowStatus($"AU 已開啟局部片段：請修音 {localStart:F3}～{localEnd:F3} 秒（也標在檔名）。兩端緩衝不寫回；保留原格式與長度，Ctrl+S 後回 SE 完成修音。若不採用，點黃色待接回區按 Delete，或選「取消交由 AU 局部修音」。");
        }
        catch (OperationCanceledException) { ShowStatus("已取消 AU 交接，原音訊與字幕保留。"); }
        catch (Exception e) { await SynchronousAudioErrorAsync(e); }
    }

    [RelayCommand]
    private async Task SynchronousAudioReloadFromAudition()
    {
        if (_synchronousAudioBusy || Window == null) return;
        if (_pendingAudition == null) { ShowStatus("目前沒有待接回的修音；請先按「交給 AU 修音」。"); return; }
        try
        {
            var pending = _pendingAudition;
            await WithSynchronousAudioProgressAsync("正在確認 AU 儲存完成並接回修音…", async token =>
            {
                await ApplyAuditionReturnAsync(token);
                return true;
            });
            ResumeAfterAudition(pending);
            ShowStatus("已只替換選取範圍，從修音處前 2 秒繼續；其餘音訊及字幕未變。Ctrl+Z 可復原局部修音。");
        }
        catch (OperationCanceledException) { ShowStatus("已取消接回；AU 修音仍待接回，可稍後重試。"); }
        catch (Exception e) { await SynchronousAudioErrorAsync(e); }
    }

    internal async Task ApplyAuditionReturnAsync(CancellationToken token)
    {
        var pending = _pendingAudition ?? throw new InvalidOperationException("目前沒有待接回的 AU 修音。");
        EnsureAuditionContext(pending);
        var identity = _subtitle;
        var hash = GetUndoRedoHash();
        var format = SelectedSubtitleFormat;
        SynchronousAudioState? retiredAudio = null;
        string? committedOutput = null;
        await RunSynchronousAudioTransactionAsync(async (before, rollbackMedia, ct) =>
        {
            var settings = GetAuditionSettingsManager().Load();
            var baselineValidation = await GetValidatedAudioSourceAsync(pending.Files.BeforePath,
                pending.Files.BeforeHash, ct);
            AuditionReturnResult returned;
            using (AudioPerformanceLog.Measure("au.capture-normalize-merge-verify"))
                returned = await Task.Run(() => new AuditionHandoffService(settings, AppContext.BaseDirectory)
                    .CaptureVerifiedReturnAsync(pending.Files, baselineValidation, ct), ct);
            using (returned)
            {
            var output = returned.Path;
            var outputHash = returned.Sha256;
            var outputValidation = CacheValidatedAudioSource(returned.DetachValidation());
            ct.ThrowIfCancellationRequested();
            EnsureAuditionContext(pending);
            EnsureSynchronousAudioContext(identity, hash, pending.Baseline.FileName, format);
            var baseline = before.SynchronousAudio!;
            var spans = ReadTimelineOrSingleSpan(baseline, pending.Files.Timeline.SampleCount);
            baseline = EnsureCompactBaseline(baseline, spans);
            var revisionId = Guid.NewGuid().ToString("N");
            var region = pending.Files.Region ?? throw new InvalidDataException("缺少 AU 局部修音範圍。");
            var patch = await AudioDeltaStore.CapturePatchAsync(output, outputHash,
                _synchronousAudioDirectory!, revisionId, region.StartSample,
                checked(region.StartSample + region.RepairSamples),
                spans, ct, outputValidation);
            var patches = ReadAudioRepairPatches(baseline).Append(patch);
            var originalRegion = ToOriginalAudioRegion(baseline, pending.Files.Region!.StartSample,
                checked(pending.Files.Region.StartSample + pending.Files.Region.RepairSamples));
            var repairRegion = new AudioEditRegion(revisionId, AudioEditRegionKind.AuditionRepair,
                originalRegion.Start, originalRegion.End);
            var target = baseline with { FileName = output, Sha256 = outputHash,
                RevisionId = revisionId, PositionSeconds = GetSynchronousAudioPreviewPosition(pending.Position),
                AudioEditRegionsJson = AddAudioEditRegion(baseline, repairRegion),
                AudioRepairPatchesJson = AudioDeltaStore.WritePatches(patches) };
            WriteAudioCheckpoint("before.syncaudio.json", baseline, before);
            await LoadSynchronousAudioAsync(target, ct);
            ct.ThrowIfCancellationRequested();
            EnsureSynchronousAudioContext(identity, hash, target.FileName, format);
            SetSynchronousAudioState(target);
            _updateAudioVisualizer = true;
            ClearSynchronousAudioSelection();
            if (Subtitles.Count > 0) SelectAndScrollToRow(Math.Clamp(pending.Row, 0, Subtitles.Count - 1));
            WriteAudioCheckpoint("current.syncaudio.json", target);
            if (_undoRedoManager.UndoList.LastOrDefault()?.Hash != before.Hash) _undoRedoManager.Do(before);
            _undoRedoManager.Do(MakeUndoRedoObject("接回 AU 局部修音"));
            retiredAudio = baseline;
            committedOutput = output;
            }
        }, token);
        TryRetireCompactWorkingAudio(retiredAudio);
        if (committedOutput != null) DeleteAuditionIntermediates(committedOutput);
        _pendingAudition = null;
        IsAuditionHandoffPending = false;
    }

    private void EnsureAuditionContext(PendingAudition pending)
    {
        if (!ReferenceEquals(_pendingAudition, pending) || !ReferenceEquals(_subtitle, pending.Document) ||
            _synchronousAudioState?.SessionId != pending.Baseline.SessionId ||
            _synchronousAudioState?.RevisionId != pending.Baseline.RevisionId ||
            !string.Equals(_videoFileName, pending.Baseline.FileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SE 工作階段已切換，未套用舊修音。AU 副本仍保留：" + pending.Files.EditPath);
    }

    [RelayCommand]
    private void SynchronousAudioCancelAudition()
    {
        if (_synchronousAudioBusy || _pendingAudition == null) return;
        var pending = _pendingAudition;
        MarkAuditionRecoveryDismissed(pending.Files);
        DisposeAuditionWorkflow();
        ResumeAfterAudition(pending);
        ShowStatus("已取消交由 AU 局部修音，繼續原音訊；黃色待接回標記已移除。未刪除 AU 修音副本：" + pending.Files.EditPath);
    }

    private void ResumeAfterAudition(PendingAudition pending)
    {
        var player = GetVideoPlayerControl();
        if (player == null) return;
        if (Subtitles.Count > 0) SelectAndScrollToRow(Math.Clamp(pending.Row, 0, Subtitles.Count - 1));
        // SeekTo bypasses a stale slider maximum/equal styled-property value.
        player.SeekTo(Math.Clamp(GetSynchronousAudioPreviewPosition(pending.Position), 0, Math.Max(0, player.VideoPlayer.Duration)));
        Play();
    }

    private bool BlockWhileAuditionPending()
    {
        if (_pendingAudition == null) return false;
        ShowStatus("AU 修音待接回。請先按「完成修音，繼續聽校」或「取消交由 AU 局部修音」，也可點黃色區後按 Delete。");
        return true;
    }

    private void OnAuditionMediaOpening(string fileName)
    {
        if (_synchronousAudioBusy || _pendingAudition == null ||
            string.Equals(fileName, _pendingAudition.Baseline.FileName, StringComparison.OrdinalIgnoreCase)) return;
        var edit = _pendingAudition.Files.EditPath;
        DisposeAuditionWorkflow();
        ShowStatus("已切換媒體，舊 AU 修音未套用；副本仍保留：" + edit);
    }

    private void DisposeAuditionWorkflow()
    {
        ReleaseAudioRevisionSource();
        _pendingAudition = null;
        IsAuditionHandoffPending = false;
    }
}
