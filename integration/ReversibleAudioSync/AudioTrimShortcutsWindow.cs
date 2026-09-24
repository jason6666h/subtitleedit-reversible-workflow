using System;
using System.Collections.Generic;
using System.Linq;
using AudioWorkflow;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Logic;

namespace Nikse.SubtitleEdit.Features.Main.Layout;

internal sealed class AudioTrimShortcutsWindow : Window
{
    private readonly MainViewModel _vm;
    private AudioTrimShortcutSettings _settings;
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBlock _error = new()
    {
        Foreground = Avalonia.Media.Brushes.OrangeRed,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
    };

    private static readonly (string Id, string Label, string Hint)[] Actions =
    [
        ("toggle-trim", "切換剪修模式", "進入／離開波形區間選取"),
        ("delete-selection", "刪除目前選取", "預設 Delete；只在波形剪修情境攔截"),
        ("clear-selection", "清除選取", "預設 Esc；清除範圍但維持剪修模式"),
        ("preview-cut", "試聽最近剪接", "從最近剪接點前方開始播放"),
        ("previous-change", "上一個變更點", "跳到上一個音訊變更"),
        ("next-change", "下一個變更點", "跳到下一個音訊變更"),
        ("toggle-comparison", "開／關雙軌比對", "顯示或隱藏原軌比較"),
        ("audition", "Audition 送出／接回", "依目前狀態自動送 AU 或接回"),
        ("toggle-timeline", "原軌／修改軌切換", "依目前時間軸自動切換"),
    ];

    public AudioTrimShortcutsWindow(MainViewModel vm)
    {
        _vm = vm;
        _settings = vm.CloneAudioTrimShortcuts();

        Title = "音訊剪修快捷鍵";
        Width = 620;
        Height = 650;
        MinWidth = 540;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Spacing = 10, Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = "點選快捷鍵欄後直接按下組合鍵；按「清除」即可停用該動作。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        root.Children.Add(new TextBlock
        {
            Text = "Delete／Esc 只在波形剪修情境生效；單一文字鍵不允許，避免干擾字幕輸入。",
            Opacity = 0.7,
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        root.Children.Add(_rows);
        BuildRows();

        root.Children.Add(_error);

        var utility = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var clearAll = new Button { Content = "全部清除" };
        clearAll.Click += (_, _) =>
        {
            foreach (var binding in _settings.Bindings) binding.Keys.Clear();
            BuildRows();
        };
        var defaults = new Button { Content = "恢復預設" };
        defaults.Click += (_, _) =>
        {
            _settings = new AudioTrimShortcutSettings();
            BuildRows();
        };
        utility.Children.Add(clearAll);
        utility.Children.Add(defaults);
        root.Children.Add(utility);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = "取消", MinWidth = 80 };
        cancel.Click += (_, _) => Close(false);
        var save = new Button { Content = "儲存", MinWidth = 80 };
        save.Classes.Add("accent");
        save.Click += (_, _) =>
        {
            if (_vm.TrySaveAudioTrimShortcuts(_settings, out var error))
                Close(true);
            else
                _error.Text = error;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        Content = new ScrollViewer { Content = root };
    }

    private void BuildRows()
    {
        _rows.Children.Clear();
        foreach (var action in Actions)
        {
            var binding = _settings.Get(action.Id);
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("170,*,70"),
                ColumnSpacing = 8,
            };

            var label = new StackPanel { Spacing = 2 };
            label.Children.Add(new TextBlock { Text = action.Label });
            label.Children.Add(new TextBlock
            {
                Text = action.Hint,
                Opacity = 0.6,
                FontSize = 11,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var capture = MakeKeyCapture(binding.Keys);
            Grid.SetColumn(capture, 1);
            grid.Children.Add(capture);

            var clear = new Button { Content = "清除", MinWidth = 60 };
            clear.Click += (_, _) =>
            {
                binding.Keys.Clear();
                capture.Text = "未設定";
            };
            Grid.SetColumn(clear, 2);
            grid.Children.Add(clear);
            _rows.Children.Add(grid);
        }
    }

    private static TextBox MakeKeyCapture(List<string> keys)
    {
        var box = new TextBox
        {
            IsReadOnly = true,
            Text = MainViewModel.DisplayShortcut(keys),
            PlaceholderText = "點選後按鍵",
            MinWidth = 170,
        };

        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
                Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
                return;

            var next = new List<string>();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) next.Add("Ctrl");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) next.Add("Shift");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) next.Add("Alt");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) next.Add("Win");
            next.Add(ShortcutManager.GetShortcutKeyName(e));

            keys.Clear();
            keys.AddRange(next);
            box.Text = MainViewModel.DisplayShortcut(keys);
            e.Handled = true;
        };

        return box;
    }
}
