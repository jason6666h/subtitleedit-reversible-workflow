using AudioWorkflow;
using Avalonia.Controls;
using Avalonia.Input;
using Nikse.SubtitleEdit.Features.Options.Shortcuts;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private AudioTrimShortcutSettings? _audioTrimShortcuts;
    internal AudioTrimShortcutSettingsStore? AudioTrimShortcutSettingsStoreOverride { get; set; }
    private AudioTrimShortcutSettingsStore AudioTrimShortcutSettingsStore =>
        AudioTrimShortcutSettingsStoreOverride ?? new();

    internal Action? AudioComparisonToggleAction { get; set; }

    internal AudioTrimShortcutSettings AudioTrimShortcuts
    {
        get
        {
            if (_audioTrimShortcuts != null)
                return _audioTrimShortcuts;

            try
            {
                _audioTrimShortcuts = AudioTrimShortcutSettingsStore.Load();
            }
            catch (Exception ex)
            {
                Se.LogError(ex, "Audio trim shortcut settings; original file was not overwritten");
                _audioTrimShortcuts = new AudioTrimShortcutSettings();
            }

            return _audioTrimShortcuts;
        }
        set => _audioTrimShortcuts = value;
    }

    internal AudioTrimShortcutSettings CloneAudioTrimShortcuts()
    {
        return new AudioTrimShortcutSettings
        {
            SchemaVersion = AudioTrimShortcuts.SchemaVersion,
            Bindings = AudioTrimShortcuts.Bindings
                .Select(b => new AudioTrimShortcutBinding(b.Id, [.. b.Keys]))
                .ToList(),
        };
    }

    internal bool TryValidateAudioTrimShortcuts(AudioTrimShortcutSettings settings, out string error)
    {
        try
        {
            settings.Validate();
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
            return false;
        }

        var native = ShortcutsMain.GetUsedShortcuts(this)
            .Where(s => s.Keys.Count > 0)
            .Select(s => ShortcutChord(s.Keys))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var playback = PlaybackBindings()
            .Where(b => b.Keys.Count > 0)
            .Select(b => ShortcutChord(b.Keys))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in settings.Bindings)
        {
            var keys = binding.Keys;
            if (keys.Count == 0)
                continue;

            if (keys.Count == 1 && keys[0].Length == 1 && char.IsLetterOrDigit(keys[0][0]))
            {
                error = "單一文字鍵會干擾字幕輸入，請加入 Ctrl／Shift 等修飾鍵，或清除為未設定。";
                return false;
            }

            var chord = ShortcutChord(keys);
            if (!owned.Add(chord))
            {
                error = $"音訊剪修快捷鍵 {DisplayShortcut(keys)} 重複。";
                return false;
            }

            // Delete / Escape are intentionally waveform-scoped and may coexist with SE's
            // ordinary editing behavior. Every other chord must remain globally collision-free.
            var scopedNativeKey = keys.Count == 1 &&
                (string.Equals(keys[0], "Delete", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(keys[0], "Escape", StringComparison.OrdinalIgnoreCase));

            if (!scopedNativeKey && native.Contains(chord))
            {
                error = $"快捷鍵 {DisplayShortcut(keys)} 已被 Subtitle Edit 使用。";
                return false;
            }

            if (playback.Contains(chord))
            {
                error = $"快捷鍵 {DisplayShortcut(keys)} 已被播放控制使用。";
                return false;
            }
        }

        error = "";
        return true;
    }

    internal bool TrySaveAudioTrimShortcuts(AudioTrimShortcutSettings settings, out string error)
    {
        if (!TryValidateAudioTrimShortcuts(settings, out error))
            return false;

        try
        {
            AudioTrimShortcutSettingsStore.Save(settings, repairInvalidExisting: true);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        AudioTrimShortcuts = settings;
        ShowStatus("音訊剪修快捷鍵已更新；未設定的動作不會攔截鍵盤。");
        return true;
    }

    private static string ShortcutChord(IEnumerable<string> keys) => string.Join("+",
        keys.Select(ShortcutManager.NormalizeKeyToken)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

    internal static string DisplayShortcut(IEnumerable<string> keys)
    {
        var list = keys.ToList();
        return list.Count == 0
            ? "未設定"
            : string.Join("+", list.Select(k =>
                k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1]) ? k[1].ToString() : k));
    }

    private static List<string> KeysFromEvent(KeyEventArgs e)
    {
        var keys = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) keys.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) keys.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) keys.Add("Alt");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) keys.Add("Win");
        keys.Add(ShortcutManager.GetShortcutKeyName(e));
        return keys;
    }

    private bool IsAudioTrimTextEditingFocus()
    {
        var focused = Window?.FocusManager?.GetFocusedElement();
        return focused is TextBox ||
               ReferenceEquals(focused, EditTextBox?.TextControl) ||
               ReferenceEquals(focused, EditTextBoxOriginal?.TextControl);
    }

    private bool IsWaveformShortcutFocus()
    {
        var focused = Window?.FocusManager?.GetFocusedElement();
        return ReferenceEquals(focused, AudioVisualizer) ||
               focused is Layout.OriginalAxisTrack { AllowSelection: true };
    }

    internal bool TryHandleSynchronousAudioShortcut(KeyEventArgs e)
    {
        if (e.Handled || AudioVisualizer == null || IsAudioTrimTextEditingFocus())
            return false;

        var chord = ShortcutChord(KeysFromEvent(e));
        var binding = AudioTrimShortcuts.Bindings.FirstOrDefault(b =>
            b.Keys.Count > 0 &&
            string.Equals(ShortcutChord(b.Keys), chord, StringComparison.OrdinalIgnoreCase));
        if (binding == null)
            return false;

        if (binding.Id is "delete-selection" or "clear-selection")
        {
            if (!IsWaveformShortcutFocus())
                return false;

            if (_selectedAudioEditRegion == null && AudioVisualizer.IsAudioRangeSelectionMode != true)
                return false;
        }

        e.Handled = true;
        _shortcutManager.ClearKeys();

        switch (binding.Id)
        {
            case "toggle-trim":
                if (!_synchronousAudioBusy)
                    SynchronousAudioToggleRangeSelectionCommand.Execute(null);
                break;

            case "delete-selection":
                if (_selectedAudioEditRegion != null)
                {
                    if (!_synchronousAudioBusy && SynchronousAudioCancelSelectedRegionCommand.CanExecute(null))
                        SynchronousAudioCancelSelectedRegionCommand.Execute(null);
                    break;
                }

                var range = GetSynchronousAudioSelection();
                if (!_synchronousAudioBusy && range != null && range.EndTime > range.StartTime &&
                    SynchronousAudioCutCommand.CanExecute(null))
                    SynchronousAudioCutCommand.Execute(null);
                break;

            case "clear-selection":
                ClearSynchronousAudioSelection();
                ShowStatus("已清除音訊選取；剪修模式維持不變。");
                break;

            case "preview-cut":
                if (SynchronousAudioPreviewCommand.CanExecute(null))
                    SynchronousAudioPreviewCommand.Execute(null);
                break;

            case "previous-change":
                if (SynchronousAudioPreviousChangeCommand.CanExecute(null))
                    SynchronousAudioPreviousChangeCommand.Execute(null);
                break;

            case "next-change":
                if (SynchronousAudioNextChangeCommand.CanExecute(null))
                    SynchronousAudioNextChangeCommand.Execute(null);
                break;

            case "toggle-comparison":
                AudioComparisonToggleAction?.Invoke();
                break;

            case "audition":
                if (IsAuditionHandoffPending)
                {
                    if (SynchronousAudioReloadFromAuditionCommand.CanExecute(null))
                        SynchronousAudioReloadFromAuditionCommand.Execute(null);
                }
                else if (SynchronousAudioOpenInAuditionCommand.CanExecute(null))
                {
                    SynchronousAudioOpenInAuditionCommand.Execute(null);
                }
                break;

            case "toggle-timeline":
                if (CanReturnEditedTimeline && SynchronousAudioEditedTimelineCommand.CanExecute(null))
                    SynchronousAudioEditedTimelineCommand.Execute(null);
                else if (CanPreviewOriginalTimeline && SynchronousAudioOriginalTimelineCommand.CanExecute(null))
                    SynchronousAudioOriginalTimelineCommand.Execute(null);
                break;
        }

        return true;
    }
}
