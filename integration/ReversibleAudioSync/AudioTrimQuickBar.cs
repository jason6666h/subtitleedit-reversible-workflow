using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Nikse.SubtitleEdit.Features.Main.Layout;

internal static class AudioTrimQuickBar
{
    internal static Control Create(
        MainViewModel vm,
        Func<Flyout> createMoreFlyout)
    {
        var actions = new StackPanel
        {
            Name = "AudioTrimQuickBar",
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            [AutomationProperties.NameProperty] = "音訊剪修快捷列",
        };

        actions.Children.Add(new Border
        {
            Width = 1,
            Height = 22,
            Opacity = 0.25,
            Margin = new Thickness(5, 0, 3, 0),
            Background = Avalonia.Media.Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var range = new ToggleButton
        {
            Name = "AudioQuickRangeToggle",
            Content = "剪修",
            MinWidth = 56,
            MinHeight = 30,
            Padding = new Thickness(8, 3),
            [AutomationProperties.NameProperty] = "音訊剪修模式",
            [ToolTip.TipProperty] = "開啟後可直接在波形拖曳範圍；剪完後保持開啟，可連續處理下一段。",
        };
        range.Bind(ToggleButton.IsCheckedProperty,
            new Binding(nameof(vm.IsSynchronousAudioRangeSelectionEnabled))
            {
                Source = vm,
                Mode = BindingMode.TwoWay,
            });
        range.Click += (_, _) => RestoreWaveformFocus(vm);
        actions.Children.Add(range);

        var cut = QuickButton(
            vm,
            "AudioQuickCutButton",
            "刪除",
            "同步刪除目前音訊選取與字幕時間",
            vm.SynchronousAudioCutCommand,
            52);
        cut.Classes.Add("accent");
        cut.Bind(Button.IsEnabledProperty,
            new Binding(nameof(vm.IsSynchronousAudioRangeSelectionEnabled)) { Source = vm });
        actions.Children.Add(cut);

        var preview = QuickButton(
            vm,
            "AudioQuickPreviewButton",
            "試聽",
            "試聽最近剪接處",
            vm.SynchronousAudioPreviewCommand,
            52);
        preview.Bind(Button.IsEnabledProperty,
            new Binding(nameof(vm.CanNavigateAudioChanges)) { Source = vm });
        actions.Children.Add(preview);

        var more = QuickButton(
            vm,
            "AudioQuickMoreButton",
            "⋯",
            "更多音訊工具、Audition、比對、專案、輸出與快捷鍵",
            null,
            34);
        var moreFlyout = createMoreFlyout();
        moreFlyout.Closed += (_, _) => RestoreWaveformFocus(vm);
        more.Flyout = moreFlyout;
        actions.Children.Add(more);

        return actions;
    }

    private static Button QuickButton(
        MainViewModel vm,
        string name,
        string text,
        string tooltip,
        System.Windows.Input.ICommand? command,
        double minWidth)
    {
        var button = new Button
        {
            Name = name,
            Content = text,
            Command = command,
            MinWidth = minWidth,
            MinHeight = 30,
            Padding = new Thickness(7, 3),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            [AutomationProperties.NameProperty] = text,
            [ToolTip.TipProperty] = tooltip,
        };

        if (command != null)
            button.Click += (_, _) => RestoreWaveformFocus(vm);

        return button;
    }

    private static void RestoreWaveformFocus(MainViewModel vm)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (vm.AudioVisualizer?.IsEffectivelyVisible == true)
                vm.AudioVisualizer.Focus();
        }, DispatcherPriority.Background);
    }
}
