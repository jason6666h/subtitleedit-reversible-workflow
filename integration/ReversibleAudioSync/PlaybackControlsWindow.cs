using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AudioWorkflow;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Logic;

namespace Nikse.SubtitleEdit.Features.Main.Layout;

internal sealed class PlaybackControlsWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly PlaybackControlSettings _settings;
    private readonly TextBox[] _values = new TextBox[4];
    private readonly CheckBox _balanceEnabled = new() { Content = "聽校時平衡大小聲" };
    private readonly CheckBox _focusGridAfterSplit = new()
    {
        Name = "FocusSubtitleGridAfterTextBoxSplit",
        Content = "從字幕文字框分割後，自動將焦點移至字幕列表（Space 可直接播放／暫停）",
    };
    private readonly ComboBox _strength = new() { Width = 100 };
    private readonly TextBlock _error = new() { Foreground = Avalonia.Media.Brushes.OrangeRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    public PlaybackControlsWindow(MainViewModel vm)
    {
        _vm = vm;
        _settings = vm.ClonePlaybackControls();
        Title = "播放控制設定";
        Width = 650;
        Height = 535;
        MinWidth = 580;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var rows = new StackPanel { Spacing = 9, Margin = new Thickness(16) };
        rows.Children.Add(new TextBlock { Text = "快捷鍵只在 Subtitle Edit 生效；點選鍵位欄後按下組合鍵。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        rows.Children.Add(new TextBlock { Text = "播放預設", FontWeight = Avalonia.Media.FontWeight.Bold });
        AddPreset(rows, 0, "速度 1", "倍", "PlaybackSpeedOneValue");
        AddPreset(rows, 1, "速度 2", "倍", "PlaybackSpeedTwoValue");
        AddPreset(rows, 2, "音量 1", "%", "PlaybackVolumeOneValue");
        AddPreset(rows, 3, "音量 2", "%", "PlaybackVolumeTwoValue");

        rows.Children.Add(new Separator());
        var balanceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _balanceEnabled.IsChecked = _settings.BalanceEnabled;
        balanceRow.Children.Add(_balanceEnabled);
        balanceRow.Children.Add(new TextBlock { Text = "強度", VerticalAlignment = VerticalAlignment.Center });
        foreach (var item in new[] { ("輕", "soft"), ("中", "medium"), ("強", "strong") })
            _strength.Items.Add(new ComboBoxItem { Content = item.Item1, Tag = item.Item2 });
        _strength.SelectedItem = _strength.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, _settings.BalanceStrength));
        balanceRow.Children.Add(_strength);
        rows.Children.Add(balanceRow);
        var balanceKeys = MakeKeyCapture(_settings.BalanceKeys);
        rows.Children.Add(MakeKeyRow("平衡開關快捷鍵", balanceKeys, _settings.BalanceKeys));
        rows.Children.Add(new TextBlock { Text = "此效果只改變播放聲音；音檔、波形與輸出內容不變。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });

        rows.Children.Add(new Separator());
        rows.Children.Add(new TextBlock { Text = "字幕分割操作", FontWeight = Avalonia.Media.FontWeight.Bold });
        _focusGridAfterSplit.IsChecked = _settings.FocusSubtitleGridAfterTextBoxSplit;
        rows.Children.Add(_focusGridAfterSplit);
        rows.Children.Add(new TextBlock
        {
            Text = "開啟後，只有在字幕文字框內執行「游標位置分割」或「影片位置＋游標位置分割」且分割成功時，焦點才會移回字幕列表；其他編輯行為不變。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        rows.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "取消", MinWidth = 80 };
        cancel.Click += (_, _) => Close(false);
        var save = new Button { Content = "儲存", MinWidth = 80 };
        save.Click += (_, _) => { if (TrySave(out _)) Close(true); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        rows.Children.Add(buttons);
        Content = new ScrollViewer { Content = rows };
    }

    private void AddPreset(StackPanel rows, int slot, string label, string unit, string name)
    {
        var item = _settings.Presets[slot];
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        row.Children.Add(new TextBlock { Text = label, Width = 100, VerticalAlignment = VerticalAlignment.Center });
        var value = new TextBox
        {
            Name = name,
            Width = 80,
            Text = item.Value.ToString("0.##", CultureInfo.InvariantCulture),
        };
        _values[slot] = value;
        row.Children.Add(value);
        row.Children.Add(new TextBlock { Text = unit, Width = 22, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(MakeKeyRow("快捷鍵", MakeKeyCapture(item.Keys), item.Keys));
        rows.Children.Add(row);
    }

    private static StackPanel MakeKeyRow(string label, TextBox capture, List<string> keys)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(capture);
        var clear = new Button { Content = "清除" };
        clear.Click += (_, _) => { keys.Clear(); capture.Text = "未設定"; };
        row.Children.Add(clear);
        return row;
    }

    private static TextBox MakeKeyCapture(List<string> keys)
    {
        var box = new TextBox { Width = 165, IsReadOnly = true, Text = DisplayKeys(keys), PlaceholderText = "點選後按鍵" };
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
                return;
            var next = new List<string>();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) next.Add("Ctrl");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) next.Add("Shift");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) next.Add("Alt");
            if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) next.Add("Win");
            next.Add(ShortcutManager.GetShortcutKeyName(e));
            keys.Clear();
            keys.AddRange(next);
            box.Text = DisplayKeys(keys);
            e.Handled = true;
        };
        return box;
    }

    private static string DisplayKeys(List<string> keys) => keys.Count == 0 ? "未設定" :
        string.Join("+", keys.Select(k => k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1]) ? k[1].ToString() : k));

    internal bool TrySave(out string error)
    {
        for (var i = 0; i < _values.Length; i++)
        {
            var text = _values[i].Text;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) &&
                !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                error = "請輸入有效的速度或音量數字。";
                _error.Text = error;
                return false;
            }
            _settings.Presets[i].Value = parsed;
        }
        _settings.BalanceEnabled = _balanceEnabled.IsChecked == true;
        _settings.BalanceStrength = (string)((ComboBoxItem)_strength.SelectedItem!).Tag!;
        _settings.FocusSubtitleGridAfterTextBoxSplit = _focusGridAfterSplit.IsChecked == true;
        if (!_vm.TrySavePlaybackControls(_settings, out error))
        {
            _error.Text = error;
            return false;
        }
        _error.Text = "";
        return true;
    }
}
