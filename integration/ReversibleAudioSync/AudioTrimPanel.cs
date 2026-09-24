using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nikse.SubtitleEdit.Features.Main.Layout;

internal static class AudioTrimPanel
{
    private const double PanelWidth = 820;

    internal static Control Create(MainViewModel vm, Action toggleComparison)
    {
        var root = new Grid
        {
            Width = PanelWidth,
            RowDefinitions = new RowDefinitions("Auto,10,Auto"),
        };

        var header = CreateHeader(vm);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,16,*"),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetRow(body, 2);
        root.Children.Add(body);

        var left = new StackPanel { Spacing = 12 };
        left.Children.Add(CreateAuditionSection(vm));
        left.Children.Add(CreateReviewSection(vm, toggleComparison));
        left.Children.Add(CreateOutputSection(vm));
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        var divider = new Border
        {
            Width = 1,
            Background = Brushes.Gray,
            Opacity = 0.22,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(divider, 1);
        body.Children.Add(divider);

        var right = new StackPanel { Spacing = 12 };
        right.Children.Add(CreateProjectSection(vm));
        right.Children.Add(CreateSettingsSection(vm));
        Grid.SetColumn(right, 2);
        body.Children.Add(right);

        return new Border
        {
            Name = "AudioTrimPanel",
            Padding = new Thickness(16, 14),
            Child = root,
            [AutomationProperties.NameProperty] = "音訊剪修工具",
        };
    }

    private static Control CreateHeader(MainViewModel vm)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,16,*"),
        };

        var title = new TextBlock
        {
            Text = "音訊剪修工具",
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var status = new TextBlock
        {
            Opacity = 0.72,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        status.Bind(TextBlock.TextProperty,
            new Binding(nameof(vm.SynchronousAudioProjectStatus)) { Source = vm });
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);

        return grid;
    }

    private static Control CreateAuditionSection(MainViewModel vm)
    {
        var body = NewSection("Audition");

        var send = NewButton("AudioAuditionStartButton", "送到 Audition", vm.SynchronousAudioOpenInAuditionCommand);
        send.Bind(Button.IsVisibleProperty,
            new Binding(nameof(vm.CanStartAuditionFromUi)) { Source = vm });

        var finish = NewButton("AudioAuditionFinishButton", "接回 AU 修音", vm.SynchronousAudioReloadFromAuditionCommand);
        finish.Classes.Add("accent");
        finish.Bind(Button.IsVisibleProperty,
            new Binding(nameof(vm.IsAuditionHandoffPending)) { Source = vm });

        var cancel = NewButton("AudioAuditionCancelButton", "取消 AU", vm.SynchronousAudioCancelAuditionCommand);
        cancel.Bind(Button.IsVisibleProperty,
            new Binding(nameof(vm.IsAuditionHandoffPending)) { Source = vm });

        body.Children.Add(send);
        var pending = TwoColumns(finish, cancel);
        pending.Bind(Control.IsVisibleProperty,
            new Binding(nameof(vm.IsAuditionHandoffPending)) { Source = vm });
        body.Children.Add(pending);
        return body;
    }

    private static Control CreateReviewSection(MainViewModel vm, Action toggleComparison)
    {
        var body = NewSection("巡聽與比對");

        var previous = NewButton("AudioPreviousChangeButton", "◀ 上一變更", vm.SynchronousAudioPreviousChangeCommand);
        var next = NewButton("AudioNextChangeButton", "下一變更 ▶", vm.SynchronousAudioNextChangeCommand);
        previous.Bind(Button.IsEnabledProperty,
            new Binding(nameof(vm.CanNavigateAudioChanges)) { Source = vm });
        next.Bind(Button.IsEnabledProperty,
            new Binding(nameof(vm.CanNavigateAudioChanges)) { Source = vm });
        body.Children.Add(TwoColumns(previous, next));

        var compare = NewButton("AudioToggleComparisonButton", "開／關雙軌比對");
        compare.Click += (_, _) => toggleComparison();

        var original = NewButton("AudioOriginalTimelineButton", "聽原時間軸", vm.SynchronousAudioOriginalTimelineCommand);
        original.Bind(Button.IsVisibleProperty,
            new Binding(nameof(vm.CanPreviewOriginalTimeline)) { Source = vm });
        body.Children.Add(TwoColumns(compare, original));

        var edited = NewButton("AudioEditedTimelineButton", "返回修改後時間軸", vm.SynchronousAudioEditedTimelineCommand);
        edited.Bind(Button.IsVisibleProperty,
            new Binding(nameof(vm.CanReturnEditedTimeline)) { Source = vm });
        body.Children.Add(edited);
        return body;
    }

    private static Control CreateProjectSection(MainViewModel vm)
    {
        var body = NewSection("專案與版本");

        body.Children.Add(TwoColumns(
            NewButton("AudioProjectBeginButton", "建立專案", vm.SynchronousAudioBeginProjectCommand),
            NewButton("AudioProjectSaveButton", "儲存工作階段", vm.SynchronousAudioSaveCommand)));

        body.Children.Add(TwoColumns(
            NewButton("AudioProjectOpenButton", "開啟工作階段…", vm.SynchronousAudioOpenCommand),
            NewButton("AudioProjectRevisionButton", "歷史版本…", vm.SynchronousAudioOpenRevisionCommand)));

        body.Children.Add(NewButton("AudioProjectCleanupButton", "清理舊專案…", vm.SynchronousAudioCleanupCommand));
        return body;
    }

    private static Control CreateOutputSection(MainViewModel vm)
    {
        var body = NewSection("驗證與輸出");

        var verify = NewButton("AudioVerifyButton", "驗證目前音檔", vm.SynchronousAudioVerifyCommand);
        var export = NewButton("AudioExportButton", "輸出 WAV + 字幕…", vm.SynchronousAudioExportCommand);
        verify.Bind(Button.IsEnabledProperty,
            new Binding(nameof(vm.CanVerifyAudio)) { Source = vm });
        export.Bind(Button.IsEnabledProperty,
            new Binding(nameof(vm.CanExportAudio)) { Source = vm });
        body.Children.Add(TwoColumns(verify, export));
        return body;
    }

    private static Control CreateSettingsSection(MainViewModel vm)
    {
        var body = NewSection("設定與其他");

        var shortcuts = NewButton("AudioShortcutSettingsButton", "音訊剪修快捷鍵…");
        shortcuts.Classes.Add("accent");
        shortcuts.Click += (_, _) =>
        {
            var window = new AudioTrimShortcutsWindow(vm);
            if (vm.Window is Window owner) _ = window.ShowDialog<bool>(owner);
            else window.Show();
        };

        var playback = NewButton("AudioPlaybackSettingsButton", "播放控制設定…");
        playback.Click += (_, _) =>
        {
            var window = new PlaybackControlsWindow(vm);
            if (vm.Window is Window owner) _ = window.ShowDialog<bool>(owner);
            else window.Show();
        };
        body.Children.Add(TwoColumns(shortcuts, playback));

        var balance = NewButton("AudioBalanceToggleButton", "", vm.ToggleReviewAudioBalanceCommand);
        balance.Bind(Button.ContentProperty,
            new Binding(nameof(vm.ReviewAudioBalanceMenuLabel)) { Source = vm });
        var auditionPath = NewButton("AudioAuditionPathButton", "Audition 路徑…", vm.SynchronousAudioSetAuditionPathCommand);
        body.Children.Add(TwoColumns(balance, auditionPath));

        body.Children.Add(TwoColumns(
            NewButton("AudioAuditionRecoverButton", "接回先前 AU…", vm.SynchronousAudioRecoverAuditionCommand),
            NewButton("AudioNormalizeSelectedButton", "整理選取標點…", vm.NormalizeSelectedSubtitlePunctuationCommand)));

        body.Children.Add(NewButton(
            "AudioNormalizeAllButton",
            "整理全部字幕標點…",
            vm.NormalizeAllSubtitlePunctuationCommand));

        return body;
    }

    private static StackPanel NewSection(string title)
    {
        var section = new StackPanel { Spacing = 6 };
        section.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
        });
        return section;
    }

    private static Button NewButton(string name, string text, System.Windows.Input.ICommand? command = null)
    {
        return new Button
        {
            Name = name,
            Content = text,
            Command = command,
            MinHeight = 32,
            Padding = new Thickness(8, 4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            [AutomationProperties.NameProperty] = text,
        };
    }

    private static Grid TwoColumns(Control left, Control right)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,8,*"),
        };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }
}
