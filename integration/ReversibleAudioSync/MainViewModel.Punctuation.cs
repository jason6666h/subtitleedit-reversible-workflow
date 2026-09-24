using AudioWorkflow;
using CommunityToolkit.Mvvm.Input;
using Nikse.SubtitleEdit.Features.Shared;
using Nikse.SubtitleEdit.Logic.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private sealed class SubtitleRowReferenceComparer : IEqualityComparer<SubtitleLineViewModel>
    {
        public static readonly SubtitleRowReferenceComparer Instance = new();
        public bool Equals(SubtitleLineViewModel? left, SubtitleLineViewModel? right) => ReferenceEquals(left, right);
        public int GetHashCode(SubtitleLineViewModel row) => RuntimeHelpers.GetHashCode(row);
    }

    private sealed record SubtitlePunctuationChange(
        SubtitleLineViewModel Row,
        string Original,
        string Normalized);

    [RelayCommand]
    private Task NormalizeSelectedSubtitlePunctuation() => NormalizeSubtitlePunctuationAsync(selectedOnly: true);

    [RelayCommand]
    private Task NormalizeAllSubtitlePunctuation() => NormalizeSubtitlePunctuationAsync(selectedOnly: false);

    private async Task NormalizeSubtitlePunctuationAsync(bool selectedOnly)
    {
        if (Window == null || IsAudioTimelineBusy || BlockOriginalTimelineEdit())
            return;

        var targets = selectedOnly
            ? SubtitleGridSelectedItems
            : Subtitles.Where(row => !row.IsReferenceOnly).ToList();
        if (targets.Count == 0)
        {
            ShowStatus(selectedOnly ? "請先選取要整理的字幕。" : "目前沒有可整理的字幕。");
            return;
        }

        var changes = CreatePunctuationChanges(targets);
        if (changes.Count == 0)
        {
            ShowStatus($"已檢查 {targets.Count} 列，標點不需變更。");
            return;
        }

        var expectedHash = GetUndoRedoHash();
        var preview = string.Join(Environment.NewLine + Environment.NewLine, changes.Take(3).Select((change, index) =>
            $"{index + 1}. 原：{CompactPunctuationPreview(change.Original)}{Environment.NewLine}" +
            $"   後：{CompactPunctuationPreview(change.Normalized)}"));
        var scope = selectedOnly ? "選取字幕" : "全部字幕";
        var result = await MessageBox.Show(Window, "整理字幕標點",
            $"範圍：{scope}（共 {targets.Count} 列）{Environment.NewLine}" +
            $"將變更：{changes.Count} 列{Environment.NewLine + Environment.NewLine}" +
            $"只會修改字幕文字，不會改變時間、順序、樣式、角色、書籤或音訊。" +
            $"可用一次復原還原。{Environment.NewLine + Environment.NewLine}{preview}" +
            (changes.Count > 3 ? $"{Environment.NewLine + Environment.NewLine}另有 {changes.Count - 3} 列。" : string.Empty),
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != MessageBoxResult.Yes)
            return;

        if (GetUndoRedoHash() != expectedHash || changes.Any(change =>
                !Subtitles.Contains(change.Row) || !string.Equals(change.Row.Text, change.Original, StringComparison.Ordinal)))
        {
            ShowStatus("字幕在確認期間已變更；為避免覆蓋新內容，請重新執行整理標點。");
            return;
        }

        try
        {
            var changed = ApplySubtitlePunctuation(changes.Select(change => change.Row).ToArray());
            ShowStatus($"已整理 {changed} 列字幕標點；可使用復原還原。");
        }
        catch (Exception exception)
        {
            Se.LogError(exception, "Normalize subtitle punctuation");
            await MessageBox.Show(Window, "整理字幕標點失敗", exception.Message,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal int ApplySubtitlePunctuation(IReadOnlyCollection<SubtitleLineViewModel> targets)
    {
        var changes = CreatePunctuationChanges(targets);
        if (changes.Count == 0)
            return 0;

        var targetRows = new HashSet<SubtitleLineViewModel>(
            changes.Select(change => change.Row), SubtitleRowReferenceComparer.Instance);
        if (targetRows.Any(row => !Subtitles.Contains(row)))
            throw new InvalidOperationException("字幕清單已變更，拒絕套用過期的標點整理結果。");

        var selectedIndices = SubtitleGridSelectedItems
            .Select(Subtitles.IndexOf)
            .Where(index => index >= 0)
            .ToArray();
        var historyCheckpoint = _undoRedoManager.CaptureCheckpoint();
        var before = MakeUndoRedoObject("整理字幕標點前");

        try
        {
            if (_undoRedoManager.LatestUndoHash != before.Hash)
                _undoRedoManager.Do(before);

            var normalizedByRow = changes.ToDictionary(
                change => change.Row, change => change.Normalized, SubtitleRowReferenceComparer.Instance);
            var updated = Subtitles.Select(row =>
            {
                var clone = new SubtitleLineViewModel(row);
                if (normalizedByRow.TryGetValue(row, out var normalized))
                    clone.Text = normalized;
                return clone;
            }).ToArray();

            EnsurePunctuationTimelineUnchanged(Subtitles, updated);
            ReplaceSubtitles(updated);
            _updateAudioVisualizer = true;
            _undoRedoManager.Do(MakeUndoRedoObject($"整理字幕標點（{changes.Count} 列）"));

            var restoredSelection = selectedIndices.Where(index => index < Subtitles.Count)
                .Select(index => Subtitles[index]).ToArray();
            if (restoredSelection.Length > 0)
                SelectAndScrollToRows(restoredSelection);

            TrySaveAudioCheckpoint();
            return changes.Count;
        }
        catch
        {
            ReplaceSubtitles(before.Subtitles.Select(row => new SubtitleLineViewModel(row)));
            _undoRedoManager.RestoreCheckpoint(historyCheckpoint);
            throw;
        }
    }

    private static List<SubtitlePunctuationChange> CreatePunctuationChanges(
        IEnumerable<SubtitleLineViewModel> targets)
    {
        var changes = new List<SubtitlePunctuationChange>();
        foreach (var row in targets.Where(row => !row.IsReferenceOnly).Distinct(SubtitleRowReferenceComparer.Instance))
        {
            var original = row.Text ?? string.Empty;
            var normalized = SubtitlePunctuationNormalizer.NormalizeText(original);
            if (string.IsNullOrWhiteSpace(normalized))
                normalized = original;
            if (!string.Equals(original, normalized, StringComparison.Ordinal))
                changes.Add(new SubtitlePunctuationChange(row, original, normalized));
        }
        return changes;
    }

    private static void EnsurePunctuationTimelineUnchanged(
        IReadOnlyList<SubtitleLineViewModel> original,
        IReadOnlyList<SubtitleLineViewModel> updated)
    {
        if (original.Count != updated.Count)
            throw new InvalidOperationException("整理標點不得新增或刪除字幕列。");
        for (var index = 0; index < original.Count; index++)
        {
            if (original[index].Id != updated[index].Id ||
                original[index].Number != updated[index].Number ||
                original[index].StartTime != updated[index].StartTime ||
                original[index].EndTime != updated[index].EndTime)
                throw new InvalidOperationException("整理標點不得改變字幕順序、編號或時間軸。");
        }
    }

    private static string CompactPunctuationPreview(string value)
    {
        var compact = value.Replace("\r\n", "↵", StringComparison.Ordinal)
            .Replace('\r', '↵').Replace('\n', '↵');
        return compact.Length <= 88 ? compact : compact[..85] + "…";
    }
}
