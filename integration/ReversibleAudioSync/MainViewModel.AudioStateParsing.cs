using AudioWorkflow;
using Nikse.SubtitleEdit.Logic.UndoRedo;
using System.IO;
using System.Text.Json;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private static SourceSpan[] ParseTimelineJson(string json, string error)
    {
        var timeline = JsonSerializer.Deserialize<SourceSpan[]>(json)
            ?? throw new InvalidDataException(error);
        TimelineMap.Validate(timeline);
        return timeline;
    }

    private static SourceSpan[] ReadTimeline(SynchronousAudioState state)
    {
        if (state.TimelineMapJson == null)
        {
            if (state.TimelineOriginalFrames <= 0)
                throw new InvalidDataException("缺少原時間軸長度。");
            return [new SourceSpan(0, state.TimelineOriginalFrames)];
        }
        return ParseTimelineJson(state.TimelineMapJson, "缺少原時間軸資料。");
    }

    private static SourceSpan[] ReadTimelineOrSingleSpan(SynchronousAudioState state, long fallbackFrames)
    {
        if (state.TimelineMapJson != null)
            return ParseTimelineJson(state.TimelineMapJson, "缺少目前音訊時間軸。");
        if (fallbackFrames <= 0)
            throw new InvalidDataException("缺少可建立時間軸的音訊長度。");
        return [new SourceSpan(0, fallbackFrames)];
    }

    private static SourceSpan[] ReadRenderBaselineTimeline(SynchronousAudioState state)
    {
        if (string.IsNullOrWhiteSpace(state.RenderBaselineTimelineMapJson))
            throw new InvalidDataException("缺少不可變基準時間軸。");
        return ParseTimelineJson(state.RenderBaselineTimelineMapJson, "缺少不可變基準時間軸。");
    }

    internal static AudioEditRegion[] ReadAudioEditRegions(SynchronousAudioState? state)
    {
        if (string.IsNullOrWhiteSpace(state?.AudioEditRegionsJson)) return [];
        var regions = JsonSerializer.Deserialize<AudioEditRegion[]>(state.AudioEditRegionsJson) ?? [];
        foreach (var region in regions) region.Validate();
        return regions;
    }

    private static AudioRepairPatch[] ReadAudioRepairPatches(SynchronousAudioState state) =>
        AudioDeltaStore.ReadPatches(state.AudioRepairPatchesJson);
}
