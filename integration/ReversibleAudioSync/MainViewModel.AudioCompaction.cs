using AudioWorkflow;
using Nikse.SubtitleEdit.Logic.Config;
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
    private static bool HasCompactAudio(SynchronousAudioState state) =>
        !string.IsNullOrWhiteSpace(state.RenderBaselineFileName) &&
        !string.IsNullOrWhiteSpace(state.RenderBaselineSha256) &&
        !string.IsNullOrWhiteSpace(state.RenderBaselineTimelineMapJson);

    private async Task<SynchronousAudioState> EnsureCompactBaselineAsync(SynchronousAudioState state,
        AudioCutResult cut, IReadOnlyList<SourceSpan> timeline, string directory, CancellationToken token)
    {
        if (HasCompactAudio(state)) return state;
        var baseline = cut.BaselinePath;
        if (!IsManagedAudioPath(baseline, directory))
        {
            Directory.CreateDirectory(directory);
            var owned = Path.Combine(directory, "baseline-" + Guid.NewGuid().ToString("N") + ".wav");
            var validated = await AudioRevisionSource.CopyValidatedAsync(
                baseline, owned, cut.BaselineSha256, token);
            CacheValidatedAudioSource(validated);
            baseline = owned;
        }
        else
        {
            CacheValidatedAudioSource(await AudioRevisionSource.OpenAsync(
                baseline, cut.BaselineSha256, token));
        }
        return state with
        {
            FileName = baseline,
            Sha256 = cut.BaselineSha256,
            RenderBaselineFileName = baseline,
            RenderBaselineSha256 = cut.BaselineSha256,
            RenderBaselineTimelineMapJson = JsonSerializer.Serialize(timeline),
            AudioRepairPatchesJson = AudioDeltaStore.WritePatches([])
        };
    }

    private static SynchronousAudioState EnsureCompactBaseline(SynchronousAudioState state,
        IReadOnlyList<SourceSpan> timeline)
    {
        if (HasCompactAudio(state)) return state;
        if (!Path.GetExtension(state.FileName).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("節省空間模式需要 PCM WAV 基準。請重新交給 AU 後再接回。");
        return state with
        {
            RenderBaselineFileName = state.FileName,
            RenderBaselineSha256 = state.Sha256,
            RenderBaselineTimelineMapJson = JsonSerializer.Serialize(timeline),
            AudioRepairPatchesJson = AudioDeltaStore.WritePatches([])
        };
    }

    private async Task EnsureCompactAudioAvailableAsync(SynchronousAudioState state, string directory,
        CancellationToken token)
    {
        if (File.Exists(state.FileName) || !HasCompactAudio(state)) return;
        var baselineTimeline = ReadRenderBaselineTimeline(state);
        var targetTimeline = state.TimelineMapJson == null
            ? baselineTimeline
            : ReadTimeline(state);
        var hash = await AudioDeltaStore.RebuildAsync(state.RenderBaselineFileName!, state.RenderBaselineSha256!,
            baselineTimeline, targetTimeline, ReadAudioRepairPatches(state),
            directory, state.FileName, token);
        if (!hash.Equals(state.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(state.FileName);
            throw new InvalidDataException("由剪輯指令重建的音訊雜湊不一致，已停止切換版本。");
        }
    }

    private void TryRetireCompactWorkingAudio(SynchronousAudioState? state)
    {
        if (state == null || !HasCompactAudio(state) ||
            string.Equals(state.FileName, state.RenderBaselineFileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state.FileName, _videoFileName, StringComparison.OrdinalIgnoreCase) ||
            _synchronousAudioDirectory == null || !IsManagedAudioPath(state.FileName, _synchronousAudioDirectory)) return;
        try
        {
            ReleaseCachedAudioSource(state.FileName);
            if (_revisionAudioSource?.Matches(state.FileName, state.Sha256) == true) ReleaseAudioRevisionSource();
            if (File.Exists(state.FileName)) File.Delete(state.FileName);
        }
        catch (Exception e) { Se.LogError(e, "Compact audio retirement"); }
    }

    private static void DeleteAuditionIntermediates(string output)
    {
        var directory = Path.GetDirectoryName(output);
        if (directory == null) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.wav"))
        {
            if (Path.GetFullPath(file).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(file);
            if (!name.StartsWith("repaired-", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("normalized-", StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); }
            catch { /* another intermediate may still be removable */ }
        }
    }
}
