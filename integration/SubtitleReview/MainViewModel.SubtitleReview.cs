using CommunityToolkit.Mvvm.Input;
using Nikse.SubtitleEdit.Features.Main.Layout;
using System;
using System.Collections.Generic;
using System.Linq;
using SubtitleReview.Core;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    [RelayCommand]
    private void SubtitleReviewSelected() => OpenSubtitleReview(selectedOnly: true);

    [RelayCommand]
    private void SubtitleReviewAll() => OpenSubtitleReview(selectedOnly: false);

    [RelayCommand]
    private void SubtitleReviewGlossary() => OpenSubtitleReview(selectedOnly: false, initialTab: 3);

    [RelayCommand]
    private void SubtitleReviewSettings() => OpenSubtitleReview(selectedOnly: false);

    private void OpenSubtitleReview(bool selectedOnly, int initialTab = 0)
    {
        var (snapshot, selection) = CreateSubtitleReviewContext(selectedOnly);
        if (selectedOnly && selection.Rows.Count == 0)
        {
            ShowStatus("請先選取要校閱的字幕。");
            return;
        }
        var window = new SubtitleReviewWindow(this, snapshot, selection, CreateSubtitleReviewLinePlayer(), initialTab);
        if (Window is Avalonia.Controls.Window owner)
            _ = window.ShowDialog(owner);
        else
            window.Show();
    }

    internal (ReviewDocumentSnapshot Snapshot, ReviewSelection Selection) CreateSubtitleReviewContext(bool selectedOnly)
    {
        var snapshot = CaptureSubtitleReviewDocument();
        var ids = selectedOnly
            ? SubtitleGridSelectedItems.Select(row => row.Id)
            : Subtitles.Where(row => !row.IsReferenceOnly).Select(row => row.Id);
        return (snapshot, ReviewSelection.Create(snapshot, ids));
    }

    internal Action<Guid>? CreateSubtitleReviewLinePlayer()
    {
        var play = MakeLinePlayer();
        if (play is null)
            return null;

        return rowId =>
        {
            var row = Subtitles.FirstOrDefault(item => item.Id == rowId);
            if (row is null)
                return;

            SelectAndScrollToRows([row]);
            play(row);
        };
    }

    internal void StopSubtitleReviewPlayback() => StopReviewLinePlayback();

    internal ReviewDocumentSnapshot CaptureSubtitleReviewDocument()
    {
        var rows = Subtitles.Select((row, index) => new ReviewRowSnapshot
        {
            Id = row.Id,
            Position = index + 1,
            Number = row.Number,
            Text = row.Text ?? string.Empty,
            OriginalText = row.OriginalText,
            StartTicks = row.StartTime.Ticks,
            EndTicks = row.EndTime.Ticks,
            Style = row.Style,
            Actor = row.Actor,
            Bookmark = row.Bookmark,
            Forced = row.Forced,
            IsReferenceOnly = row.IsReferenceOnly,
            ReferenceParagraphId = row.ReferenceParagraphId,
            Layer = row.Layer,
            Language = row.Language,
            Region = row.Region,
            Extra = row.Extra,
            Effect = row.Effect,
            IsComment = row.IsComment,
            MarginL = row.MarginL,
            MarginR = row.MarginR,
            MarginV = row.MarginV,
            NewSection = row.NewSection,
            Paragraph = row.Paragraph is { } paragraph ? new ReviewParagraphSnapshot
            {
                Id = paragraph.Id,
                Number = paragraph.Number,
                Text = paragraph.Text,
                StartTicks = TimeSpan.FromMilliseconds(paragraph.StartTime.TotalMilliseconds).Ticks,
                EndTicks = TimeSpan.FromMilliseconds(paragraph.EndTime.TotalMilliseconds).Ticks,
                Forced = paragraph.Forced,
                Extra = paragraph.Extra,
                IsComment = paragraph.IsComment,
                Actor = paragraph.Actor,
                Region = paragraph.Region,
                MarginL = paragraph.MarginL,
                MarginR = paragraph.MarginR,
                MarginV = paragraph.MarginV,
                Effect = paragraph.Effect,
                Layer = paragraph.Layer,
                Language = paragraph.Language,
                Style = paragraph.Style,
                NewSection = paragraph.NewSection,
                Bookmark = paragraph.Bookmark,
            } : null,
        }).ToArray();
        return new ReviewDocumentSnapshot(_subtitleFileName, SelectedSubtitleFormat?.Name ?? string.Empty,
            rows, GetUndoRedoHash().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal int ApplySubtitleReview(
        ReviewDocumentSnapshot original,
        ReviewSelection selection,
        IReadOnlyList<ReviewDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(decisions);

        var reviewDecisions = new ReviewDecisions(selection, decisions);
        var validation = ReviewSafety.Validate(CaptureSubtitleReviewDocument(), original, reviewDecisions);
        if (!validation.IsValid)
            throw new InvalidOperationException(validation.BlockingReason);

        if (validation.Changes.Count == 0)
            return 0;

        var changesById = validation.Changes.ToDictionary(change => change.RowId, change => change.Text);
        var selectedIndices = SubtitleGridSelectedItems
            .Select(Subtitles.IndexOf)
            .Where(index => index >= 0)
            .ToArray();
        var historyCheckpoint = _undoRedoManager.CaptureCheckpoint();
        var before = MakeUndoRedoObject("AI 字幕校閱前");

        try
        {
            var revalidation = ReviewSafety.Validate(CaptureSubtitleReviewDocument(), original, reviewDecisions);
            if (!revalidation.IsValid)
                throw new InvalidOperationException(revalidation.BlockingReason);

            if (_undoRedoManager.LatestUndoHash != before.Hash)
                _undoRedoManager.Do(before);

            foreach (var row in Subtitles)
                if (changesById.TryGetValue(row.Id, out var text))
                    row.Text = text;

            _updateAudioVisualizer = true;
            RefreshSubtitlePreview();
            _undoRedoManager.Do(MakeUndoRedoObject($"AI 字幕校閱（{validation.Changes.Count} 列）"));

            var restoredSelection = selectedIndices
                .Where(index => index < Subtitles.Count)
                .Select(index => Subtitles[index])
                .ToArray();
            if (restoredSelection.Length > 0)
                SelectAndScrollToRows(restoredSelection);

            TrySaveAudioCheckpoint();
            ShowStatus($"AI 字幕校閱已套用到 SE 字幕：{validation.Changes.Count} 列；可用 Ctrl+Z 一次還原。", 5000);
            return validation.Changes.Count;
        }
        catch
        {
            ReplaceSubtitles(before.Subtitles.Select(row => new SubtitleLineViewModel(row)));
            _undoRedoManager.RestoreCheckpoint(historyCheckpoint);
            throw;
        }
    }
}
