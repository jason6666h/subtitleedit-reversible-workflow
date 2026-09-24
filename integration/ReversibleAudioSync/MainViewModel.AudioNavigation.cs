using AudioWorkflow;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private string? _lastPreviewedAudioChangeId;

    [RelayCommand]
    private Task SynchronousAudioPreviousChange() => PreviewAudioChangeAsync(next: false);

    [RelayCommand]
    private Task SynchronousAudioNextChange() => PreviewAudioChangeAsync(next: true);

    private async Task PreviewAudioChangeAsync(bool next)
    {
        if (_synchronousAudioBusy || _synchronousAudioState == null)
        {
            ShowStatus("目前沒有可巡聽的音訊編修專案。");
            return;
        }
        if (_pendingAudition != null)
        {
            ShowStatus("請先完成或取消 AU 修音，再巡聽既有變更點。");
            return;
        }

        var regions = DisplayAudioEditRegions
            .Where(p => p.Kind is AudioEditRegionKind.Deleted or AudioEditRegionKind.AuditionRepair)
            .OrderBy(p => p.StartSeconds)
            .ThenBy(p => p.EndSeconds)
            .ToArray();
        if (regions.Length == 0)
        {
            ShowStatus("目前沒有已刪除或 AU 已修音的變更點。");
            return;
        }

        var currentIndex = Array.FindIndex(regions, p => p.Id == _lastPreviewedAudioChangeId);
        int index;
        if (currentIndex >= 0)
            index = next ? (currentIndex + 1) % regions.Length : (currentIndex - 1 + regions.Length) % regions.Length;
        else
        {
            var position = GetVideoPlayerControl()?.VideoPlayer.Position ?? 0;
            var originalPosition = ComparisonPosition(position);
            if (next)
            {
                index = Array.FindIndex(regions, p => p.StartSeconds > originalPosition + 0.05);
                if (index < 0) index = 0;
            }
            else
            {
                index = Array.FindLastIndex(regions, p => p.StartSeconds < originalPosition - 0.05);
                if (index < 0) index = regions.Length - 1;
            }
        }

        var region = regions[index];
        _lastPreviewedAudioChangeId = region.Id;
        SelectAudioEditRegion(region);
        var label = region.Kind == AudioEditRegionKind.Deleted ? "刪除" : "AU 修音";
        await PlayOriginalAxisAsync(false, Math.Max(0, region.StartSeconds - 2));
        ShowStatus($"巡聽 {index + 1}/{regions.Length}：{label} {region.StartSeconds:F3}～{region.EndSeconds:F3} 秒；已從前 2 秒開始播放。");
    }
}
