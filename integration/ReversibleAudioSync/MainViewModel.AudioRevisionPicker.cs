using AudioWorkflow;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private sealed record AudioRevisionChoice(string Path, string Display)
    {
        public override string ToString() => Display;
    }

    private async Task<string?> PickAudioRevisionAsync()
    {
        if (Window == null || _synchronousAudioState == null || string.IsNullOrEmpty(_synchronousAudioDirectory))
            return null;

        var root = RevisionRoot(_synchronousAudioState, _synchronousAudioDirectory);
        if (!Directory.Exists(root))
        {
            ShowStatus("目前工作階段尚未建立可恢復的歷史版本。");
            return null;
        }

        var latestRevisionDirectory = _lastRevisionDirectory;
        var loaded = await Task.Run(() =>
        {
            var choices = new List<AudioRevisionChoice>();
            var skipped = 0;
            foreach (var directory in Directory.EnumerateDirectories(root)
                         .Where(p => !Path.GetFileName(p).StartsWith(".pending-", StringComparison.Ordinal)))
            {
                try
                {
                    var revision = AudioRevisionStore.ReadManifest(directory);
                    var kind = revision.Kind switch
                    {
                        "original" => "原始版本",
                        "before" => "操作前",
                        "edit" => "編修版本",
                        _ => revision.Kind,
                    };
                    var latest = latestRevisionDirectory != null &&
                        string.Equals(Path.GetFullPath(directory), Path.GetFullPath(latestRevisionDirectory),
                            StringComparison.OrdinalIgnoreCase) ? "  ★目前保存點" : "";
                    choices.Add(new AudioRevisionChoice(directory,
                        $"{revision.CreatedUtc.ToLocalTime():yyyy/MM/dd HH:mm:ss}　{kind}{latest}"));
                }
                catch
                {
                    skipped++;
                }
            }
            return (Choices: choices.OrderByDescending(p => p.Display, StringComparer.Ordinal).ToArray(), Skipped: skipped);
        });

        if (loaded.Choices.Length == 0)
        {
            ShowStatus(loaded.Skipped == 0
                ? "目前沒有可恢復的歷史版本。"
                : "歷史版本清單無可安全讀取的項目；請保留工作檔並檢查版本資料。");
            return null;
        }

        var list = new ListBox
        {
            ItemsSource = loaded.Choices,
            SelectionMode = SelectionMode.Single,
            MinHeight = 260,
        };
        list.SelectedIndex = 0;

        var status = new TextBlock
        {
            Text = loaded.Skipped == 0
                ? "選擇要恢復的配對音訊／字幕版本。開啟時仍會做完整雜湊驗證。"
                : $"選擇要恢復的版本。已略過 {loaded.Skipped} 個無法讀取的版本清單；選取版本仍會做完整雜湊驗證。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        var restore = new Button { Content = "恢復選取版本", MinHeight = 36, MinWidth = 140 };
        var cancel = new Button { Content = "取消", MinHeight = 36, MinWidth = 90 };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { restore, cancel },
        };
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(16),
        };
        content.Children.Add(status);
        Grid.SetRow(list, 1);
        content.Children.Add(list);
        Grid.SetRow(actions, 2);
        content.Children.Add(actions);

        var dialog = new Window
        {
            Title = "恢復歷史音訊／字幕版本",
            Width = 720,
            Height = 460,
            MinWidth = 560,
            MinHeight = 340,
            Content = content,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        string? selected = null;
        restore.IsEnabled = list.SelectedItem != null;
        list.SelectionChanged += (_, _) => restore.IsEnabled = list.SelectedItem != null;
        restore.Click += (_, _) =>
        {
            if (list.SelectedItem is not AudioRevisionChoice choice) return;
            selected = choice.Path;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(Window);
        return selected;
    }
}
