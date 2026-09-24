using AudioWorkflow;
using CommunityToolkit.Mvvm.Input;
using Nikse.SubtitleEdit.Features.Options.Shortcuts;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.VideoPlayers.LibMpvDynamic;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private PlaybackControlSettings? _playbackControls;
    internal PlaybackControlSettingsStore? PlaybackSettingsStoreOverride { get; set; }
    private PlaybackControlSettingsStore PlaybackSettingsStore => PlaybackSettingsStoreOverride ?? new();
    internal PlaybackControlSettings PlaybackControls
    {
        get
        {
            if (_playbackControls != null) return _playbackControls;
            try { _playbackControls = PlaybackSettingsStore.Load(); }
            catch (Exception ex)
            {
                Se.LogError(ex, "Playback controls settings; original file was not overwritten");
                _playbackControls = new PlaybackControlSettings();
            }
            return _playbackControls;
        }
        set => _playbackControls = value;
    }

    public string ReviewAudioBalanceMenuLabel => PlaybackControls.BalanceEnabled
        ? "聽校大小聲平衡：開" : "聽校大小聲平衡：關";

    private enum ReviewBalanceApplyResult { Disabled, Applied, WaitingForPlayer, Unsupported, Failed }

    private static string Chord(IEnumerable<string> keys) => string.Join("+",
        keys.Select(ShortcutManager.NormalizeKeyToken).OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

    internal bool TryValidatePlaybackControls(PlaybackControlSettings settings, out string error)
    {
        try { settings.Validate(); }
        catch (InvalidDataException ex) { error = ex.Message; return false; }

        var native = ShortcutsMain.GetUsedShortcuts(this)
            .Where(s => s.Keys.Count > 0)
            .Select(s => Chord(s.Keys))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keys in settings.Presets.Select(p => p.Keys).Append(settings.BalanceKeys))
        {
            if (keys.Count == 0) continue;
            if (keys.Count == 1 && keys[0].Length == 1 && char.IsLetterOrDigit(keys[0][0]))
            {
                error = "單一文字鍵會干擾字幕輸入，請加入 Ctrl／Shift 等修飾鍵。";
                return false;
            }
            var chord = Chord(keys);
            if (!owned.Add(chord))
            {
                error = $"播放控制快捷鍵 {string.Join("+", keys)} 重複。";
                return false;
            }
            if (native.Contains(chord))
            {
                error = $"快捷鍵 {string.Join("+", keys)} 已被 Subtitle Edit 使用。";
                return false;
            }
        }
        error = "";
        return true;
    }

    internal bool TrySavePlaybackControls(PlaybackControlSettings settings, out string error)
    {
        if (!TryValidatePlaybackControls(settings, out error)) return false;
        try { PlaybackSettingsStore.Save(settings, repairInvalidExisting: true); }
        catch (Exception ex) { error = ex.Message; return false; }
        PlaybackControls = settings;
        OnPropertyChanged(nameof(ReviewAudioBalanceMenuLabel));
        ReloadShortcuts();
        var balanceResult = ApplyReviewAudioBalance();
        ShowStatus(balanceResult switch
        {
            ReviewBalanceApplyResult.Disabled => "聽校大小聲平衡已關閉。",
            ReviewBalanceApplyResult.Applied => "聽校大小聲平衡已開啟；僅影響播放。",
            ReviewBalanceApplyResult.WaitingForPlayer => "聽校大小聲平衡已儲存；載入音訊後會啟用。",
            ReviewBalanceApplyResult.Unsupported => "聽校大小聲平衡已儲存，但目前播放器不支援；請使用 mpv。",
            _ => "聽校大小聲平衡已儲存，但濾鏡套用失敗；請檢查播放器。",
        });
        return true;
    }

    private IEnumerable<(string Name, List<string> Keys, IRelayCommand Command)> PlaybackBindings()
    {
        var p = PlaybackControls.Presets;
        yield return (nameof(PlaybackSpeedOneCommand), p[0].Keys, PlaybackSpeedOneCommand);
        yield return (nameof(PlaybackSpeedTwoCommand), p[1].Keys, PlaybackSpeedTwoCommand);
        yield return (nameof(PlaybackVolumeOneCommand), p[2].Keys, PlaybackVolumeOneCommand);
        yield return (nameof(PlaybackVolumeTwoCommand), p[3].Keys, PlaybackVolumeTwoCommand);
        yield return (nameof(ToggleReviewAudioBalanceCommand), PlaybackControls.BalanceKeys, ToggleReviewAudioBalanceCommand);
    }

    private IEnumerable<(string Name, List<string> Keys, IRelayCommand Command)> SafePlaybackBindings()
    {
        var native = ShortcutsMain.GetUsedShortcuts(this)
            .Where(s => s.Keys.Count > 0).Select(s => Chord(s.Keys))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in PlaybackBindings())
        {
            if (binding.Keys.Count == 0) continue;
            if (binding.Keys.Count == 1 && binding.Keys[0].Length == 1 && char.IsLetterOrDigit(binding.Keys[0][0])) continue;
            var chord = Chord(binding.Keys);
            if (!native.Contains(chord) && accepted.Add(chord)) yield return binding;
        }
    }

    private void RegisterPlaybackShortcuts()
    {
        foreach (var (name, keys, command) in SafePlaybackBindings())
            _shortcutManager.RegisterShortcut(new ShortCut(name, [.. keys], ShortcutCategory.General, command));
    }

    private void AddPlaybackFullScreenBindings(List<(string name, List<string> keys, IRelayCommand command)> bindings)
    {
        foreach (var (name, keys, command) in SafePlaybackBindings())
            bindings.Add((name, [.. keys], command));
    }

    private void ApplyPlaybackSpeed(int slot)
    {
        var control = GetVideoPlayerControl();
        if (control == null) return;
        var value = PlaybackControls.Presets[slot].Value;
        var display = value.ToString("0.##", CultureInfo.InvariantCulture) + "x";
        if (!Speeds.Contains(display))
        {
            var insert = Speeds.ToList().FindIndex(s =>
                double.TryParse(s.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var current) && current > value);
            if (insert < 0) Speeds.Add(display);
            else Speeds.Insert(insert, display);
        }
        SelectedSpeed = display;
        control.SetSpeed(value);
        ShowStatus($"播放速度 {display}");
    }

    private void ApplyPlaybackVolume(int slot)
    {
        var control = GetVideoPlayerControl();
        if (control == null) return;
        control.Volume = PlaybackControls.Presets[slot].Value;
        Se.Settings.Video.Volume = control.Volume;
        ShowStatus($"播放音量 {control.Volume:0}%");
    }

    [RelayCommand] private void PlaybackSpeedOne() => ApplyPlaybackSpeed(0);
    [RelayCommand] private void PlaybackSpeedTwo() => ApplyPlaybackSpeed(1);
    [RelayCommand] private void PlaybackVolumeOne() => ApplyPlaybackVolume(2);
    [RelayCommand] private void PlaybackVolumeTwo() => ApplyPlaybackVolume(3);

    [RelayCommand]
    private void ToggleReviewAudioBalance()
    {
        var copy = ClonePlaybackControls();
        copy.BalanceEnabled = !copy.BalanceEnabled;
        if (!TrySavePlaybackControls(copy, out var error)) { ShowStatus(error); return; }
    }

    internal PlaybackControlSettings ClonePlaybackControls() => new()
    {
        Presets = PlaybackControls.Presets.Select(p => new PlaybackShortcutPreset(p.Id, p.Value, [.. p.Keys])).ToList(),
        BalanceKeys = [.. PlaybackControls.BalanceKeys],
        BalanceEnabled = PlaybackControls.BalanceEnabled,
        BalanceStrength = PlaybackControls.BalanceStrength,
        FocusSubtitleGridAfterTextBoxSplit = PlaybackControls.FocusSubtitleGridAfterTextBoxSplit,
    };

    private enum TextBoxSplitFocusTarget
    {
        None,
        TranslationText,
        OriginalText,
        SubtitleGrid,
    }

    private TextBoxSplitFocusTarget GetTextBoxSplitFocusTarget()
    {
        if (EditTextBoxOriginal.IsFocused)
        {
            return PlaybackControls.FocusSubtitleGridAfterTextBoxSplit
                ? TextBoxSplitFocusTarget.SubtitleGrid
                : TextBoxSplitFocusTarget.OriginalText;
        }

        if (EditTextBox.IsFocused)
        {
            return PlaybackControls.FocusSubtitleGridAfterTextBoxSplit
                ? TextBoxSplitFocusTarget.SubtitleGrid
                : TextBoxSplitFocusTarget.TranslationText;
        }

        return TextBoxSplitFocusTarget.None;
    }

    private void RestoreTextBoxSplitFocusAfterSuccessfulSplit(TextBoxSplitFocusTarget target, int subtitleCountBefore)
    {
        if (target == TextBoxSplitFocusTarget.None || Subtitles.Count <= subtitleCountBefore)
            return;

        void ApplyFocus()
        {
            switch (target)
            {
                case TextBoxSplitFocusTarget.SubtitleGrid:
                    if (SubtitleGrid != null)
                        TableViewExtras.FocusRow(SubtitleGrid);
                    break;
                case TextBoxSplitFocusTarget.OriginalText:
                    EditTextBoxOriginal.Focus();
                    break;
                case TextBoxSplitFocusTarget.TranslationText:
                    EditTextBox.Focus();
                    break;
            }
        }

        // Split/renumber/reveal can queue layout work that changes focus afterwards.
        // Apply once immediately for responsive keyboard use and once after queued UI work settles.
        ApplyFocus();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ApplyFocus();
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyFocus, Avalonia.Threading.DispatcherPriority.ApplicationIdle);
        }, Avalonia.Threading.DispatcherPriority.ApplicationIdle);
    }

    private ReviewBalanceApplyResult ApplyReviewAudioBalance()
    {
        var control = GetVideoPlayerControl();
        if (control?.VideoPlayer is LibMpvDynamicPlayer mpv)
        {
            if (!mpv.SetReviewAudioBalance(PlaybackControls.BalanceEnabled, PlaybackControls.BalanceStrength))
                return ReviewBalanceApplyResult.Failed;
            return PlaybackControls.BalanceEnabled ? ReviewBalanceApplyResult.Applied : ReviewBalanceApplyResult.Disabled;
        }
        if (!PlaybackControls.BalanceEnabled) return ReviewBalanceApplyResult.Disabled;
        return control == null ? ReviewBalanceApplyResult.WaitingForPlayer : ReviewBalanceApplyResult.Unsupported;
    }

    internal void ReapplyPersonalPlaybackControls() => ReapplyPlaybackSpeed();
}
