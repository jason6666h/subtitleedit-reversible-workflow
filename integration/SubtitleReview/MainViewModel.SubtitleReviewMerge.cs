using Nikse.SubtitleEdit.Logic;
using SubtitleReview.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Nikse.SubtitleEdit.Features.Main;

internal sealed record SubtitleReviewMergePreview(
    int CurrentNumber,
    int NextNumber,
    TimeSpan Start,
    TimeSpan End);

public partial class MainViewModel
{
    private void EnsureSubtitleReviewSnapshotCurrent(
        ReviewDocumentSnapshot original,
        ReviewSelection selection)
    {
        var keepDecisions = selection.Rows
            .Select(row => new ReviewDecision(row.ReviewId, ReviewDecisionKind.Keep))
            .ToArray();
        var validation = ReviewSafety.Validate(
            CaptureSubtitleReviewDocument(),
            original,
            new ReviewDecisions(selection, keepDecisions));
        if (!validation.IsValid)
            throw new InvalidOperationException(validation.BlockingReason);
    }

    internal SubtitleReviewMergePreview GetSubtitleReviewMergePreview(
        ReviewDocumentSnapshot original,
        ReviewSelection selection,
        int reviewId)
    {
        EnsureSubtitleReviewSnapshotCurrent(original, selection);
        var selected = selection.Rows.FirstOrDefault(row => row.ReviewId == reviewId)
            ?? throw new InvalidOperationException("找不到目前校閱列。");
        var index = Subtitles.ToList().FindIndex(row => row.Id == selected.RowId);
        if (index < 0 || index + 1 >= Subtitles.Count)
            throw new InvalidOperationException("目前字幕沒有可合併的下一行。");

        var current = Subtitles[index];
        var next = Subtitles[index + 1];
        if (current.IsReferenceOnly || next.IsReferenceOnly ||
            string.IsNullOrWhiteSpace(next.Text))
            throw new InvalidOperationException("下一行不是可合併的工作字幕。");

        var end = current.EndTime > next.EndTime ? current.EndTime : next.EndTime;
        var keepEndTime = MergeManager.ShouldKeepEndTime(SelectedSubtitleFormat);
        if (!keepEndTime && index + 2 < Subtitles.Count)
        {
            var following = Subtitles[index + 2];
            if (!following.IsReferenceOnly &&
                end > following.StartTime &&
                current.StartTime < following.StartTime)
                end = following.StartTime - TimeSpan.FromMilliseconds(1);
        }

        return new SubtitleReviewMergePreview(
            current.Number,
            next.Number,
            current.StartTime,
            end);
    }

    internal bool MergeSubtitleReviewWithNext(
        ReviewDocumentSnapshot original,
        ReviewSelection selection,
        int reviewId)
    {
        _ = GetSubtitleReviewMergePreview(original, selection, reviewId);
        var selected = selection.Rows.First(row => row.ReviewId == reviewId);
        var index = Subtitles.ToList().FindIndex(row => row.Id == selected.RowId);
        var current = Subtitles[index];
        var next = Subtitles[index + 1];

        var checkpoint = _undoRedoManager.CaptureCheckpoint();
        var before = MakeUndoRedoObject("AI 字幕校閱合併前");
        try
        {
            EnsureSubtitleReviewSnapshotCurrent(original, selection);
            if (_undoRedoManager.LatestUndoHash != before.Hash)
                _undoRedoManager.Do(before);

            _mergeManager.MergeSelectedLines(
                Subtitles,
                new List<SubtitleLineViewModel> { current, next },
                MergeManager.BreakMode.Normal,
                MergeManager.ShouldKeepEndTime(SelectedSubtitleFormat));
            Renumber();

            SelectAndScrollToRow(current);
            _updateAudioVisualizer = true;
            _undoRedoManager.Do(MakeUndoRedoObject("AI 字幕校閱：合併下一行"));
            return true;
        }
        catch
        {
            ReplaceSubtitles(before.Subtitles.Select(row => new SubtitleLineViewModel(row)));
            _undoRedoManager.RestoreCheckpoint(checkpoint);
            throw;
        }
    }
}
