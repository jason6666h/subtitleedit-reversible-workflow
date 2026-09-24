using AudioWorkflow;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Nikse.SubtitleEdit.Features.Shared;
using Nikse.SubtitleEdit.Logic.UndoRedo;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task SynchronousAudioVerify()
    {
        if (BlockOriginalTimelineEdit() || _synchronousAudioBusy || Window == null) return;
        var state = _synchronousAudioState;
        var directory = _synchronousAudioDirectory;
        if (state == null || directory == null)
        {
            ShowStatus("尚未進入音檔編修中，沒有可驗證的音訊專案。");
            return;
        }

        try
        {
            var pending = _pendingAudition == null ? 0 : 1;
            var result = await WithSynchronousAudioProgressAsync("正在逐樣本驗證目前音檔…",
                token => VerifyCurrentSynchronousAudioAsync(state, directory, token));
            if (_synchronousAudioState?.SessionId != state.SessionId ||
                _synchronousAudioState.RevisionId != state.RevisionId ||
                !string.Equals(_synchronousAudioState.Sha256, state.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("驗證期間音訊版本已改變，請重新驗證。");

            var regions = DisplayAudioEditRegions;
            var deleted = regions.Count(p => p.Kind == AudioEditRegionKind.Deleted);
            var repaired = regions.Count(p => p.Kind == AudioEditRegionKind.AuditionRepair);
            var details = result == null
                ? "目前尚無 PCM 剪修操作；原始音檔 SHA-256 身份驗證通過。"
                : $"目前音檔與操作記錄逐樣本一致。\n" +
                  $"未標示差異：0 樣本\n" +
                  $"橘色已刪除區：{deleted} 段（淨刪除 {result.DeletedFrames:N0} 樣本）\n" +
                  $"紫色 AU 已修音區：{repaired} 段（實際覆蓋 {result.RepairedFrames:N0} 樣本）\n" +
                  $"格式：{result.Wave.SampleRate:N0} Hz／{result.Wave.Channels} 聲道／{result.Wave.BitsPerSample} bit\n" +
                  $"目前長度：{result.Wave.SampleCount:N0} 樣本";
            var pendingText = pending == 0
                ? "黃色 AU 待接回區：0 段"
                : "黃色 AU 待接回區：1 段（尚未套用，不在目前音檔內）";
            var message = $"數位驗證通過。\n\n{details}\n{pendingText}\n\n" +
                $"SHA-256：{state.Sha256}\n\n" +
                "這證明檔案資料與編修操作一致；內容是否符合你的聽感，仍請試聽剪接點與修音區。";
            ShowStatus(pending == 0
                ? "音檔驗證通過：操作重播與目前音檔逐樣本一致。"
                : "目前音檔驗證通過；另有 AU 修音尚待接回。" );
            await MessageBox.Show(Window, "驗證目前音檔", message,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { ShowStatus("已取消音檔驗證。"); }
        catch (Exception e) { await SynchronousAudioErrorAsync(e); }
    }

    private async Task<AudioProjectVerificationResult?> VerifyCurrentSynchronousAudioAsync(
        SynchronousAudioState state, string directory, CancellationToken token)
    {
        await EnsureCompactAudioAvailableAsync(state, directory, token);
        var current = await GetValidatedAudioSourceAsync(state.FileName, state.Sha256, token);
        if (!Path.GetExtension(state.FileName).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            if (HasCompactAudio(state) || !string.IsNullOrWhiteSpace(state.AudioRepairPatchesJson))
                throw new InvalidDataException("含剪修操作的專案必須以 PCM WAV 驗證。");
            return null;
        }

        var wave = WaveInfo.Read(state.FileName);
        var targetTimeline = ReadTimelineOrSingleSpan(state, wave.SampleCount);
        var baselineTimeline = HasCompactAudio(state)
            ? ReadRenderBaselineTimeline(state)
            : targetTimeline;
        var baselinePath = HasCompactAudio(state) ? state.RenderBaselineFileName! : state.FileName;
        var baselineHash = HasCompactAudio(state) ? state.RenderBaselineSha256! : state.Sha256;
        var patches = ReadAudioRepairPatches(state);
        ValidateDisplayedOperations(state, targetTimeline, patches);
        return await AudioDeltaStore.VerifyAsync(baselinePath, baselineHash, baselineTimeline,
            targetTimeline, patches, directory, state.FileName, state.Sha256, token, current);
    }

    private void ValidateDisplayedOperations(SynchronousAudioState state, SourceSpan[] timeline,
        AudioRepairPatch[] patches)
    {
        var regions = DisplayAudioEditRegions.Where(p => p.Kind != AudioEditRegionKind.AuditionPending).ToArray();
        var repairIds = regions.Where(p => p.Kind == AudioEditRegionKind.AuditionRepair)
            .Select(p => p.Id).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var patchIds = patches.Select(p => p.Id).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        if (!repairIds.SequenceEqual(patchIds, StringComparer.Ordinal))
            throw new InvalidDataException("紫色 AU 修音標記與實際修音差分不一致，拒絕通過驗證。");

        if (state.TimelineSampleRate <= 0 || state.TimelineOriginalFrames <= 0) return;
        var expected = new System.Collections.Generic.List<SourceSpan>();
        long previous = 0;
        foreach (var span in timeline)
        {
            if (span.Start > previous) expected.Add(new SourceSpan(previous, span.Start));
            previous = span.End;
        }
        if (previous < state.TimelineOriginalFrames)
            expected.Add(new SourceSpan(previous, state.TimelineOriginalFrames));
        var marked = MergeRanges(regions.Where(p => p.Kind == AudioEditRegionKind.Deleted)
            .Select(p => new SourceSpan(
                Math.Clamp((long)Math.Round(p.StartSeconds * state.TimelineSampleRate), 0, state.TimelineOriginalFrames),
                Math.Clamp((long)Math.Round(p.EndSeconds * state.TimelineSampleRate), 0, state.TimelineOriginalFrames)))
            .Where(p => p.End > p.Start));
        if (!expected.SequenceEqual(marked))
            throw new InvalidDataException("橘色已刪除標記與實際刪除時間軸不一致，拒絕通過驗證。");
    }

    private static SourceSpan[] MergeRanges(System.Collections.Generic.IEnumerable<SourceSpan> source)
    {
        var ordered = source.OrderBy(p => p.Start).ThenBy(p => p.End).ToArray();
        var result = new System.Collections.Generic.List<SourceSpan>();
        foreach (var span in ordered)
        {
            if (result.Count == 0 || span.Start > result[^1].End) result.Add(span);
            else result[^1] = result[^1] with { End = Math.Max(result[^1].End, span.End) };
        }
        return result.ToArray();
    }
}
