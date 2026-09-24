using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Styling;

namespace Nikse.SubtitleEdit.Features.Main.Layout;

/// <summary>Compact waveform actions plus advanced flyout; preserves native waveform geometry.</summary>
public static class ReversibleAudioSyncUi
{
    private sealed class MenuActions { public Action? ToggleComparison { get; set; } }
    private static readonly ConditionalWeakTable<MainViewModel, MenuActions> Actions = new();

    public static Button CreateToolbarButton(MainViewModel vm)
    {
        var actions = Actions.GetValue(vm, _ => new MenuActions());
        var flyout = CreateAdvancedFlyout(vm, () => actions.ToggleComparison?.Invoke());
        var button = new Button
        {
            Name = "ReversibleAudioSyncToolbarButton",
            Padding = new Thickness(8, 4),
            Background = Avalonia.Media.Brushes.Transparent,
            Flyout = flyout,
            [AutomationProperties.NameProperty] = "音訊剪修",
            [ToolTip.TipProperty] = "更多音訊工具、專案歷史與設定",
        };
        button.Bind(Button.ContentProperty,
            new Binding(nameof(vm.SynchronousAudioProjectStatus)) { Source = vm });
        return button;
    }

    private static Flyout CreateAdvancedFlyout(MainViewModel vm, Action toggleComparison)
    {
        Application.Current!.TryFindResource(typeof(FlyoutPresenter), out var presenterDefaultTheme);
        return new Flyout
        {
            Content = AudioTrimPanel.Create(vm, toggleComparison),
            FlyoutPresenterTheme = new ControlTheme(typeof(FlyoutPresenter))
            {
                BasedOn = presenterDefaultTheme as ControlTheme,
                Setters =
                {
                    new Setter(FlyoutPresenter.MaxWidthProperty, 880d),
                    new Setter(FlyoutPresenter.MaxHeightProperty, 700d),
                },
            },
        };
    }

    public static Grid Attach(Grid mainGrid,MainViewModel vm)
    {
        if(vm.AudioVisualizer==null)return mainGrid;
        vm.AudioVisualizer.IsAudioRangeSelectionMode=vm.IsSynchronousAudioRangeSelectionEnabled;
        Grid? comparisonHost=null;
        Grid? nativeHost=null;
        void CloseComparison()
        {
            if(comparisonHost==null || nativeHost==null)return;
            mainGrid.Children.Remove(comparisonHost);
            foreach(var child in nativeHost.Children.ToArray())
            {
                nativeHost.Children.Remove(child);
                mainGrid.Children.Add(child);
            }
            comparisonHost=null;nativeHost=null;
        }
        void ToggleComparison()
        {
            if(comparisonHost!=null){CloseComparison();return;}
            nativeHost=new Grid{ClipToBounds=true};
            foreach(var child in mainGrid.Children.Where(c=>Grid.GetRow(c)==0).ToArray())
            {
                mainGrid.Children.Remove(child);nativeHost.Children.Add(child);
            }
            var comparison=new DualTimelinePanel(vm);
            comparison.ShowOriginalComparison();
            comparison.CloseRequested+=CloseComparison;
            comparisonHost=new Grid{RowDefinitions=new RowDefinitions("*,6,2*"),ClipToBounds=true};
            comparisonHost.Children.Add(nativeHost);
            var splitter=new GridSplitter{Height=6,ResizeDirection=GridResizeDirection.Rows,ResizeBehavior=GridResizeBehavior.PreviousAndNext,HorizontalAlignment=HorizontalAlignment.Stretch};
            Grid.SetRow(splitter,1);comparisonHost.Children.Add(splitter);
            Grid.SetRow(comparison,2);comparisonHost.Children.Add(comparison);
            mainGrid.Children.Add(comparisonHost);
        }
        Actions.GetValue(vm, _ => new MenuActions()).ToggleComparison = ToggleComparison;
        vm.AudioComparisonToggleAction = ToggleComparison;

        var waveformToolbar = mainGrid.Children
            .OfType<StackPanel>()
            .FirstOrDefault(c => Grid.GetRow(c) == 1);
        if (waveformToolbar != null)
        {
            waveformToolbar.Children.Add(AudioTrimQuickBar.Create(
                vm,
                () => CreateAdvancedFlyout(vm, ToggleComparison)));
        }

        // Context-menu fallback remains available when the main toolbar is hidden.
        var context=vm.AudioVisualizer.MenuFlyout;
        foreach(var old in context.Items.OfType<MenuItem>().Where(i=>Equals(i.Header,"音訊剪修")).ToArray())context.Items.Remove(old);
        context.Items.Insert(0,CreateMenu(vm,ToggleComparison));
        return mainGrid;
    }

    private static MenuItem CreateMenu(MainViewModel vm, Action toggleComparison)
    {
        var root = new MenuItem { Header = "音訊剪修" };

        var range = new MenuItem { Header = "音訊區間選取（可跨字幕）", ToggleType = MenuItemToggleType.CheckBox };
        range.Bind(MenuItem.IsCheckedProperty,
            new Binding(nameof(vm.IsSynchronousAudioRangeSelectionEnabled)) { Source = vm, Mode = BindingMode.TwoWay });
        root.Items.Add(range);
        root.Items.Add(new MenuItem { Header = "同步刪除選取區間…（Delete）", Command = vm.SynchronousAudioCutCommand });
        root.Items.Add(new MenuItem { Header = "移除選取標記並恢復波形…（Delete）", Command = vm.SynchronousAudioCancelSelectedRegionCommand });

        var sendAudition = new MenuItem { Header = "交由 AU 局部修音…", Command = vm.SynchronousAudioOpenInAuditionCommand };
        sendAudition.Bind(MenuItem.IsVisibleProperty,
            new Binding(nameof(vm.CanStartAuditionFromUi)) { Source = vm });
        root.Items.Add(sendAudition);

        var finishAudition = new MenuItem { Header = "● 完成 AU 修音，繼續聽校", Command = vm.SynchronousAudioReloadFromAuditionCommand };
        finishAudition.Bind(MenuItem.IsVisibleProperty,
            new Binding(nameof(vm.IsAuditionHandoffPending)) { Source = vm });
        root.Items.Add(finishAudition);

        var cancelAudition = new MenuItem { Header = "取消本次 AU 修音（保留副本）", Command = vm.SynchronousAudioCancelAuditionCommand };
        cancelAudition.Bind(MenuItem.IsVisibleProperty,
            new Binding(nameof(vm.IsAuditionHandoffPending)) { Source = vm });
        root.Items.Add(cancelAudition);

        root.Items.Add(new Separator());

        var review = new MenuItem { Header = "變更點巡聽" };
        var previousChange = new MenuItem { Header = "◀ 上一個變更點", Command = vm.SynchronousAudioPreviousChangeCommand };
        var nextChange = new MenuItem { Header = "下一個變更點 ▶", Command = vm.SynchronousAudioNextChangeCommand };
        previousChange.Bind(MenuItem.IsEnabledProperty,
            new Binding(nameof(vm.CanNavigateAudioChanges)) { Source = vm });
        nextChange.Bind(MenuItem.IsEnabledProperty,
            new Binding(nameof(vm.CanNavigateAudioChanges)) { Source = vm });
        review.Items.Add(previousChange);
        review.Items.Add(nextChange);
        review.Items.Add(new MenuItem { Header = "試聽最近剪接處", Command = vm.SynchronousAudioPreviewCommand });
        root.Items.Add(review);

        var compare = new MenuItem { Header = "顯示／隱藏原軌比對" };
        compare.Click += (_, _) => toggleComparison();
        root.Items.Add(compare);

        var original = new MenuItem { Header = "切到原時間軸聽校", Command = vm.SynchronousAudioOriginalTimelineCommand };
        original.Bind(MenuItem.IsVisibleProperty,
            new Binding(nameof(vm.CanPreviewOriginalTimeline)) { Source = vm });
        root.Items.Add(original);

        var edited = new MenuItem { Header = "← 返回修改後時間軸", Command = vm.SynchronousAudioEditedTimelineCommand };
        edited.Bind(MenuItem.IsVisibleProperty,
            new Binding(nameof(vm.CanReturnEditedTimeline)) { Source = vm });
        root.Items.Add(edited);

        root.Items.Add(new Separator());

        var verify = new MenuItem { Header = "驗證目前音檔…", Command = vm.SynchronousAudioVerifyCommand };
        var export = new MenuItem { Header = "輸出配對 WAV 與字幕…", Command = vm.SynchronousAudioExportCommand };
        verify.Bind(MenuItem.IsEnabledProperty,
            new Binding(nameof(vm.CanVerifyAudio)) { Source = vm });
        export.Bind(MenuItem.IsEnabledProperty,
            new Binding(nameof(vm.CanExportAudio)) { Source = vm });
        root.Items.Add(verify);
        root.Items.Add(export);

        root.Items.Add(new Separator());
        var balanceToggle = new MenuItem { Command = vm.ToggleReviewAudioBalanceCommand };
        balanceToggle.Bind(MenuItem.HeaderProperty,
            new Binding(nameof(vm.ReviewAudioBalanceMenuLabel)) { Source = vm });
        root.Items.Add(balanceToggle);
        var playbackSettings = new MenuItem { Header = "播放控制設定…" };
        playbackSettings.Click += (_, _) =>
        {
            var window = new PlaybackControlsWindow(vm);
            if (vm.Window is Window owner) _ = window.ShowDialog<bool>(owner);
            else window.Show();
        };
        root.Items.Add(playbackSettings);

        var subtitleText = new MenuItem { Header = "字幕文字" };
        subtitleText.Items.Add(new MenuItem
        {
            Header = "整理選取字幕標點…",
            Command = vm.NormalizeSelectedSubtitlePunctuationCommand,
        });
        subtitleText.Items.Add(new MenuItem
        {
            Header = "整理全部字幕標點…",
            Command = vm.NormalizeAllSubtitlePunctuationCommand,
        });
        root.Items.Add(subtitleText);

        var project = new MenuItem { Header = "專案與歷史" };
        project.Items.Add(new MenuItem { Header = "建立可逆音訊專案（編修前）", Command = vm.SynchronousAudioBeginProjectCommand });
        project.Items.Add(new MenuItem { Header = "儲存音訊／字幕工作階段", Command = vm.SynchronousAudioSaveCommand });
        project.Items.Add(new MenuItem { Header = "開啟音訊／字幕工作階段…", Command = vm.SynchronousAudioOpenCommand });
        project.Items.Add(new MenuItem { Header = "恢復歷史音訊／字幕版本…", Command = vm.SynchronousAudioOpenRevisionCommand });
        project.Items.Add(new MenuItem { Header = "清理舊專案…", Command = vm.SynchronousAudioCleanupCommand });
        root.Items.Add(project);

        var audition = new MenuItem { Header = "AU 設定與恢復" };
        audition.Items.Add(new MenuItem { Header = "接回先前 AU 修音（重啟後）…", Command = vm.SynchronousAudioRecoverAuditionCommand });
        audition.Items.Add(new MenuItem { Header = "設定 Audition.exe…", Command = vm.SynchronousAudioSetAuditionPathCommand });
        root.Items.Add(audition);

        return root;
    }
}
