using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Features.Main.Layout;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.UndoRedo;
using SubtitleReview.Core;

namespace UITests.Logic;

public class SubtitleReviewIntegrationTests
{
    [AvaloniaFact]
    public void ToolbarOffersDistinctReviewActions()
    {
        using var host = new Host();
        var button = SubtitleReviewIntegrationUi.CreateToolbarButton(host.Vm);
        Assert.Equal("SubtitleReviewIntegrationToolbarButton", button.Name);
        var flyout = Assert.IsType<MenuFlyout>(button.Flyout);
        Assert.Equal(["校閱目前選取字幕", "校閱整份字幕", "詞彙表", "提示詞設定"],
            flyout.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? string.Empty).ToArray());
    }

    [AvaloniaFact]
    public void SelectedAndWholeDocumentReviewScopesAreDifferent()
    {
        using var host = new Host();
        var first = new SubtitleLineViewModel { Text = "第一列" };
        var second = new SubtitleLineViewModel { Text = "第二列" };
        var third = new SubtitleLineViewModel { Text = "第三列" };
        foreach (var row in new[] { first, second, third })
            host.Vm.Subtitles.Add(row);
        host.Vm.SubtitleGrid.SelectedItems!.Clear();
        host.Vm.SubtitleGrid.SelectedItems.Add(second);

        var selected = host.Vm.CreateSubtitleReviewContext(selectedOnly: true).Selection;
        var all = host.Vm.CreateSubtitleReviewContext(selectedOnly: false).Selection;

        Assert.Equal([second.Id], selected.Rows.Select(row => row.RowId).ToArray());
        Assert.Equal([first.Id, second.Id, third.Id], all.Rows.Select(row => row.RowId).ToArray());
    }

    [AvaloniaFact]
    public void SnapshotKeepsTheWholeDocumentAndReviewSelectionKeepsOriginalIds()
    {
        using var host = new Host();
        var vm = host.Vm;
        var first = new SubtitleLineViewModel { Number = 1, Text = "第一列", StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromSeconds(1) };
        var blank = new SubtitleLineViewModel { Number = 2, Text = "", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(2) };
        var reference = new SubtitleLineViewModel { Number = 3, Text = "參考", IsReferenceOnly = true, StartTime = TimeSpan.FromSeconds(2), EndTime = TimeSpan.FromSeconds(3) };
        var last = new SubtitleLineViewModel { Number = 4, Text = "第四列", StartTime = TimeSpan.FromSeconds(3), EndTime = TimeSpan.FromSeconds(4) };
        foreach (var row in new[] { first, blank, reference, last }) vm.Subtitles.Add(row);
        var snapshot = vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [last.Id, reference.Id, first.Id]);
        Assert.Equal(4, snapshot.Rows.Count);
        Assert.Equal([new ReviewSelectionRow(1, first.Id), new ReviewSelectionRow(2, last.Id)], selection.Rows);
        Assert.Equal([first.Id, blank.Id, reference.Id, last.Id], vm.Subtitles.Select(row => row.Id));
    }

    [AvaloniaFact]
    public void TwoAcceptedRowsUseOneNativeUndoAndPreserveMetadata()
    {
        using var host = new Host();
        var vm = host.Vm;
        var first = new SubtitleLineViewModel { Number = 1, Text = "舊一", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(2), Style = "Main", Actor = "A", Bookmark = "核對" };
        var middle = new SubtitleLineViewModel { Number = 2, Text = "保留", StartTime = TimeSpan.FromSeconds(3), EndTime = TimeSpan.FromSeconds(4) };
        var last = new SubtitleLineViewModel { Number = 3, Text = "舊三", StartTime = TimeSpan.FromSeconds(5), EndTime = TimeSpan.FromSeconds(6), Style = "Alt" };
        foreach (var row in new[] { first, middle, last }) vm.Subtitles.Add(row);
        host.History.Do(vm.MakeUndoRedoObject("before review"));
        var before = vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(before, [first.Id, middle.Id, last.Id]);
        var decisions = new[] {
            new ReviewDecision(1, ReviewDecisionKind.Apply, "新一"),
            new ReviewDecision(2, ReviewDecisionKind.Keep),
            new ReviewDecision(3, ReviewDecisionKind.Apply, "新三"),
        };
        Assert.Equal(2, vm.ApplySubtitleReview(before, selection, decisions));
        Assert.Equal(["新一", "保留", "新三"], vm.Subtitles.Select(row => row.Text));
        Assert.Equal((first.Id, first.StartTime, first.EndTime, "Main", "A", "核對"),
            (vm.Subtitles[0].Id, vm.Subtitles[0].StartTime, vm.Subtitles[0].EndTime,
                vm.Subtitles[0].Style, vm.Subtitles[0].Actor, vm.Subtitles[0].Bookmark));
        Assert.Equal(2, host.History.UndoCount);
        vm.UndoCommand.Execute(null);
        Assert.Equal(["舊一", "保留", "舊三"], vm.Subtitles.Select(row => row.Text));
        vm.RedoCommand.Execute(null);
        Assert.Equal(["新一", "保留", "新三"], vm.Subtitles.Select(row => row.Text));
    }

    [AvaloniaFact]
    public void ChangedUnrelatedMetadataRejectsTheWholeReviewBatch()
    {
        using var host = new Host();
        var vm = host.Vm;
        var first = new SubtitleLineViewModel { Number = 1, Text = "第一列" };
        var other = new SubtitleLineViewModel { Number = 2, Text = "另一列" };
        vm.Subtitles.Add(first);
        vm.Subtitles.Add(other);
        host.History.Do(vm.MakeUndoRedoObject("before review"));
        var before = vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(before, [first.Id]);
        other.Actor = "新角色";
        var undoCount = host.History.UndoCount;
        Assert.Throws<InvalidOperationException>(() => vm.ApplySubtitleReview(before, selection,
            [new ReviewDecision(1, ReviewDecisionKind.Apply, "AI 字幕")]));
        Assert.Equal("第一列", vm.Subtitles[0].Text);
        Assert.Equal("新角色", vm.Subtitles[1].Actor);
        Assert.Equal(undoCount, host.History.UndoCount);
    }

    [AvaloniaFact]
    public void MergeNextUsesNativeUndoRedoAndMakesOldSnapshotStale()
    {
        using var host = new Host();
        var vm = host.Vm;
        var first = new SubtitleLineViewModel { Number = 1, Text = "甲", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(2) };
        var next = new SubtitleLineViewModel { Number = 2, Text = "乙", StartTime = TimeSpan.FromSeconds(2.2), EndTime = TimeSpan.FromSeconds(3) };
        var last = new SubtitleLineViewModel { Number = 3, Text = "丙", StartTime = TimeSpan.FromSeconds(4), EndTime = TimeSpan.FromSeconds(5) };
        foreach (var row in new[] { first, next, last }) vm.Subtitles.Add(row);
        host.History.Do(vm.MakeUndoRedoObject("before merge"));

        var snapshot = vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [first.Id]);
        var preview = vm.GetSubtitleReviewMergePreview(snapshot, selection, 1);
        Assert.Equal((1, 2, first.StartTime, next.EndTime),
            (preview.CurrentNumber, preview.NextNumber, preview.Start, preview.End));

        Assert.True(vm.MergeSubtitleReviewWithNext(snapshot, selection, 1));
        Assert.Equal(2, vm.Subtitles.Count);
        Assert.Contains("甲", vm.Subtitles[0].Text);
        Assert.Contains("乙", vm.Subtitles[0].Text);
        Assert.Equal(2, host.History.UndoCount);
        Assert.Throws<InvalidOperationException>(() => vm.ApplySubtitleReview(
            snapshot, selection, [new ReviewDecision(1, ReviewDecisionKind.Apply, "舊快照")]));

        vm.UndoCommand.Execute(null);
        Assert.Equal(["甲", "乙", "丙"], vm.Subtitles.Select(row => row.Text));
        vm.RedoCommand.Execute(null);
        Assert.Equal(2, vm.Subtitles.Count);
    }

    [AvaloniaFact]
    public void MergeNextRejectsReferenceOnlyOrStaleDocumentWithoutPartialWrite()
    {
        using var host = new Host();
        var vm = host.Vm;
        var first = new SubtitleLineViewModel { Number = 1, Text = "第一列" };
        var reference = new SubtitleLineViewModel { Number = 2, Text = "", OriginalText = "參考", IsReferenceOnly = true };
        vm.Subtitles.Add(first);
        vm.Subtitles.Add(reference);
        host.History.Do(vm.MakeUndoRedoObject("before merge"));
        var snapshot = vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [first.Id]);
        var undoCount = host.History.UndoCount;

        Assert.Throws<InvalidOperationException>(() => vm.MergeSubtitleReviewWithNext(snapshot, selection, 1));
        Assert.Equal(2, vm.Subtitles.Count);
        Assert.Equal(undoCount, host.History.UndoCount);

        reference.IsReferenceOnly = false;
        reference.Text = "第二列";
        Assert.Throws<InvalidOperationException>(() => vm.MergeSubtitleReviewWithNext(snapshot, selection, 1));
        Assert.Equal(["第一列", "第二列"], vm.Subtitles.Select(row => row.Text));
        Assert.Equal(undoCount, host.History.UndoCount);
    }

    [AvaloniaFact]
    public void ReviewPanelBlocksConflictAndMissingAiWhileKeepingLiveSubtitleReadOnly()
    {
        using var host = new Host();
        var vm = host.Vm;
        var first = new SubtitleLineViewModel { Number = 1, Text = "舊一" };
        var conflict = new SubtitleLineViewModel { Number = 2, Text = "舊二" };
        var missing = new SubtitleLineViewModel { Number = 3, Text = "舊三" };
        foreach (var row in new[] { first, conflict, missing }) vm.Subtitles.Add(row);

        var snapshot = vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [first.Id, conflict.Id, missing.Id]);
        var window = new SubtitleReviewWindow(vm, snapshot, selection, null);
        var preview = new ReviewPreviewResult(
            [
                new ReviewPreviewRow(1, "舊一", "新一", false, [], "not_provided", ReviewEvidencePacket.Empty),
                new ReviewPreviewRow(2, "舊二", "新二", true, ["新二", "另一版本"], "not_provided", ReviewEvidencePacket.Empty),
                new ReviewPreviewRow(3, "舊三", "", false, [], "not_provided", ReviewEvidencePacket.Empty),
            ],
            [],
            1,
            [2],
            new Dictionary<int, IReadOnlyList<string>> { [2] = ["新二", "另一版本"] },
            [3],
            [99],
            false);
        typeof(SubtitleReviewWindow).GetMethod("LoadPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [preview]);

        var rowList = (TableView)GetPrivateField(window, "_rowList");
        var apply = (Button)GetPrivateField(window, "_applyAiButton");
        var manual = (Button)GetPrivateField(window, "_manualButton");
        var commit = (Button)GetPrivateField(window, "_commitButton");
        var applyAll = (Button)GetPrivateField(window, "_applyAllAiButton");
        var bulkHint = (TextBlock)GetPrivateField(window, "_bulkActionHint");
        var setDecision = typeof(SubtitleReviewWindow).GetMethod("SetDecision", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.False(applyAll.IsEnabled);
        Assert.Contains("未知編號", bulkHint.Text);

        rowList.SelectedIndex = 0;
        Assert.True(apply.IsEnabled);
        rowList.SelectedIndex = 1;
        Assert.False(apply.IsEnabled);
        Assert.True(manual.IsEnabled);
        rowList.SelectedIndex = 2;
        Assert.False(apply.IsEnabled);
        Assert.True(manual.IsEnabled);
        Assert.Throws<InvalidOperationException>(() => window.ApplyAllAiResponsesToSubtitle());
        Assert.Equal(["舊一", "舊二", "舊三"], vm.Subtitles.Select(row => row.Text));

        rowList.SelectedIndex = 0;
        setDecision.Invoke(window, [ReviewDecisionKind.Apply]);
        setDecision.Invoke(window, [ReviewDecisionKind.Keep]);
        Assert.True(commit.IsEnabled);
        Assert.Equal(["舊一", "舊二", "舊三"], vm.Subtitles.Select(row => row.Text));
    }

    [AvaloniaFact]
    public void ReviewPanelDisablesMergeForTheLastRowAndShowsWhy()
    {
        using var host = new Host();
        var first = new SubtitleLineViewModel { Number = 1, Text = "第一列" };
        var last = new SubtitleLineViewModel { Number = 2, Text = "最後一列" };
        host.Vm.Subtitles.Add(first);
        host.Vm.Subtitles.Add(last);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [first.Id, last.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);
        var preview = new ReviewPreviewResult(
            [
                new ReviewPreviewRow(1, first.Text, "新一", false, [], "not_provided", ReviewEvidencePacket.Empty),
                new ReviewPreviewRow(2, last.Text, "新二", false, [], "not_provided", ReviewEvidencePacket.Empty),
            ],
            [], 2, [], new Dictionary<int, IReadOnlyList<string>>(), [], [], true);
        typeof(SubtitleReviewWindow).GetMethod("LoadPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [preview]);

        var rows = (TableView)GetPrivateField(window, "_rowList");
        var merge = (Button)GetPrivateField(window, "_mergeButton");
        var mergeHint = (TextBlock)GetPrivateField(window, "_mergeActionHint");
        rows.SelectedIndex = 0;
        Assert.True(merge.IsEnabled);
        rows.SelectedIndex = 1;
        Assert.False(merge.IsEnabled);
        Assert.Contains("下一行", mergeHint.Text);
    }

    [AvaloniaFact]
    public void ReviewPanelDisablesMergeWhenNextRowIsReferenceOrBlank()
    {
        using var host = new Host();
        var first = new SubtitleLineViewModel { Number = 1, Text = "第一列" };
        var reference = new SubtitleLineViewModel { Number = 2, Text = "參考", IsReferenceOnly = true };
        var second = new SubtitleLineViewModel { Number = 3, Text = "第三列" };
        var blank = new SubtitleLineViewModel { Number = 4, Text = "" };
        foreach (var row in new[] { first, reference, second, blank })
            host.Vm.Subtitles.Add(row);

        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [first.Id, second.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);
        var preview = new ReviewPreviewResult(
            [
                new ReviewPreviewRow(1, first.Text, "新一", false, [], "not_provided", ReviewEvidencePacket.Empty),
                new ReviewPreviewRow(2, second.Text, "新三", false, [], "not_provided", ReviewEvidencePacket.Empty),
            ],
            [], 2, [], new Dictionary<int, IReadOnlyList<string>>(), [], [], true);
        typeof(SubtitleReviewWindow).GetMethod("LoadPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [preview]);

        var rows = (TableView)GetPrivateField(window, "_rowList");
        var merge = (Button)GetPrivateField(window, "_mergeButton");
        var mergeHint = (TextBlock)GetPrivateField(window, "_mergeActionHint");

        rows.SelectedIndex = 0;
        Assert.False(merge.IsEnabled);
        Assert.Contains("參考字幕", mergeHint.Text);

        rows.SelectedIndex = 1;
        Assert.False(merge.IsEnabled);
        Assert.Contains("空白字幕", mergeHint.Text);
    }

    [AvaloniaFact]
    public void ReviewPanelBlocksAiThatDropsAssLineBreak()
    {
        using var host = new Host();
        var vm = host.Vm;
        var row = new SubtitleLineViewModel { Number = 1, Text = "甲\\N乙" };
        vm.Subtitles.Add(row);
        var snapshot = vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [row.Id]);
        var window = new SubtitleReviewWindow(vm, snapshot, selection, null);
        var preview = new ReviewPreviewResult(
            [new ReviewPreviewRow(1, row.Text, "甲乙", false, [], "not_provided", ReviewEvidencePacket.Empty)],
            [], 1, [], new Dictionary<int, IReadOnlyList<string>>(), [], [], true);
        typeof(SubtitleReviewWindow).GetMethod("LoadPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [preview]);
        Assert.False(((Button)GetPrivateField(window, "_applyAiButton")).IsEnabled);
        Assert.Contains("換行", ((TextBlock)GetPrivateField(window, "_rowWarning")).Text);
    }

    [AvaloniaFact]
    public void EvidenceAndFiltersChangeVisibilityOnlyAndPreserveHumanDecision()
    {
        using var host = new Host();
        var vm = host.Vm;
        var first = new SubtitleLineViewModel { Number = 1, Text = "舊一" };
        var second = new SubtitleLineViewModel { Number = 2, Text = "舊二" };
        var unchanged = new SubtitleLineViewModel { Number = 3, Text = "原三" };
        foreach (var row in new[] { first, second, unchanged }) vm.Subtitles.Add(row);

        var snapshot = vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [first.Id, second.Id, unchanged.Id]);
        var window = new SubtitleReviewWindow(vm, snapshot, selection, null);
        var riskyEvidence = ReviewEvidencePacket.Empty with
        {
            Available = true,
            Alternatives =
            [
                new ReviewEvidenceAlternative(
                    "asr_observation", "obs-1", "ct2", null, "候選一", [], null, ""),
            ],
            Risk = new ReviewEvidenceRisk(
                70,
                "high",
                ["likely_deletion"],
                [new ReviewEvidenceFlag("likely_deletion", "high", 70, "")]),
        };
        var preview = new ReviewPreviewResult(
            [
                new ReviewPreviewRow(1, "舊一", "新一", false, [], "matched", riskyEvidence),
                new ReviewPreviewRow(2, "舊二", "新二", false, [], "not_provided", ReviewEvidencePacket.Empty),
                new ReviewPreviewRow(3, "原三", "原三", false, [], "not_provided", ReviewEvidencePacket.Empty),
            ],
            [],
            1,
            [],
            new Dictionary<int, IReadOnlyList<string>>(),
            [],
            [],
            true);
        typeof(SubtitleReviewWindow).GetMethod("LoadPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [preview]);

        var rowList = (TableView)GetPrivateField(window, "_rowList");
        var highRiskOnly = (CheckBox)GetPrivateField(window, "_highRiskOnly");
        var changedOnly = (CheckBox)GetPrivateField(window, "_changedOnly");
        var evidenceSummary = (TextBlock)GetPrivateField(window, "_evidenceSummary");
        var setDecision = typeof(SubtitleReviewWindow).GetMethod(
            "SetDecision", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Equal(3, rowList.ItemsSource!.Cast<object>().Count());
        rowList.SelectedIndex = 0;
        Assert.Contains("likely_deletion", evidenceSummary.Text);
        setDecision.Invoke(window, [ReviewDecisionKind.Apply]);
        Assert.Equal(["舊一", "舊二", "原三"], vm.Subtitles.Select(row => row.Text));

        highRiskOnly.IsChecked = true;
        Assert.Single(rowList.ItemsSource!.Cast<object>());
        Assert.Contains("套用 AI", rowList.ItemsSource!.Cast<object>().Single().ToString());

        highRiskOnly.IsChecked = false;
        changedOnly.IsChecked = true;
        Assert.Equal(2, rowList.ItemsSource!.Cast<object>().Count());

        changedOnly.IsChecked = false;
        rowList.SelectedIndex = 1;
        Assert.Contains("Evidence 未提供", evidenceSummary.Text);
        Assert.Contains("不能據此判定低風險", evidenceSummary.Text);
        Assert.Equal(["舊一", "舊二", "原三"], vm.Subtitles.Select(row => row.Text));
    }

    [AvaloniaFact]
    public void ReviewTableShowsEveryPreviewAndBulkAppliesEverySafeAiResponse()
    {
        using var host = new Host();
        var vm = host.Vm;
        var first = new SubtitleLineViewModel { Number = 1, Text = "舊一" };
        var second = new SubtitleLineViewModel { Number = 2, Text = "原二" };
        vm.Subtitles.Add(first);
        vm.Subtitles.Add(second);
        var snapshot = vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [first.Id, second.Id]);
        var window = new SubtitleReviewWindow(vm, snapshot, selection, null);
        var preview = new ReviewPreviewResult(
            [
                new ReviewPreviewRow(1, "舊一", "新一", false, [], "not_provided", ReviewEvidencePacket.Empty),
                new ReviewPreviewRow(2, "原二", "原二", false, [], "not_provided", ReviewEvidencePacket.Empty),
            ],
            [], 1, [], new Dictionary<int, IReadOnlyList<string>>(), [], [], true);
        typeof(SubtitleReviewWindow).GetMethod("LoadPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [preview]);

        var table = (TableView)GetPrivateField(window, "_rowList");
        Assert.Equal(["編號", "決定", "原文", "AI 校閱", "檢查", "風險"],
            table.Columns.Select(column => column.Header?.ToString() ?? string.Empty).ToArray());
        Assert.Equal(2, table.ItemsSource!.Cast<object>().Count());

        var applyAll = (Button)GetPrivateField(window, "_applyAllAiButton");
        Assert.True(applyAll.IsEnabled);
        Assert.Equal("一鍵套用全部 AI 結果到 SE 字幕", applyAll.Content);
        Assert.Equal(["舊一", "原二"], vm.Subtitles.Select(row => row.Text));
    }

    [AvaloniaFact]
    public void BulkAiApplyWritesEveryParsedTextIntoTheLiveSeRowsWithOneUndo()
    {
        using var host = new Host();
        var vm = host.Vm;
        var first = new SubtitleLineViewModel { Number = 1, Text = "舊一", Style = "Main" };
        var second = new SubtitleLineViewModel { Number = 2, Text = "舊二", Actor = "旁白" };
        vm.Subtitles.Add(first);
        vm.Subtitles.Add(second);
        host.History.Do(vm.MakeUndoRedoObject("before review"));
        var snapshot = vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [first.Id, second.Id]);
        var window = new SubtitleReviewWindow(vm, snapshot, selection, null);
        var preview = new ReviewPreviewResult(
            [
                new ReviewPreviewRow(1, "舊一", "AI 新一", false, [], "not_provided", ReviewEvidencePacket.Empty),
                new ReviewPreviewRow(2, "舊二", "AI 新二", false, [], "not_provided", ReviewEvidencePacket.Empty),
            ],
            [], 2, [], new Dictionary<int, IReadOnlyList<string>>(), [], [], true);
        typeof(SubtitleReviewWindow).GetMethod("LoadPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [preview]);

        Assert.Equal(2, window.ApplyAllAiResponsesToSubtitle());
        Assert.Same(first, vm.Subtitles[0]);
        Assert.Same(second, vm.Subtitles[1]);
        Assert.Equal(["AI 新一", "AI 新二"], vm.Subtitles.Select(row => row.Text));
        Assert.Equal("Main", vm.Subtitles[0].Style);
        Assert.Equal("旁白", vm.Subtitles[1].Actor);
        Assert.Equal(2, host.History.UndoCount);

        vm.UndoCommand.Execute(null);
        Assert.Equal(["舊一", "舊二"], vm.Subtitles.Select(row => row.Text));
    }

    [AvaloniaFact]
    public void BulkAiApplyKeepsMissingResponsesAsOriginalAndAppliesTheRest()
    {
        using var host = new Host();
        var vm = host.Vm;
        var first = new SubtitleLineViewModel { Number = 1, Text = "舊一" };
        var second = new SubtitleLineViewModel { Number = 2, Text = "舊二" };
        vm.Subtitles.Add(first);
        vm.Subtitles.Add(second);
        host.History.Do(vm.MakeUndoRedoObject("before review"));
        var snapshot = vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [first.Id, second.Id]);
        var window = new SubtitleReviewWindow(vm, snapshot, selection, null);
        var preview = new ReviewPreviewResult(
            [
                new ReviewPreviewRow(1, "舊一", "AI 新一", false, [], "not_provided", ReviewEvidencePacket.Empty),
                new ReviewPreviewRow(2, "舊二", "", false, [], "not_provided", ReviewEvidencePacket.Empty),
            ],
            [], 1, [], new Dictionary<int, IReadOnlyList<string>>(), [2], [], false);
        typeof(SubtitleReviewWindow).GetMethod("LoadPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [preview]);

        var applyAll = (Button)GetPrivateField(window, "_applyAllAiButton");
        var hint = (TextBlock)GetPrivateField(window, "_bulkActionHint");
        Assert.True(applyAll.IsEnabled);
        Assert.Contains("自動保留原文", hint.Text);

        Assert.Equal(1, window.ApplyAllAiResponsesToSubtitle());
        Assert.Equal(["AI 新一", "舊二"], vm.Subtitles.Select(row => row.Text));
        Assert.Equal(2, host.History.UndoCount);

        var state = (ReviewWorkflowState)GetPrivateField(window, "_state");
        Assert.Equal(ReviewDecisionKind.Apply, state.Rows[0].Decision);
        Assert.Equal(ReviewDecisionKind.Keep, state.Rows[1].Decision);

        vm.UndoCommand.Execute(null);
        Assert.Equal(["舊一", "舊二"], vm.Subtitles.Select(row => row.Text));
    }

    [AvaloniaFact]
    public void ResponseTabShowsCompleteAndIncompleteImportedBatches()
    {
        using var host = new Host();
        foreach (var text in new[] { "原一", "原二", "原三", "原四" })
            host.Vm.Subtitles.Add(new SubtitleLineViewModel { Text = text });
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, snapshot.Rows.Select(row => row.Id).ToArray());
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);

        window.GetType().GetMethod("RebuildPromptExpectedIds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [2]);
        var documents = new (string Name, string Content)[]
        {
            ("AI回覆_第002批.md", ReviewResponse((3, "新三"))),
            ("AI回覆_第001批.md", ReviewResponse((1, "新一"), (2, "原二"))),
        };
        window.GetType().GetMethod("LoadResponseDocuments", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [documents]);

        var table = (TableView)GetPrivateField(window, "_responseStatusTable");
        Assert.Equal(["批次", "來源", "已解析", "未修改", "已修改", "缺漏", "衝突", "未知", "狀態"],
            table.Columns.Select(column => column.Header?.ToString() ?? string.Empty).ToArray());
        var statusRows = table.ItemsSource!.Cast<ResponseParseStatusRow>().ToArray();
        var statuses = statusRows.Select(row => row.ToString()).ToArray();
        Assert.Equal(2, statuses.Length);
        Assert.Contains("完成", statuses[0]);
        Assert.Contains("第001批", statuses[0]);
        Assert.Equal("1", statusRows[0].Unchanged);
        Assert.Equal("1", statusRows[0].Changed);
        Assert.Contains("尚未完成", statuses[1]);
        Assert.Contains("第002批", statuses[1]);
        Assert.Contains("4", statuses[1]);
        Assert.Equal("0", statusRows[1].Unchanged);
        Assert.Equal("1", statusRows[1].Changed);

        var responses = (string[])window.GetType().GetMethod(
            "CurrentResponses", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        Assert.Equal(documents.Select(document => document.Content), responses);
    }

    [AvaloniaFact]
    public void ResponseProgressIgnoresPunctuationAndWhitespaceWhenCountingChanges()
    {
        using var host = new Host();
        host.Vm.Subtitles.Add(new SubtitleLineViewModel { Text = "持續 學習，保持好奇！" });
        host.Vm.Subtitles.Add(new SubtitleLineViewModel { Text = "原二" });
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, snapshot.Rows.Select(row => row.Id).ToArray());
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);
        window.GetType().GetMethod("RebuildPromptExpectedIds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [2]);
        var documents = new (string Name, string Content)[]
        {
            ("AI回覆_第001批.md", ReviewResponse(
                (1, "持續學習, 保持好奇 !"),
                (2, "實質修改"))),
        };

        window.GetType().GetMethod("LoadResponseDocuments", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [documents]);

        var status = Assert.Single(((TableView)GetPrivateField(window, "_responseStatusTable"))
            .ItemsSource!.Cast<ResponseParseStatusRow>());
        Assert.Equal("1", status.Unchanged);
        Assert.Equal("1", status.Changed);
    }

    [AvaloniaFact]
    public void ResponseProgressTreatsBlankReviewedTextAsMissingInsteadOfUnchanged()
    {
        using var host = new Host();
        host.Vm.Subtitles.Add(new SubtitleLineViewModel { Text = "，！" });
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, snapshot.Rows.Select(row => row.Id).ToArray());
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);
        window.GetType().GetMethod("RebuildPromptExpectedIds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [1]);
        var documents = new (string Name, string Content)[]
        {
            ("AI回覆_第001批.md", ReviewResponse((1, " "))),
        };

        window.GetType().GetMethod("LoadResponseDocuments", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [documents]);

        var status = Assert.Single(((TableView)GetPrivateField(window, "_responseStatusTable"))
            .ItemsSource!.Cast<ResponseParseStatusRow>());
        Assert.Equal("0/1", status.Parsed);
        Assert.Equal("0", status.Unchanged);
        Assert.Equal("0", status.Changed);
        Assert.Equal("1", status.Missing);
        Assert.Equal("尚未完成", status.Status);
    }

    [AvaloniaFact]
    public async Task RepeatedPastedResponsesSplitIntoBatchesAndBuildOneCombinedPreview()
    {
        using var host = new Host();
        var first = new SubtitleLineViewModel { Text = "原一" };
        var second = new SubtitleLineViewModel { Text = "原二" };
        host.Vm.Subtitles.Add(first);
        host.Vm.Subtitles.Add(second);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [first.Id, second.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);
        window.GetType().GetMethod("RebuildPromptExpectedIds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [1]);
        var response = (TextBox)GetPrivateField(window, "_responseText");

        var addPastedResponse = window.GetType().GetMethod(
            "AddPastedResponse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        response.Text = ReviewResponse((1, "新一"));
        addPastedResponse.Invoke(window, null);
        response.Text = ReviewResponse((2, "新二"));
        addPastedResponse.Invoke(window, null);

        var responses = (string[])window.GetType().GetMethod(
            "CurrentResponses", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        Assert.Equal(2, responses.Length);
        await (Task)window.GetType().GetMethod(
            "PreparePreviewAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false])!;
        var rows = ((TableView)GetPrivateField(window, "_rowList")).ItemsSource!.Cast<object>().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Contains("待審", row.ToString()));
        var statuses = ((TableView)GetPrivateField(window, "_responseStatusTable"))
            .ItemsSource!.Cast<object>().Select(row => row.ToString()!).ToArray();
        Assert.Equal(2, statuses.Length);
        Assert.All(statuses, status => Assert.Contains("完成", status));
    }

    [AvaloniaFact]
    public async Task ResponseTypingDebouncesParseStatusAndUsesLatestText()
    {
        using var host = new Host();
        var first = new SubtitleLineViewModel { Text = "原一" };
        var second = new SubtitleLineViewModel { Text = "原二" };
        host.Vm.Subtitles.Add(first);
        host.Vm.Subtitles.Add(second);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [first.Id, second.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);
        window.GetType().GetMethod("RebuildPromptExpectedIds", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [2]);
        var response = (TextBox)GetPrivateField(window, "_responseText");

        response.Text = ReviewResponse((1, "新一"));
        response.Text = ReviewResponse((1, "新一"), (2, "新二"));
        await Task.Delay(350);

        var statuses = ((TableView)GetPrivateField(window, "_responseStatusTable"))
            .ItemsSource!.Cast<object>().Select(row => row.ToString()!).ToArray();
        Assert.Single(statuses);
        Assert.Contains("完成", statuses[0]);
        Assert.Contains("2/2", statuses[0]);
    }

    [AvaloniaFact]
    public async Task IncompleteMultiFileImportBuildsPreviewWithoutLeavingResponseTab()
    {
        using var host = new Host();
        var first = new SubtitleLineViewModel { Text = "原一" };
        var second = new SubtitleLineViewModel { Text = "原二" };
        host.Vm.Subtitles.Add(first);
        host.Vm.Subtitles.Add(second);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [first.Id, second.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null, initialTab: 1);
        var documents = new (string Name, string Content)[]
        {
            ("AI回覆_第001批.md", ReviewResponse((1, "新一"))),
        };

        var completeImport = window.GetType().GetMethod(
            "CompleteResponseImportAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(completeImport);
        await (Task)completeImport!.Invoke(window, [documents])!;

        Assert.Equal(2, ((TableView)GetPrivateField(window, "_rowList")).ItemsSource!.Cast<object>().Count());
        Assert.Equal(1, ((ListBox)GetPrivateField(window, "_workspaceNavigation")).SelectedIndex);
        Assert.True(((Button)GetPrivateField(window, "_applyAllAiButton")).IsEnabled);
        Assert.Contains("自動保留原文", ((TextBlock)GetPrivateField(window, "_bulkActionHint")).Text);

        var state = (ReviewWorkflowState)GetPrivateField(window, "_state");
        Assert.False(state.Rows[0].MissingResponse);
        Assert.True(state.Rows[1].MissingResponse);
        Assert.Equal(ReviewDecisionKind.Keep, state.Rows[1].Decision);
    }

    [AvaloniaFact]
    public async Task CompletedMultiFileImportBuildsPreviewWithoutLeavingResponseTab()
    {
        using var host = new Host();
        var first = new SubtitleLineViewModel { Text = "原一" };
        var second = new SubtitleLineViewModel { Text = "原二" };
        host.Vm.Subtitles.Add(first);
        host.Vm.Subtitles.Add(second);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [first.Id, second.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null, initialTab: 1);
        var documents = new (string Name, string Content)[]
        {
            ("AI回覆_第001批.md", ReviewResponse((1, "新一"))),
            ("AI回覆_第002批.md", ReviewResponse((2, "新二"))),
        };

        var completeImport = window.GetType().GetMethod(
            "CompleteResponseImportAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(completeImport);
        await (Task)completeImport!.Invoke(window, [documents])!;

        Assert.Equal(2, ((TableView)GetPrivateField(window, "_rowList")).ItemsSource!.Cast<object>().Count());
        Assert.Equal(1, ((ListBox)GetPrivateField(window, "_workspaceNavigation")).SelectedIndex);
    }

    [AvaloniaFact]
    public async Task BusyWorkDisablesTheWorkflowUntilItFinishes()
    {
        using var host = new Host();
        var row = new SubtitleLineViewModel { Text = "原文" };
        host.Vm.Subtitles.Add(row);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [row.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runBusy = window.GetType().GetMethod(
            "RunBusyAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(runBusy);

        var running = (Task)runBusy!.Invoke(window, ["處理中", (Func<Task>)(() => release.Task)])!;
        Assert.False(((SplitView)GetPrivateField(window, "_workspaceSplitView")).IsEnabled);
        release.SetResult();
        await running;
        Assert.True(((SplitView)GetPrivateField(window, "_workspaceSplitView")).IsEnabled);
    }

    [AvaloniaFact]
    public void ReviewWindowExposesAutomationNamesAndLiveStatus()
    {
        using var host = new Host();
        var row = new SubtitleLineViewModel { Text = "原文" };
        host.Vm.Subtitles.Add(row);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [row.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);

        Assert.Equal("AI 字幕校閱工作區", AutomationProperties.GetName((SplitView)GetPrivateField(window, "_workspaceSplitView")));
        Assert.Equal("校閱流程導覽", AutomationProperties.GetName((ListBox)GetPrivateField(window, "_workspaceNavigation")));
        Assert.Equal("字幕校閱結果", AutomationProperties.GetName((TableView)GetPrivateField(window, "_rowList")));
        Assert.Equal("AI 回覆輸入", AutomationProperties.GetName((TextBox)GetPrivateField(window, "_responseText")));
        Assert.Equal("手動修正文字", AutomationProperties.GetName((TextBox)GetPrivateField(window, "_manualText")));
        var status = (TextBlock)GetPrivateField(window, "_status");
        Assert.Equal("校閱狀態", AutomationProperties.GetName(status));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
    }

    [AvaloniaFact]
    public void ReviewKeyboardShortcutsNavigateDecideAndPlay()
    {
        using var host = new Host();
        var first = new SubtitleLineViewModel { Text = "原一" };
        var second = new SubtitleLineViewModel { Text = "原二" };
        host.Vm.Subtitles.Add(first);
        host.Vm.Subtitles.Add(second);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [first.Id, second.Id]);
        Guid? played = null;
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, id => played = id);
        var preview = new ReviewPreviewResult(
            [
                new ReviewPreviewRow(1, first.Text, "新一", false, [], "not_provided", ReviewEvidencePacket.Empty),
                new ReviewPreviewRow(2, second.Text, "新二", false, [], "not_provided", ReviewEvidencePacket.Empty),
            ],
            [], 2, [], new Dictionary<int, IReadOnlyList<string>>(), [], [], true);
        typeof(SubtitleReviewWindow).GetMethod("LoadPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [preview]);

        var rows = (TableView)GetPrivateField(window, "_rowList");
        var shortcut = window.GetType().GetMethod("HandleReviewShortcut", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True((bool)shortcut.Invoke(window, [Key.Down, KeyModifiers.Control])!);
        Assert.Equal(1, rows.SelectedIndex);
        Assert.True((bool)shortcut.Invoke(window, [Key.Up, KeyModifiers.Control])!);
        Assert.Equal(0, rows.SelectedIndex);

        Assert.True((bool)shortcut.Invoke(window, [Key.Enter, KeyModifiers.Control])!);
        Assert.Contains("套用 AI", rows.ItemsSource!.Cast<object>().First().ToString());
        Assert.Equal(1, rows.SelectedIndex);
        Assert.True((bool)shortcut.Invoke(window, [Key.K, KeyModifiers.Control])!);
        Assert.Contains("保留", rows.ItemsSource!.Cast<object>().Last().ToString());
        Assert.True((bool)shortcut.Invoke(window, [Key.P, KeyModifiers.Control])!);
        Assert.Equal(second.Id, played);
        Assert.False((bool)shortcut.Invoke(window, [Key.P, KeyModifiers.None])!);
    }

    [AvaloniaFact]
    public void PromptExportWritesZeroPaddedMarkdownFilesInOneFolder()
    {
        using var host = new Host();
        var row = new SubtitleLineViewModel { Text = "原文" };
        host.Vm.Subtitles.Add(row);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [row.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);
        var state = (ReviewWorkflowState)GetPrivateField(window, "_state");
        state.Prompts.AddRange(["第一批提示詞", "第二批提示詞"]);
        var root = Path.Combine(Path.GetTempPath(), "subtitle-review-prompts-" + Guid.NewGuid().ToString("N"));

        try
        {
            var output = (string)window.GetType().GetMethod(
                "ExportPromptFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [root])!;
            Assert.Equal(["AI校閱提示詞_第001批.md", "AI校閱提示詞_第002批.md"],
                Directory.GetFiles(output).Select(path => Path.GetFileName(path)!).Order().ToArray());
            Assert.Equal("第一批提示詞", File.ReadAllText(Path.Combine(output, "AI校閱提示詞_第001批.md")));
            Assert.Equal("第二批提示詞", File.ReadAllText(Path.Combine(output, "AI校閱提示詞_第002批.md")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReviewSettingsSeedsEmbeddedGlossaryIntoDataAndPreservesUserChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "subtitle-review-data-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SubtitleReviewSettingsStore(root);
            var resolved = store.ResolveGlossaryPath(string.Empty);
            Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(resolved), StringComparison.OrdinalIgnoreCase);

            var seededRows = GlossaryRepository.Read(resolved);
            Assert.Contains(seededRows, row => row.Source == "Open Captin" && row.Target == "Open Caption");

            var protectedPath = Path.Combine(root, "glossary", "protected_phrases.json");
            Assert.True(File.Exists(protectedPath));
            Assert.Contains("Open Caption", GlossaryRepository.LoadProtectedPhrases(protectedPath));

            File.WriteAllText(resolved, "user-edited");
            Assert.Equal(resolved, store.ResolveGlossaryPath(string.Empty));
            Assert.Equal("user-edited", File.ReadAllText(resolved));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReviewSettingsRoundTripPersistsCompletePromptTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), "subtitle-review-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SubtitleReviewSettingsStore(root);
            store.Save(new SubtitleReviewSettings(
                "D:\\terms.csv",
                80,
                "D:\\evidence.jsonl",
                "  # 我的完整提示詞\n\n請保留產品專有名詞。  "));

            var loaded = store.Load();
            Assert.Equal("D:\\terms.csv", loaded.GlossaryPath);
            Assert.Equal(80, loaded.ChunkSize);
            Assert.Equal("D:\\evidence.jsonl", loaded.EvidencePath);
            Assert.Equal("# 我的完整提示詞\n\n請保留產品專有名詞。", loaded.PromptTemplate);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReviewSettingsCopiesAndExportsMultipleTxtAttachmentsWithoutChangingBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "subtitle-review-attachments-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var export = Path.Combine(root, "export");
        try
        {
            Directory.CreateDirectory(source);
            var first = Path.Combine(source, "第一場.txt");
            var second = Path.Combine(source, "第二場.txt");
            File.WriteAllBytes(first, [0xEF, 0xBB, 0xBF, 0xE4, 0xBD, 0x9B]);
            File.WriteAllBytes(second, [0x41, 0x00, 0x42, 0x00]);

            var store = new SubtitleReviewSettingsStore(Path.Combine(root, "data"));
            store.Save(new SubtitleReviewSettings(
                "D:\\terms.csv",
                80,
                "D:\\evidence.jsonl",
                "# 完整提示詞",
                "Acme Studio terminology reference",
                []));
            store.AttachReviewReferenceFiles([first, second]);

            var loaded = store.Load();
            Assert.Equal("Acme Studio terminology reference", loaded.ReferenceMaterial);
            Assert.Equal(["第一場.txt", "第二場.txt"], loaded.ReviewReferenceFiles);
            Assert.All(store.ResolveReviewReferenceFiles(), path =>
                Assert.StartsWith(Path.Combine(root, "data", "references"), path, StringComparison.OrdinalIgnoreCase));

            File.Delete(first);
            File.Delete(second);
            store.ExportReviewReferenceFiles(export);
            Assert.Equal([0xEF, 0xBB, 0xBF, 0xE4, 0xBD, 0x9B], File.ReadAllBytes(Path.Combine(export, "第一場.txt")));
            Assert.Equal([0x41, 0x00, 0x42, 0x00], File.ReadAllBytes(Path.Combine(export, "第二場.txt")));

            var managed = store.ResolveReviewReferenceFiles();
            File.Delete(managed[0]);
            Assert.Throws<FileNotFoundException>(() => store.ResolveReviewReferenceFiles());
            store.ClearReviewReferenceFiles();
            Assert.Empty(store.Load().ReviewReferenceFiles);
            Assert.False(File.Exists(managed[1]));

            var lockedSource = Path.Combine(source, "鎖定中.txt");
            File.WriteAllText(lockedSource, "locked");
            store.AttachReviewReferenceFiles([lockedSource]);
            var lockedManaged = store.ResolveReviewReferenceFiles().Single();
            using (File.Open(lockedManaged, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var undeleted = store.ClearReviewReferenceFiles();
                Assert.Equal(["鎖定中.txt"], undeleted);
                Assert.Empty(store.Load().ReviewReferenceFiles);
                Assert.True(File.Exists(lockedManaged));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReviewSettingsWithoutSavedTemplateUsesTheCompleteDefault()
    {
        var previousUiCulture = System.Globalization.CultureInfo.CurrentUICulture;
        var root = Path.Combine(Path.GetTempPath(), "subtitle-review-default-template-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("zh-TW");
            var loaded = new SubtitleReviewSettingsStore(root).Load();
            Assert.Equal(ReviewResources.PromptTemplate.Trim(), loaded.PromptTemplate);
            Assert.Contains("## 1. 角色與核心任務", loaded.PromptTemplate);
            Assert.Contains("## 3. 回覆格式（最高優先）", loaded.PromptTemplate);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = previousUiCulture;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public void PromptTabEditsTheSavedTemplateAndKeepsGeneratedOutputReadOnly()
    {
        using var host = new Host();
        var row = new SubtitleLineViewModel { Number = 1, Text = "原文" };
        host.Vm.Subtitles.Add(row);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [row.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);

        var template = (TextBox)GetPrivateField(window, "_promptTemplateText");
        var generated = (TextBox)GetPrivateField(window, "_promptText");
        Assert.False(template.IsReadOnly);
        Assert.True(generated.IsReadOnly);
    }

    [AvaloniaFact]
    public void PromptSectionNavigationListsNumberedSectionsAndMovesCaret()
    {
        var previousUiCulture = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("zh-TW");
        using var host = new Host();
        var row = new SubtitleLineViewModel { Number = 1, Text = "原文" };
        host.Vm.Subtitles.Add(row);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [row.Id]);
        var settingsRoot = Path.Combine(Path.GetTempPath(), "subtitle-review-section-nav-" + Guid.NewGuid().ToString("N"));
        var window = new SubtitleReviewWindow(
            host.Vm,
            snapshot,
            selection,
            null,
            settingsStore: new SubtitleReviewSettingsStore(settingsRoot),
            sessionStore: new ReviewSessionStore(Path.Combine(settingsRoot, "sessions")));
        window.Show();

        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var template = (TextBox)GetPrivateField(window, "_promptTemplateText");
            var navigation = (StackPanel)GetPrivateField(window, "_promptSectionButtons");
            var scroller = (ScrollViewer)GetPrivateField(window, "_promptSectionScroller");
            var buttons = navigation.Children.OfType<Button>().ToArray();

            Assert.Equal(Orientation.Horizontal, navigation.Orientation);
            Assert.Equal(ScrollBarVisibility.Auto, scroller.HorizontalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Disabled, scroller.VerticalScrollBarVisibility);
            Assert.Equal(3, buttons.Length);
            Assert.Equal("1. 角色與核心任務", buttons[0].Content?.ToString());
            Assert.Equal("3. 回覆格式（最高優先）", buttons[^1].Content?.ToString());

            buttons[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var expected = template.Text!.IndexOf("## 3. 回覆格式（最高優先）", StringComparison.Ordinal);
            Assert.True(expected >= 0);
            Assert.Equal(expected, template.CaretIndex);
        }
        finally
        {
            window.Close();
            System.Globalization.CultureInfo.CurrentUICulture = previousUiCulture;
            if (Directory.Exists(settingsRoot))
                Directory.Delete(settingsRoot, recursive: true);
        }
    }

    [AvaloniaFact]
    public void PromptUtilityDrawerDoesNotResizePrimaryEditor()
    {
        using var host = new Host();
        var row = new SubtitleLineViewModel { Number = 1, Text = "原文" };
        host.Vm.Subtitles.Add(row);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [row.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);

        try
        {
            window.Width = 1024;
            window.Height = 720;
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var template = (TextBox)GetPrivateField(window, "_promptTemplateText");
            var utility = (SplitView)GetPrivateField(window, "_promptUtilitySplitView");
            var tabs = (TabControl)GetPrivateField(window, "_promptUtilityTabs");

            Assert.Equal(SplitViewPanePlacement.Right, utility.PanePlacement);
            Assert.Equal(SplitViewDisplayMode.Overlay, utility.DisplayMode);
            Assert.False(utility.IsPaneOpen);
            Assert.Equal(2, tabs.ItemCount);
            Assert.True(template.Bounds.Height >= 360);
            Assert.True(template.Bounds.Width >= 500);

            var widthBefore = template.Bounds.Width;
            var heightBefore = template.Bounds.Height;
            utility.IsPaneOpen = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.Equal(widthBefore, template.Bounds.Width);
            Assert.Equal(heightBefore, template.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ResponseParseProgressIsDirectlyVisibleInMainPage()
    {
        using var host = new Host();
        var row = new SubtitleLineViewModel { Number = 1, Text = "原文" };
        host.Vm.Subtitles.Add(row);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [row.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);

        try
        {
            window.Width = 1024;
            window.Height = 720;
            window.Show();

            var navigation = (ListBox)GetPrivateField(window, "_workspaceNavigation");
            var content = (ContentControl)GetPrivateField(window, "_workspaceContent");
            var table = (TableView)GetPrivateField(window, "_responseStatusTable");

            navigation.SelectedIndex = 1;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.IsType<Grid>(content.Content);
            Assert.True(table.Bounds.Width > 0);
            Assert.True(table.Bounds.Height > 0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ReviewWorkspaceUsesSplitViewAndSinglePanelDetails()
    {
        using var host = new Host();
        var row = new SubtitleLineViewModel { Number = 1, Text = "原文" };
        host.Vm.Subtitles.Add(row);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [row.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);

        var split = (SplitView)GetPrivateField(window, "_workspaceSplitView");
        var navigation = (ListBox)GetPrivateField(window, "_workspaceNavigation");
        var content = (ContentControl)GetPrivateField(window, "_workspaceContent");
        var detailTabs = (TabControl)GetPrivateField(window, "_reviewDetailTabs");
        var promptUtility = (SplitView)GetPrivateField(window, "_promptUtilitySplitView");
        var reviewUtility = (SplitView)GetPrivateField(window, "_reviewUtilitySplitView");
        var glossaryTabs = (TabControl)GetPrivateField(window, "_glossaryTabs");
        var apply = typeof(SubtitleReviewWindow).GetMethod(
            "ApplyResponsiveLayout", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Equal(4, navigation.ItemCount);
        Assert.Equal(3, detailTabs.ItemCount);
        Assert.Equal(2, glossaryTabs.ItemCount);

        foreach (var utility in new[] { promptUtility, reviewUtility })
        {
            Assert.Equal(SplitViewPanePlacement.Right, utility.PanePlacement);
            Assert.Equal(SplitViewDisplayMode.Overlay, utility.DisplayMode);
            Assert.False(utility.IsPaneOpen);
        }

        apply.Invoke(window, [900d]);
        Assert.Equal(SplitViewDisplayMode.Overlay, split.DisplayMode);
        Assert.False(split.IsPaneOpen);

        apply.Invoke(window, [1280d]);
        Assert.Equal(SplitViewDisplayMode.Inline, split.DisplayMode);
        Assert.True(split.IsPaneOpen);

        navigation.SelectedIndex = 2;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.NotNull(content.Content);
    }

    [AvaloniaFact]
    public void UtilityDrawersOverlayWithoutChangingPrimaryPageBounds()
    {
        using var host = new Host();
        var row = new SubtitleLineViewModel { Number = 1, Text = "原文" };
        host.Vm.Subtitles.Add(row);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [row.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);
        var navigation = (ListBox)GetPrivateField(window, "_workspaceNavigation");
        var utilities = new[]
        {
            (Index: 0, Split: (SplitView)GetPrivateField(window, "_promptUtilitySplitView")),
            (Index: 2, Split: (SplitView)GetPrivateField(window, "_reviewUtilitySplitView")),
        };

        try
        {
            window.Width = 1024;
            window.Height = 720;
            window.Show();

            foreach (var item in utilities)
            {
                navigation.SelectedIndex = item.Index;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var primary = Assert.IsAssignableFrom<Control>(item.Split.Content);
                var widthBefore = primary.Bounds.Width;
                var heightBefore = primary.Bounds.Height;
                Assert.True(widthBefore > 0);
                Assert.True(heightBefore > 0);

                item.Split.IsPaneOpen = true;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Assert.Equal(widthBefore, primary.Bounds.Width);
                Assert.Equal(heightBefore, primary.Bounds.Height);

                item.Split.IsPaneOpen = false;
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ReviewWorkspaceFitsCommonDesktopWindowSizesWithoutTopLevelOverflow()
    {
        using var host = new Host();
        var row = new SubtitleLineViewModel { Number = 1, Text = "原文" };
        host.Vm.Subtitles.Add(row);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [row.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);
        var split = (SplitView)GetPrivateField(window, "_workspaceSplitView");
        var navigation = (ListBox)GetPrivateField(window, "_workspaceNavigation");
        var content = (ContentControl)GetPrivateField(window, "_workspaceContent");
        var detailTabs = (TabControl)GetPrivateField(window, "_reviewDetailTabs");
        var apply = typeof(SubtitleReviewWindow).GetMethod(
            "ApplyResponsiveLayout", BindingFlags.Instance | BindingFlags.NonPublic)!;

        try
        {
            window.Show();

            foreach (var (width, height) in new[]
                     {
                         (800d, 600d),
                         (1024d, 640d),
                         (1280d, 720d),
                         (1536d, 864d),
                     })
            {
                window.Width = width;
                window.Height = height;
                apply.Invoke(window, [width]);
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Assert.True(split.Bounds.Width <= window.Bounds.Width);
                Assert.True(split.Bounds.Height <= window.Bounds.Height);
                Assert.True(content.Bounds.Width <= split.Bounds.Width);
                Assert.True(content.Bounds.Height <= split.Bounds.Height);
                Assert.True(content.Bounds.Width > 0);
                Assert.True(content.Bounds.Height > 0);

                navigation.SelectedIndex = 2;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.True(detailTabs.Bounds.Width <= content.Bounds.Width);
                Assert.True(detailTabs.Bounds.Height <= content.Bounds.Height);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ChangingReviewSettingsClearsResponseOnlyState()
    {
        using var host = new Host();
        var row = new SubtitleLineViewModel { Number = 1, Text = "原文" };
        host.Vm.Subtitles.Add(row);
        var snapshot = host.Vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [row.Id]);
        var window = new SubtitleReviewWindow(host.Vm, snapshot, selection, null);

        var state = (ReviewWorkflowState)GetPrivateField(window, "_state");
        var template = (TextBox)GetPrivateField(window, "_promptTemplateText");
        var store = (SubtitleReviewSettingsStore)GetPrivateField(window, "_settingsStore");
        var originalSettings = store.Load();
        try
        {
            state.ResponseDocuments.Add(new ResponseDocument("舊回覆.md", ReviewResponse((1, "舊 AI 回覆"))));
            Assert.Empty(state.Prompts);
            Assert.Empty(state.Rows);

            template.Text = originalSettings.PromptTemplate + "\n\n# regression-change";
            var saved = (bool)window.GetType().GetMethod(
                "SaveSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;

            Assert.True(saved);
            Assert.Empty(state.ResponseDocuments);
            Assert.Empty(state.ResponseStatuses);
            Assert.Empty(state.Rows);
            Assert.Empty(state.Prompts);
            Assert.Contains("已清除舊提示詞、AI 回覆與預覽", ((TextBlock)GetPrivateField(window, "_status")).Text);
        }
        finally
        {
            store.Save(originalSettings);
        }
    }

    [AvaloniaFact]
    public void ResetForChangedReviewSourcesReturnsWindowToWritableFreshState()
    {
        using var host = new Host();
        var vm = host.Vm;
        var row = new SubtitleLineViewModel { Number = 1, Text = "原文" };
        vm.Subtitles.Add(row);
        var snapshot = vm.CaptureSubtitleReviewDocument();
        var selection = ReviewSelection.Create(snapshot, [row.Id]);
        var window = new SubtitleReviewWindow(vm, snapshot, selection, null);

        var state = (ReviewWorkflowState)GetPrivateField(window, "_state");
        state.SessionInvalidated = true;
        state.UnknownResponseIdCount = 2;
        state.Prompts.Add("舊提示詞");
        state.PromptExpectedIds.Add([1]);
        state.ResponseDocuments.Add(new ResponseDocument("舊回覆.md", "舊回覆"));
        state.ResponseStatuses.Add(ResponseParseStatusRow.NotImported(1, [1]));
        state.Rows.Add(new ReviewUiRow(
            1, row.Id, 1, "原文", "AI 新文", false, false, true, ReviewDecisionKind.Pending));
        var response = (TextBox)GetPrivateField(window, "_responseText");
        response.Text = "舊回覆";

        window.GetType().GetMethod(
            "ResetReviewStateForNewSources", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, ["來源已變更"]);

        Assert.False(state.SessionInvalidated);
        Assert.Equal(0, state.UnknownResponseIdCount);
        Assert.Empty(state.Prompts);
        Assert.Empty(state.PromptExpectedIds);
        Assert.Empty(state.Rows);
        Assert.Empty(state.ResponseDocuments);
        Assert.Empty(state.ResponseStatuses);
        Assert.Equal(string.Empty, response.Text);
        var status = (TextBlock)GetPrivateField(window, "_status");
        Assert.Equal("來源已變更", status.Text);
    }

    private static object GetPrivateField(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static string ReviewResponse(params (int Id, string Text)[] rows) =>
        "## 校閱結果\n| 編號 | 原文 | 校閱後文字 |\n|---:|---|---|\n" +
        string.Join("\n", rows.Select(row => $"| {row.Id} | 原文 | {row.Text} |"));

    private sealed class Host : IDisposable
    {
        private readonly IServiceProvider _previous = Locator.Services;
        private readonly SettingsScope _settings = new("General.CheckForUpdatesOnStartup");
        private readonly ServiceProvider _services;
        public MainViewModel Vm { get; }
        public IUndoRedoManager History { get; }

        public Host()
        {
            Se.Settings.General.CheckForUpdatesOnStartup = false;
            var services = new ServiceCollection();
            services.AddSubtitleEditServices();
            _services = services.BuildServiceProvider();
            Locator.Services = _services;
            Vm = _services.GetRequiredService<MainViewModel>();
            History = (IUndoRedoManager)typeof(MainViewModel)
                .GetField("_undoRedoManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Vm)!;
            History.StopChangeDetection();
        }

        public void Dispose()
        {
            foreach (var name in new[] { "_positionTimer", "_cursorTimer", "_slowTimer", "_dropDownFormatsSearchTimer" })
            {
                var timer = typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Vm);
                timer?.GetType().GetMethod("Stop", Type.EmptyTypes)?.Invoke(timer, null);
            }
            _services.GetRequiredService<IAutoBackupService>().StopAutobackup();
            History.StopChangeDetection();
            Vm.VideoPlayerControl = null;
            Vm.AudioVisualizer = null;
            Locator.Services = _previous;
            _services.Dispose();
            _settings.Dispose();
        }
    }
}
