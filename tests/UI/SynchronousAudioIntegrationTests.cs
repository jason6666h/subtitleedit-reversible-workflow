using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AudioWorkflow;
using Avalonia;
using Point = Avalonia.Point;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using Nikse.SubtitleEdit.Controls.AudioVisualizerControl;
using Nikse.SubtitleEdit.Controls.VideoPlayer;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Features.Main.Layout;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.Media;
using Nikse.SubtitleEdit.Logic.UndoRedo;
using Nikse.SubtitleEdit.Logic.VideoPlayers;
using Nikse.SubtitleEdit.Logic.VideoPlayers.LibMpvDynamic;
using SeMessageBox = Nikse.SubtitleEdit.Features.Shared.MessageBox;

namespace UITests.Logic;

/// <summary>
/// Real SE MainViewModel/command/hash/cutter contracts on the Avalonia test thread.
/// Headless owned windows exercise real cut confirmation and native undo/redo commands.
/// A fake media player/load hook injects cancellation and post-pop failures; native
/// decoding and audible playback are not covered by this suite.
/// </summary>
public class SynchronousAudioIntegrationTests
{
    [AvaloniaFact]
    public void ReviewAudioBalanceRejectsUninitializedPlayerAndUsesBoundedFilter()
    {
        using var player = new LibMpvDynamicPlayer();
        Assert.False(player.SetReviewAudioBalance(true, "medium"));
        var filter = LibMpvDynamicPlayer.ReviewAudioBalanceFilter("medium");
        Assert.Contains("acompressor=", filter);
        Assert.Contains("dynaudnorm=", filter);
        Assert.Contains("alimiter=", filter);
        Assert.Contains("maxgain=3.5", filter);
        Assert.Throws<ArgumentOutOfRangeException>(() => LibMpvDynamicPlayer.ReviewAudioBalanceFilter("extreme"));
    }

    [AvaloniaFact]
    public void PlaybackPresetsUseActivePlayerAndKeepCustomSpeedVisible()
    {
        using var host = new Host(); using var media = new MediaFixture(host);
        var vm = host.Vm;
        vm.PlaybackControls = new PlaybackControlSettings();
        vm.PlaybackControls.Presets[0].Value = 1.35;
        vm.PlaybackSpeedOneCommand.Execute(null);
        Assert.Equal(1.35, media.Player.Speed);
        Assert.Equal("1.35x", vm.SelectedSpeed);
        Assert.Contains("1.35x", vm.Speeds);
        Assert.True(vm.Speeds.IndexOf("1.2x") < vm.Speeds.IndexOf("1.35x"));
        Assert.True(vm.Speeds.IndexOf("1.35x") < vm.Speeds.IndexOf("1.4x"));
        vm.PlaybackVolumeTwoCommand.Execute(null);
        Assert.Equal(85, media.Player.Volume);
        Assert.Equal(85, vm.GetVideoPlayerControl()!.Volume);
        Assert.Equal(85, Se.Settings.Video.Volume);
    }

    [AvaloniaFact]
    public void PlaybackShortcutDispatchesThroughNativeKeyRouter()
    {
        using var host = new Host(); using var media = new MediaFixture(host);
        var vm = host.Vm;
        vm.PlaybackControls = new PlaybackControlSettings();
        media.Window.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, vm.OnKeyDownHandler,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        media.Window.AddHandler(Avalonia.Input.InputElement.KeyUpEvent, vm.OnKeyUpHandler,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        Assert.True(vm.AudioVisualizer!.Focus());
        media.Window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Digit1,
            Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Shift);
        Assert.Equal(1.2, media.Player.Speed);
    }

    [AvaloniaFact]
    public void PlaybackShortcutRebindPersistsAndRemovesOldChord()
    {
        using var host = new Host(); using var media = new MediaFixture(host);
        var vm = host.Vm;
        vm.PlaybackSettingsStoreOverride = new PlaybackControlSettingsStore(Path.Combine(media.Work, "preferences"));
        var settings = vm.ClonePlaybackControls();
        settings.Presets[0].Keys = ["Ctrl", "Shift", "F8"];
        settings.Presets[0].Value = 1.35;
        Assert.True(vm.TrySavePlaybackControls(settings, out var error), error);
        Assert.Equal(1.35, vm.PlaybackSettingsStoreOverride.Load().Presets[0].Value);
        media.Window.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, vm.OnKeyDownHandler,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        media.Window.AddHandler(Avalonia.Input.InputElement.KeyUpEvent, vm.OnKeyUpHandler,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        Assert.True(vm.AudioVisualizer!.Focus());
        media.Window.KeyPressQwerty(Avalonia.Input.PhysicalKey.Digit1,
            Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Shift);
        media.Window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.Digit1,
            Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Shift);
        Assert.Equal(1, media.Player.Speed);
        media.Window.KeyPressQwerty(Avalonia.Input.PhysicalKey.F8,
            Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Shift);
        Assert.Equal(1.35, media.Player.Speed);
    }

    [AvaloniaFact]
    public void PlaybackSettingsUiRepairsCorruptSettingsButPreservesNewerSchema()
    {
        using var host = new Host(); using var media = new MediaFixture(host);
        var vm = host.Vm;
        var store = new PlaybackControlSettingsStore(Path.Combine(media.Work, "repair-preferences"));
        vm.PlaybackSettingsStoreOverride = store;
        vm.PlaybackControls = new PlaybackControlSettings();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, "{ broken json");
        var settings = vm.ClonePlaybackControls();
        settings.Presets[0].Value = 1.4;
        Assert.True(vm.TrySavePlaybackControls(settings, out var error), error);
        Assert.Equal(1.4, store.Load().Presets[0].Value);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "playback-controls.json.corrupt-*.bak"));
        var future = File.ReadAllText(store.FilePath).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");
        File.WriteAllText(store.FilePath, future);
        Assert.False(vm.TrySavePlaybackControls(settings, out _));
        Assert.Equal(future, File.ReadAllText(store.FilePath));
    }

    [AvaloniaFact]
    public void PlaybackShortcutValidationRejectsNativeCollision()
    {
        using var host = new Host();
        var settings = new PlaybackControlSettings();
        settings.Presets[0].Keys = ["Ctrl", "S"];
        Assert.False(host.Vm.TryValidatePlaybackControls(settings, out var error));
        Assert.Contains("Ctrl", error);
    }

    [AvaloniaFact]
    public void PlaybackShortcutValidationRejectsEquivalentOwnedChords()
    {
        using var host = new Host();
        var settings = new PlaybackControlSettings();
        settings.Presets[1].Keys = ["Shift", "LeftCtrl", "D1"];
        Assert.False(host.Vm.TryValidatePlaybackControls(settings, out var error));
        Assert.Contains("重複", error);
    }

    [AvaloniaFact]
    public void PlaybackSettingsWindowShowsPresetsAndRejectsInvalidValue()
    {
        using var host = new Host();
        var window = new PlaybackControlsWindow(host.Vm);
        using var scope = new TestWindowScope(window);
        var speed = window.GetVisualDescendants().OfType<TextBox>()
            .Single(t => t.Name == "PlaybackSpeedOneValue");
        Assert.Equal("1.2", speed.Text);
        speed.Text = "9";
        Assert.False(window.TrySave(out var error));
        Assert.Contains("0.25", error);
        var flyout = Assert.IsType<Flyout>(ReversibleAudioSyncUi.CreateToolbarButton(host.Vm).Flyout);
        var panel = Assert.IsType<Border>(flyout.Content);
        Assert.DoesNotContain(panel.GetLogicalDescendants(), c => c is ScrollViewer);
        Assert.Contains(panel.GetLogicalDescendants().OfType<Button>(),
            b => b.Name == "AudioPlaybackSettingsButton" && Equals(b.Content, "播放控制設定…"));
    }

    [AvaloniaFact]
    public void PlaybackSettingsPersistTextBoxSplitFocusPreference()
    {
        var directory = Path.Combine(Path.GetTempPath(), "se-playback-focus-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PlaybackControlSettingsStore(directory);
            var settings = new PlaybackControlSettings();
            Assert.True(settings.FocusSubtitleGridAfterTextBoxSplit);
            settings.FocusSubtitleGridAfterTextBoxSplit = false;
            store.Save(settings);
            Assert.False(store.Load().FocusSubtitleGridAfterTextBoxSplit);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitForSplitFocusAsync(Func<bool> condition, string message)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (stopwatch.ElapsedMilliseconds > 2000)
                Assert.Fail(message);
            await Task.Delay(10);
        }
    }

    private static bool FocusIsInsideSubtitleGrid(Window window, TableView grid)
    {
        var focused = window.FocusManager?.GetFocusedElement();
        if (ReferenceEquals(focused, grid))
            return true;
        return focused is Visual visual && visual.GetVisualAncestors().Any(x => ReferenceEquals(x, grid));
    }

    [AvaloniaFact]
    public async Task TextBoxCursorSplitReturnsFocusToSubtitleGridWhenEnabled()
    {
        using var host = new Host(withMainView: true);
        var vm = host.Vm;
        vm.PlaybackControls = new PlaybackControlSettings { FocusSubtitleGridAfterTextBoxSplit = true };
        var window = new Window { Width = 1200, Height = 800 };
        MainView.NextHostWindow = window;
        window.Content = new MainView();
        window.Show();
        window.SuppressSaveChangesPromptOnClose(vm);
        try
        {
            var line = new SubtitleLineViewModel(new Paragraph("hello world", 1000, 3000), vm.SelectedSubtitleFormat);
            vm.Subtitles.Add(line);
            vm.SelectedSubtitle = line;
            vm.SelectedSubtitleIndex = 0;
            vm.SubtitleGrid.SelectedItem = line;
            for (var i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

            Assert.True(vm.EditTextBox.TextControl.Focus());
            vm.EditTextBox.SelectionStart = 5;
            Assert.True(vm.EditTextBox.IsFocused);

            vm.SplitAtTextBoxCursorPositionCommand.Execute(null);

            Assert.Equal(2, vm.Subtitles.Count);
            await WaitForSplitFocusAsync(
                () => FocusIsInsideSubtitleGrid(window, vm.SubtitleGrid),
                "分割後字幕列表未取得鍵盤焦點。");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TextBoxCursorSplitKeepsEditorFocusWhenPreferenceDisabled()
    {
        using var host = new Host(withMainView: true);
        var vm = host.Vm;
        vm.PlaybackControls = new PlaybackControlSettings { FocusSubtitleGridAfterTextBoxSplit = false };
        var window = new Window { Width = 1200, Height = 800 };
        MainView.NextHostWindow = window;
        window.Content = new MainView();
        window.Show();
        window.SuppressSaveChangesPromptOnClose(vm);
        try
        {
            var line = new SubtitleLineViewModel(new Paragraph("hello world", 1000, 3000), vm.SelectedSubtitleFormat);
            vm.Subtitles.Add(line);
            vm.SelectedSubtitle = line;
            vm.SelectedSubtitleIndex = 0;
            vm.SubtitleGrid.SelectedItem = line;
            for (var i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

            Assert.True(vm.EditTextBox.TextControl.Focus());
            vm.EditTextBox.SelectionStart = 5;
            vm.SplitAtTextBoxCursorPositionCommand.Execute(null);

            Assert.Equal(2, vm.Subtitles.Count);
            await WaitForSplitFocusAsync(
                () => vm.EditTextBox.IsFocused,
                "關閉分割後移焦點設定時，字幕文字框未保留鍵盤焦點。");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ReviewBalanceMenuShowsStateAndTogglePersistsPreference()
    {
        using var host = new Host(); using var media = new MediaFixture(host);
        var vm = host.Vm;
        vm.PlaybackSettingsStoreOverride = new PlaybackControlSettingsStore(Path.Combine(media.Work, "balance-preferences"));
        var flyout = Assert.IsType<Flyout>(ReversibleAudioSyncUi.CreateToolbarButton(vm).Flyout);
        var panel = Assert.IsType<Border>(flyout.Content);
        Assert.DoesNotContain(panel.GetLogicalDescendants(), c => c is ScrollViewer);
        var balance = panel.GetLogicalDescendants().OfType<Button>()
            .Single(b => b.Name == "AudioBalanceToggleButton");
        Assert.False(vm.PlaybackControls.BalanceEnabled);
        balance.Command!.Execute(null);
        Assert.True(vm.PlaybackControls.BalanceEnabled);
        Assert.True(vm.PlaybackSettingsStoreOverride.Load().BalanceEnabled);
        Assert.Equal("聽校大小聲平衡：開", vm.ReviewAudioBalanceMenuLabel);
        Assert.Equal("聽校大小聲平衡：開", balance.Content);
    }

    [AvaloniaFact]
    public void SynchronousCutKeepsOneParagraphAndMapsEdgesWithoutWarnings()
    {
        var source = new Subtitle { Header = "header", Footer = "footer" };
        source.Paragraphs.Add(new Paragraph("before", 0, 1000));
        source.Paragraphs.Add(new Paragraph("crossing\ntext", 1000, 5000) { Bookmark = "user note", Extra = "style" });
        source.Paragraphs.Add(new Paragraph("left edge", 1500, 3000));
        source.Paragraphs.Add(new Paragraph("removed", 2000, 4000));
        source.Paragraphs.Add(new Paragraph("right edge", 3000, 5000));
        source.Paragraphs.Add(new Paragraph("after", 4000, 7000));
        var result = SynchronousSubtitleCut.Remove(source, 2, 4);
        Assert.Equal(new[] { "before", "crossing\ntext", "left edge", "right edge", "after" }, result.Paragraphs.Select(p => p.Text));
        Assert.Equal(new[] { 0d, 1000d, 1500d, 2000d, 2000d }, result.Paragraphs.Select(p => p.StartTime.TotalMilliseconds));
        Assert.Equal(new[] { 1000d, 3000d, 2000d, 3000d, 5000d }, result.Paragraphs.Select(p => p.EndTime.TotalMilliseconds));
        Assert.Equal("user note", result.Paragraphs[1].Bookmark);
        Assert.Equal("style", result.Paragraphs[1].Extra);
        Assert.Equal("header", result.Header); Assert.Equal("footer", result.Footer);
        Assert.Equal(6, source.Paragraphs.Count);
        Assert.Equal(5000d, source.Paragraphs[1].EndTime.TotalMilliseconds);
        Assert.NotSame(source.Paragraphs[1], result.Paragraphs[1]);
        var again = SynchronousSubtitleCut.Remove(result, 1.75, 2.25);
        Assert.Single(again.Paragraphs, p => p.Text == "crossing\ntext");
        Assert.Equal(2500d, again.Paragraphs.Single(p => p.Text == "crossing\ntext").EndTime.TotalMilliseconds);
    }

    [AvaloniaFact]
    public async Task DeleteOnSelectedAudioRangeRunsPairedCutAndUndo()
    {
        using var host = new Host(); using var media = new MediaFixture(host);
        var vm = host.Vm;
        var range = vm.AudioVisualizer!.NewSelectionParagraph;
        vm.IsSynchronousAudioRangeSelectionEnabled = true;
        vm.AudioVisualizer.AudioRangeSelection = range;
        await media.Cut(viaDelete: true);
        Assert.Equal(6d, media.Player.Duration);
        Assert.Equal(new[] { "crossing", "after" }, vm.Subtitles.Select(p => p.Text));
        Assert.Equal(3d, vm.Subtitles[0].EndTime.TotalSeconds);
        Assert.Null(vm.AudioVisualizer.AudioRangeSelection);
        vm.UndoCommand.Execute(null); await media.WaitIdle();
        Assert.Equal(8d, media.Player.Duration);
        Assert.Equal(5d, vm.Subtitles[0].EndTime.TotalSeconds);
    }

    [AvaloniaFact]
    public void AudioDeleteLeavesTextAndModifiedKeysAloneAndConsumesEmptyRange()
    {
        using var host = new Host(); using var media = new MediaFixture(host);
        var vm = host.Vm;
        var text = new TextBox { Text = "abc" };
        ((StackPanel)media.Window.Content!).Children.Add(text);
        vm.IsSynchronousAudioRangeSelectionEnabled = true;
        text.Focus();
        var e = new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.Delete };
        Assert.False(vm.TryHandleSynchronousAudioDelete(e)); Assert.False(e.Handled);
        vm.AudioVisualizer!.Focus();
        e = new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.Delete, KeyModifiers = Avalonia.Input.KeyModifiers.Shift };
        Assert.False(vm.TryHandleSynchronousAudioDelete(e));
        e = new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.Delete };
        Assert.True(vm.TryHandleSynchronousAudioDelete(e)); Assert.True(e.Handled);
        Assert.Equal(2, vm.Subtitles.Count); Assert.Empty(media.Window.OwnedWindows);
        vm.IsSynchronousAudioRangeSelectionEnabled = false;
        Assert.False(vm.TryHandleSynchronousAudioDelete(new Avalonia.Input.KeyEventArgs { Key = Avalonia.Input.Key.Delete }));
    }

    [AvaloniaFact]
    public void AudioTrimShortcutsCanBeClearedReboundAndRejectNativeCollision()
    {
        using var host = new Host(); using var media = new MediaFixture(host);
        var vm = host.Vm;
        vm.AudioTrimShortcutSettingsStoreOverride =
            new AudioTrimShortcutSettingsStore(Path.Combine(media.Work, "audio-shortcuts"));

        var settings = vm.CloneAudioTrimShortcuts();
        settings.Get("delete-selection").Keys.Clear();
        settings.Get("toggle-trim").Keys = ["Ctrl", "Shift", "F8"];
        Assert.True(vm.TrySaveAudioTrimShortcuts(settings, out var error), error);

        vm.IsSynchronousAudioRangeSelectionEnabled = false;
        Assert.True(vm.AudioVisualizer!.Focus());

        var delete = new KeyEventArgs { Key = Key.Delete };
        Assert.False(vm.TryHandleSynchronousAudioDelete(delete));
        Assert.False(delete.Handled);

        var toggle = new KeyEventArgs
        {
            Key = Key.F8,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift,
        };
        Assert.True(vm.TryHandleSynchronousAudioDelete(toggle));
        Assert.True(toggle.Handled);
        Assert.True(vm.IsSynchronousAudioRangeSelectionEnabled);

        var collision = vm.CloneAudioTrimShortcuts();
        collision.Get("preview-cut").Keys = ["Ctrl", "S"];
        Assert.False(vm.TryValidateAudioTrimShortcuts(collision, out error));
        Assert.Contains("Subtitle Edit", error);
    }

    [AvaloniaFact]
    public void MainToolbarHasOneAudioMenuAndComparisonUsesCurrentWaveform()
    {
        using var host = new Host(); var vm = host.Vm;
        var toolbar = InitToolbar.Make(vm);
        var grid = new Grid();
        var waveform = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        vm.AudioVisualizer = new AudioVisualizer();
        waveform.Children.Add(vm.AudioVisualizer);
        var nativeWaveformToolbar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        Grid.SetRow(nativeWaveformToolbar, 1);
        waveform.Children.Add(nativeWaveformToolbar);
        ReversibleAudioSyncUi.Attach(waveform, vm);
        grid.Children.Add(toolbar);
        var rendered = new Window { Width = 1400, Height = 180, Content = grid };
        using var window = new TestWindowScope(rendered);
        var button = Assert.Single(toolbar.GetVisualDescendants().OfType<Button>(), b => b.Name == "ReversibleAudioSyncToolbarButton");
        Assert.Equal("音訊剪修 ▾", button.Content);
        var flyout = Assert.IsType<Flyout>(button.Flyout);
        var panel = Assert.IsType<Border>(flyout.Content);
        Assert.DoesNotContain(panel.GetLogicalDescendants(), c => c is ScrollViewer);
        var advanced = panel.GetLogicalDescendants().ToArray();
        Assert.Empty(advanced.OfType<Expander>());
        Assert.Contains(advanced.OfType<Button>(), b => b.Name == "AudioAuditionStartButton");
        Assert.Contains(advanced.OfType<Button>(), b => b.Name == "AudioProjectRevisionButton");
        Assert.Contains(advanced.OfType<Button>(), b => b.Name == "AudioShortcutSettingsButton");
        Assert.Contains(advanced.OfType<Button>(), b => b.Name == "AudioToggleComparisonButton");

        var waveformToolbar = waveform.Children
            .OfType<StackPanel>()
            .Single(c => Grid.GetRow(c) == 1);
        var quickBar = waveformToolbar.Children
            .OfType<StackPanel>()
            .Single(b => b.Name == "AudioTrimQuickBar");
        var quick = quickBar.GetLogicalDescendants().ToArray();
        Assert.Contains(quick.OfType<Button>(),
            b => b.Name == "AudioQuickCutButton" && ReferenceEquals(b.Command, vm.SynchronousAudioCutCommand));
        Assert.Contains(quick.OfType<Button>(),
            b => b.Name == "AudioQuickPreviewButton" && ReferenceEquals(b.Command, vm.SynchronousAudioPreviewCommand));
        Assert.Contains(quick.OfType<Button>(), b => b.Name == "AudioQuickMoreButton");
        Assert.DoesNotContain(quick.OfType<Button>(), b => b.Name == "AudioQuickAuditionButton");
        Assert.DoesNotContain(quick.OfType<Button>(), b => b.Name == "AudioQuickCompareButton");

        var range = quick.OfType<ToggleButton>().Single(c => c.Name == "AudioQuickRangeToggle");
        range.IsChecked = true;
        Assert.True(vm.AudioVisualizer.IsAudioRangeSelectionMode);

        var compare = advanced.OfType<Button>().Single(b => b.Name == "AudioToggleComparisonButton");
        compare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains(waveform.Children, c => c is Grid);
        compare.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Same(waveform, vm.AudioVisualizer.Parent);
    }

    [AvaloniaFact]
    public void PunctuationCleanupChangesOnlyEditableTextAndIsOneStepUndoable()
    {
        using var host = new Host();
        var vm = host.Vm;
        var editable = new SubtitleLineViewModel(
            new Paragraph("是不是呢? 價格 1,000.", 1234, 5678), vm.SelectedSubtitleFormat)
        {
            Number = 7,
            OriginalText = "原始對照",
            Style = "Main",
            Actor = "Speaker",
            Layer = 3,
            Bookmark = "複查",
            Forced = true,
            Effect = "banner",
            MarginL = "12",
        };
        var reference = new SubtitleLineViewModel(
            new Paragraph("參考?", 6000, 7000), vm.SelectedSubtitleFormat)
        {
            IsReferenceOnly = true,
        };
        vm.Subtitles.Add(editable);
        vm.Subtitles.Add(reference);
        host.History.Do(vm.MakeUndoRedoObject("before punctuation"));
        var id = editable.Id;

        Assert.Equal(1, vm.ApplySubtitlePunctuation(new[] { editable, reference }));
        var changed = vm.Subtitles[0];
        Assert.Equal("是不是呢  ？  價格  1,000。", changed.Text);
        Assert.Equal((id, 7, 1234d, 5678d, "原始對照", "Main", "Speaker", 3, "複查", true, "banner", "12"),
            (changed.Id, changed.Number, changed.StartTime.TotalMilliseconds, changed.EndTime.TotalMilliseconds,
                changed.OriginalText, changed.Style, changed.Actor, changed.Layer, changed.Bookmark,
                changed.Forced, changed.Effect, changed.MarginL));
        Assert.Equal("參考?", vm.Subtitles[1].Text);
        Assert.True(vm.Subtitles[1].IsReferenceOnly);
        Assert.Equal(2, host.History.UndoCount);

        vm.UndoCommand.Execute(null);
        Assert.Equal("是不是呢? 價格 1,000.", vm.Subtitles[0].Text);
        vm.RedoCommand.Execute(null);
        Assert.Equal("是不是呢  ？  價格  1,000。", vm.Subtitles[0].Text);
    }

    [AvaloniaFact]
    public void DualTimelinePanelKeepsNativeEditorAndProvidesTwoTracksAndCompactMode()
    {
        using var host=new Host();
        var vm=host.Vm;
        vm.AudioVisualizer=new AudioVisualizer();
        vm.AudioVisualizer.CurrentVideoPositionSeconds=45;
        var live=new Grid();live.Children.Add(vm.AudioVisualizer);
        var panel=new DualTimelinePanel(vm);
        var renderedWindow=new Window{Width=1000,Height=350,Content=panel};
        using var window=new TestWindowScope(renderedWindow);
        renderedWindow.UpdateLayout();Dispatcher.UIThread.RunJobs();
        var controls=panel.GetVisualDescendants().ToArray();
        Assert.Equal(2,controls.OfType<OriginalAxisTrack>().Count());
        Assert.Empty(controls.OfType<Expander>());
        Assert.Same(live,vm.AudioVisualizer.Parent);
        Assert.Single(controls.OfType<GridSplitter>());
        var compare=controls.OfType<CheckBox>().Single(c=>Equals(c.Content,"顯示原軌比對"));
        Assert.NotEqual(true,compare.IsChecked);
        Assert.Single(controls.OfType<OriginalAxisTrack>(),t=>t.IsEffectivelyVisible);
        var initialTrack=controls.OfType<OriginalAxisTrack>().Single(t=>t.IsEffectivelyVisible);
        Assert.InRange(45,initialTrack.Start,initialTrack.Start+initialTrack.Bounds.Width*initialTrack.SecondsPerPixel);
        compare.IsChecked=true;
        Assert.Equal(2,controls.OfType<OriginalAxisTrack>().Count(t=>t.IsEffectivelyVisible));
        var compact=controls.OfType<CheckBox>().Single(c=>Equals(c.Content,"緊湊高度"));compact.IsChecked=true;
        Assert.Contains(controls.OfType<Grid>(),g=>g.MaxHeight==160);
        compact.IsChecked=false;
        Assert.False(vm.AudioVisualizer.IsReadOnly);
        var timer=(DispatcherTimer)typeof(DualTimelinePanel).GetField("_timer",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
        Assert.Equal(TimeSpan.FromMilliseconds(250),timer.Interval);
        timer.Stop();
        int trackIndex=0;
        foreach(var waveform in controls.OfType<OriginalAxisTrack>())
        {
            var peaks=new WavePeakData2(100,Enumerable.Range(0,1200).Select(i=>new WavePeak2((short)(4000+12000*Math.Abs(Math.Sin(i*0.05))),(short)(-4000-12000*Math.Abs(Math.Sin(i*0.05))))).ToArray());
            waveform.SetView(peaks,new OriginalAxisMap([new SourceSpan(0,2000),new SourceSpan(4000,12000)],1000),trackIndex++==1,0,.012);
            waveform.SetCursor(5,true);
        }
        var capture=Environment.GetEnvironmentVariable("REVERSIBLE_TIMELINE_TEST_SCREENSHOT");
        if(!string.IsNullOrEmpty(capture))
        {
            Dispatcher.UIThread.RunJobs();AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame=renderedWindow.CaptureRenderedFrame();Assert.NotNull(frame);
            frame!.Save(capture,PngBitmapEncoderOptions.Default);
        }
    }

    private sealed class TestWindowScope : IDisposable
    {
        private readonly Window _window;
        public TestWindowScope(Window window){_window=window;window.Show();Dispatcher.UIThread.RunJobs();}
        public void Dispose(){_window.Close();Dispatcher.UIThread.RunJobs();}
    }

    [AvaloniaFact]
    public async Task OriginalCoordinateClicksPlayCorrectMediaAndPreserveSpeedAndSelection()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        await media.Cut();var vm=host.Vm;media.Player.Speed=1.5;
        var map=vm.DisplayAxisMap;Assert.NotNull(map);Assert.Same(map,vm.DisplayAxisMap);
        await vm.PlayOriginalAxisAsync(true,3);
        Assert.True(vm.IsOriginalAudioTimeline);Assert.True(media.Player.IsPlaying);
        Assert.Equal(3,media.Player.Position);Assert.Equal(1.5,media.Player.Speed);
        await vm.PlayOriginalAxisAsync(false,3); // inside deleted original [2,4), seek to splice
        Assert.False(vm.IsOriginalAudioTimeline);Assert.True(media.Player.IsPlaying);
        Assert.Equal(2,media.Player.Position);Assert.Equal(1.5,media.Player.Speed);
        Assert.Equal(4,vm.DisplayAxisMap!.ToOriginal(media.Player.Position));
        vm.AudioVisualizer??=new AudioVisualizer();
        vm.SelectOriginalAxisRange(1,5);
        Assert.Equal(1,vm.AudioVisualizer.AudioRangeSelection!.StartTime.TotalSeconds);
        Assert.Equal(3,vm.AudioVisualizer.AudioRangeSelection.EndTime.TotalSeconds);
        vm.SelectOriginalAxisRange(0,0);Assert.Null(vm.AudioVisualizer.AudioRangeSelection);
        // The overlay follows SE's native interpolated cursor event and must not poll mpv
        // Position or wait for its slow metadata timer.
        var panel=new DualTimelinePanel(vm);
        var overlayWindow=new Window{Width=900,Height=240,Content=panel};
        using var overlay=new TestWindowScope(overlayWindow);
        var timer=(DispatcherTimer)typeof(DualTimelinePanel).GetField("_timer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(panel)!;
        timer.Stop();
        media.Player.ResetPositionReads();
        vm.AudioVisualizer.CurrentVideoPositionSeconds=2.25;
        var sourcePosition=(double)typeof(DualTimelinePanel).GetField("_lastPosition",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(panel)!;
        Assert.Equal(vm.DisplayAxisMap!.ToOriginal(2.25),sourcePosition);
        Assert.Equal(0,media.Player.PositionReads);
    }

    [AvaloniaFact]
    public async Task OldDeletedRegionCanBeRemovedWithoutRemovingLaterCutAndCancellationIsUndoable()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        var vm=host.Vm;
        var indicator=ReversibleAudioSyncUi.CreateToolbarButton(vm);
        ((StackPanel)media.Window.Content!).Children.Add(indicator);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("音訊剪修 ▾",indicator.Content);
        await media.Cut();Dispatcher.UIThread.RunJobs();
        Assert.Equal("● 音檔編修中 ▾",indicator.Content);
        Assert.Equal("● 音檔編修中 ▾",vm.SynchronousAudioProjectStatus);
        var deleted=Assert.Single(vm.DisplayAudioEditRegions,r=>r.Kind==AudioEditRegionKind.Deleted);
        Assert.Equal(2,deleted.StartSeconds);Assert.Equal(4,deleted.EndSeconds);
        Assert.Equal(vm.MakeUndoRedoObject("cut").SynchronousAudio!.RevisionId,deleted.Id);

        // This later cut crosses the earlier [2,4] original-axis gap. Removing the older
        // marker must not restore the portions that belong to this later operation.
        vm.AudioVisualizer!.NewSelectionParagraph=new SubtitleLineViewModel(new Paragraph("",1000,3000),vm.SelectedSubtitleFormat);
        await media.Cut();
        Assert.Equal(2,vm.DisplayAudioEditRegions.Count(r=>r.Kind==AudioEditRegionKind.Deleted));

        vm.SelectAudioEditRegion(deleted);
        var cancel=vm.SynchronousAudioCancelSelectedRegionCommand.ExecuteAsync(null);
        await MediaFixture.PumpUntil(()=>media.Window.OwnedWindows.OfType<SeMessageBox>().Any());
        MediaFixture.Click(media.Window.OwnedWindows.OfType<SeMessageBox>().Single(),Se.Language.General.Yes);
        await cancel.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(6,media.Player.Duration,3);
        Assert.Equal(2,vm.DisplayAudioEditRegions.Count(r=>r.Kind==AudioEditRegionKind.Deleted));
        Assert.Single(vm.DisplayAudioEditRegions.Where(r=>r.Kind==AudioEditRegionKind.Deleted).Select(r=>r.Id).Distinct());
        Assert.DoesNotContain(vm.DisplayAudioEditRegions,r=>r.Id==deleted.Id);
        Assert.Null(vm.SelectedAudioEditRegion);

        vm.UndoCommand.Execute(null);await media.WaitIdle();
        Assert.Equal(4,media.Player.Duration,3);
        Assert.Equal(2,vm.DisplayAudioEditRegions.Count(r=>r.Kind==AudioEditRegionKind.Deleted));
        vm.RedoCommand.Execute(null);await media.WaitIdle();
        Assert.Equal(6,media.Player.Duration,3);
        Assert.Equal(2,vm.DisplayAudioEditRegions.Count(r=>r.Kind==AudioEditRegionKind.Deleted));
    }

    [AvaloniaFact]
    public void AudioOnlyPlayheadUsesTheOfficialFreezePolicyAtTwoTimesSpeed()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        var vm=host.Vm;var control=vm.GetVideoPlayerControl();Assert.NotNull(control);
        var method=typeof(MainViewModel).GetMethod("UpdatePlayheadEstimate",BindingFlags.Instance|BindingFlags.NonPublic)!;
        double Tick(string file)
        {
            const double start=3;
            var now=System.Diagnostics.Stopwatch.GetTimestamp();
            SetField(vm,"_videoFileName",file);
            SetField(vm,"_playheadEstimateSeconds",start);
            SetField(vm,"_playheadLastRealSeconds",start);
            SetField(vm,"_playheadLastTimestamp",now-System.Diagnostics.Stopwatch.Frequency/10);
            SetField(vm,"_playheadLastRawChangeTs",now-System.Diagnostics.Stopwatch.Frequency);
            SetField(vm,"_playheadValid",true);
            SetField(vm,"_pauseRequested",false);
            SetField(vm,"_playheadSeekTarget",null);
            media.Player.Position=start;media.Player.Speed=2;media.Player.Play();
            return (double)method.Invoke(vm,[control,true])!;
        }
        Assert.Equal(3,Tick("initial.wav"),6);
        Assert.Equal(3,Tick("initial.mkv"),6);
    }

    [AvaloniaFact]
    public void AudioOnlyPlayheadContinuesAtOneAndTwoTimesSpeedWhenPlayerClockMoves()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        var vm=host.Vm;var control=vm.GetVideoPlayerControl();Assert.NotNull(control);
        var tick=typeof(MainViewModel).GetMethod("UpdatePlayheadEstimate",BindingFlags.Instance|BindingFlags.NonPublic)!;
        foreach(var speed in new[]{1d,2d})
        {
            var now=System.Diagnostics.Stopwatch.GetTimestamp();
            SetField(vm,"_videoFileName","moving.wav");
            SetField(vm,"_playheadEstimateSeconds",3d);
            SetField(vm,"_playheadLastRealSeconds",3d);
            SetField(vm,"_playheadLastTimestamp",now-System.Diagnostics.Stopwatch.Frequency/20);
            SetField(vm,"_playheadLastRawChangeTs",now-System.Diagnostics.Stopwatch.Frequency/20);
            SetField(vm,"_playheadValid",true);
            SetField(vm,"_playheadResyncOnPlay",false);
            SetField(vm,"_pauseRequested",false);
            SetField(vm,"_playheadSeekTarget",null);
            media.Player.Speed=speed;
            media.Player.Position=3+0.05*speed;
            media.Player.Play();

            Assert.InRange((double)tick.Invoke(vm,[control,true])!,3+0.04*speed,3+0.06*speed);
        }
    }

    [AvaloniaFact]
    public void AudioOnlyPlayheadUsesTheOfficialResyncThresholdForAStaleClock()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        var vm=host.Vm;var control=vm.GetVideoPlayerControl();Assert.NotNull(control);
        var method=typeof(MainViewModel).GetMethod("UpdatePlayheadEstimate",BindingFlags.Instance|BindingFlags.NonPublic)!;
        SetField(vm,"_videoFileName","continuous.wav");
        SetField(vm,"_playheadEstimateSeconds",0.75d);
        SetField(vm,"_playheadLastRealSeconds",0d);
        SetField(vm,"_playheadValid",true);
        SetField(vm,"_pauseRequested",false);
        SetField(vm,"_playheadSeekTarget",null);
        SetField(vm,"_playheadResyncOnPlay",false);
        var now=System.Diagnostics.Stopwatch.GetTimestamp();
        SetField(vm,"_playheadLastTimestamp",now-System.Diagnostics.Stopwatch.Frequency*16/1000);
        SetField(vm,"_playheadLastRawChangeTs",now-System.Diagnostics.Stopwatch.Frequency);
        media.Player.Position=0;media.Player.Speed=2;media.Player.Play();

        Assert.Equal(0,(double)method.Invoke(vm,[control,true])!,6);
    }

    [AvaloniaFact]
    public void PausedAudioPlayheadDoesNotRewindAtOneAndTwoTimesSpeed()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        var vm=host.Vm;var control=vm.GetVideoPlayerControl();Assert.NotNull(control);
        var tick=typeof(MainViewModel).GetMethod("UpdatePlayheadEstimate",BindingFlags.Instance|BindingFlags.NonPublic)!;
        foreach(var speed in new[]{1d,2d})
        foreach(var cursor in new[]{2.45d,2.90d})
        {
            var now=System.Diagnostics.Stopwatch.GetTimestamp();
            media.Player.Speed=speed;
            media.Player.Position=2.2;
            media.Player.Pause();
            SetField(vm,"_videoFileName","paused.wav");
            SetField(vm,"_playheadEstimateSeconds",cursor);
            SetField(vm,"_playheadLastRealSeconds",2.2d);
            SetField(vm,"_playheadLastTimestamp",now);
            SetField(vm,"_playheadLastRawChangeTs",now-System.Diagnostics.Stopwatch.Frequency/5);
            SetField(vm,"_playheadValid",true);
            SetField(vm,"_playheadPausedSettled",false);
            SetField(vm,"_playheadWasPlaying",true);
            SetField(vm,"_audioPauseCursorSeconds",null);
            SetField(vm,"_pauseRequested",false);
            SetField(vm,"_playheadSeekTarget",null);

            Assert.Equal(cursor,(double)tick.Invoke(vm,[control,false])!,3);
        }
    }

    [AvaloniaFact]
    public void PausedAudioSplitUsesTheFrozenVisibleCursor()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        var vm=host.Vm;var control=vm.GetVideoPlayerControl();Assert.NotNull(control);
        vm.SelectedSubtitle=vm.Subtitles[0];
        control.Duration=media.Player.Duration;
        SetField(vm,"_videoFileName","paused.wav");
        SetField(vm,"_playheadEstimateSeconds",2.3d);
        SetField(vm,"_playheadValid",true);
        SetField(vm,"_pauseRequested",false);
        SetField(vm,"_playheadSeekTarget",null);
        media.Player.Position=2.1;
        vm.RequestPausePlayheadFreeze();
        media.Player.Pause();

        vm.SplitAtVideoPositionCommand.Execute(null);

        Assert.Equal(3,vm.Subtitles.Count);
        Assert.Equal(2.3,vm.Subtitles[1].StartTime.TotalSeconds,3);
    }

    [AvaloniaFact]
    public void AudioOnlyResumeDoesNotRewindTheFrozenCursor()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        var vm=host.Vm;var control=vm.GetVideoPlayerControl();Assert.NotNull(control);
        var tick=typeof(MainViewModel).GetMethod("UpdatePlayheadEstimate",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var now=System.Diagnostics.Stopwatch.GetTimestamp();
        SetField(vm,"_videoFileName","paused.wav");
        SetField(vm,"_playheadEstimateSeconds",2.45d);
        SetField(vm,"_playheadLastRealSeconds",2.20d);
        SetField(vm,"_playheadLastTimestamp",now-System.Diagnostics.Stopwatch.Frequency/100);
        SetField(vm,"_playheadLastRawChangeTs",now-System.Diagnostics.Stopwatch.Frequency/100);
        SetField(vm,"_playheadValid",true);
        SetField(vm,"_playheadResyncOnPlay",true);
        SetField(vm,"_pauseRequested",false);
        SetField(vm,"_playheadSeekTarget",null);
        control.Duration=media.Player.Duration;
        media.Player.Position=2.20;
        media.Player.Pause();
        vm.RequestPausePlayheadFreeze();
        vm.CancelPausePlayheadFreeze();
        media.Player.Speed=2;
        media.Player.Play();

        Assert.Equal(2.45,media.Player.Position,3);
        Assert.True((double)tick.Invoke(vm,[control,true])!>=2.45);
    }

    [AvaloniaFact]
    public void AudioOnlyExplicitSeekOverridesThePauseCursorOnResume()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        var vm=host.Vm;var control=vm.GetVideoPlayerControl();Assert.NotNull(control);
        control.Duration=media.Player.Duration;
        SetField(vm,"_videoFileName","paused.wav");
        SetField(vm,"_playheadEstimateSeconds",2.45d);
        SetField(vm,"_playheadValid",true);
        media.Player.Position=2.20;
        media.Player.Pause();
        vm.RequestPausePlayheadFreeze();
        control.SeekTo(3.1);
        typeof(MainViewModel).GetMethod("PinPlayheadTo",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(vm,[3.1d]);

        vm.CancelPausePlayheadFreeze();

        Assert.Equal(3.1,media.Player.Position,3);
        Assert.Equal(3.1,(double)typeof(MainViewModel).GetField("_playheadEstimateSeconds",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm)!,3);
    }

    [AvaloniaFact]
    public void DoubleClickingSubtitleSeeksThePlayerAndCursorAfterAudioPause()
    {
        using var settings = new SettingsScope("General.SubtitleDoubleClickAction");
        using var host = new Host(); using var media = new MediaFixture(host);
        var vm = host.Vm; var control = vm.GetVideoPlayerControl(); Assert.NotNull(control);
        control.Duration = media.Player.Duration;
        vm.SubtitleGrid.SelectedItem = vm.Subtitles[1]; // 6-second cue
        var tick = typeof(MainViewModel).GetMethod("UpdatePlayheadEstimate", BindingFlags.Instance | BindingFlags.NonPublic)!;

        foreach (var (action, expected, playing) in new[]
        {
            (SubtitleDoubleClickActionType.GoToSubtitleOnly, 6d, false),
            (SubtitleDoubleClickActionType.GoToSubtitleAndPause, 6d, false),
            (SubtitleDoubleClickActionType.GoToSubtitleAndPlay, 6d, true),
            (SubtitleDoubleClickActionType.GoToSubtitleMinus1SecAndPause, 5d, false),
            (SubtitleDoubleClickActionType.GoToSubtitleMinusHalfSecAndPause, 5.5d, false),
            (SubtitleDoubleClickActionType.GoToSubtitleMinus1SecAndPlay, 5d, true),
        })
        {
            Se.Settings.General.SubtitleDoubleClickAction = action.ToString();
            media.Player.Pause();
            control.SeekTo(2.2);
            SetField(vm, "_playheadEstimateSeconds", 2.45d);
            SetField(vm, "_playheadSeekTarget", null);
            SetField(vm, "_playheadValid", true);
            vm.RequestPausePlayheadFreeze();

            vm.OnSubtitleGridDoubleTapped(vm.SubtitleGrid);

            Assert.Equal(expected, media.Player.Position, 3);
            Assert.Equal(expected, vm.AudioVisualizer!.CurrentVideoPositionSeconds, 3);
            Assert.Equal(expected, (double)tick.Invoke(vm, [control, playing])!, 3);
        }
    }

    [AvaloniaFact]
    public void RoutedSubtitleDoubleClickSelectsTheClickedRowAndMovesTheCursor()
    {
        using var settings = new SettingsScope("General.SubtitleDoubleClickAction");
        using var host = new Host(withMainView: true);
        var vm = host.Vm;
        var window = new Window { Width = 1200, Height = 800 };
        MainView.NextHostWindow = window;
        window.Content = new MainView();
        window.Show();
        window.SuppressSaveChangesPromptOnClose(vm);
        try
        {
            Se.Settings.General.SubtitleDoubleClickAction = SubtitleDoubleClickActionType.GoToSubtitleAndPause.ToString();
            var player = new FakePlayer();
            vm.Subtitles.Add(new SubtitleLineViewModel(new Paragraph("first", 1000, 2000), vm.SelectedSubtitleFormat));
            vm.Subtitles.Add(new SubtitleLineViewModel(new Paragraph("second", 6000, 7000), vm.SelectedSubtitleFormat));
            for (var i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }

            var row = vm.SubtitleGrid.ContainerFromItem(vm.Subtitles[1]);
            Assert.NotNull(row);
            var point = row!.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window);
            Assert.NotNull(point);
            PointerEventArgs? pointerPress = null;
            row.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointerPress = e,
                RoutingStrategies.Bubble, handledEventsToo: true);
            window.MouseDown(point.Value, MouseButton.Left);
            window.MouseUp(point.Value, MouseButton.Left);
            Assert.NotNull(pointerPress);
            vm.VideoPlayerControl = new VideoPlayerControl(player) { Duration = 8 };
            SetField(vm, "_videoFileName", "double-click.wav");
            Assert.Equal(1, TableViewExtras.GetRowIndexFromPoint(vm.SubtitleGrid, pointerPress.GetPosition(vm.SubtitleGrid)));
            var routedHits = 0;
            var routedIndex = -2;
            var handledAtHost = false;
            vm.SubtitleGridDropHost!.AddHandler(InputElement.DoubleTappedEvent, (_, e) =>
            {
                routedHits++;
                routedIndex = TableViewExtras.GetRowIndexFromPoint(vm.SubtitleGrid, e.GetPosition(vm.SubtitleGrid));
                handledAtHost = e.Handled;
            },
                RoutingStrategies.Bubble, handledEventsToo: true);
            row.RaiseEvent(new TappedEventArgs(InputElement.DoubleTappedEvent, pointerPress));
            Assert.Equal(6, player.Position, 3);
            Dispatcher.UIThread.RunJobs();

            Assert.True(routedHits > 0, "Double tap did not reach the subtitle drop host");
            Assert.Equal(1, routedIndex);
            Assert.True(handledAtHost, "Subtitle drop host did not handle the double tap");
            Assert.Same(vm.Subtitles[1], vm.SubtitleGrid.SelectedItem);
            Assert.Equal(6, player.Position, 3);
            Assert.Equal(6, vm.AudioVisualizer!.CurrentVideoPositionSeconds, 3);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SingleClickingSubtitleUsesTheNewAudioPositionAfterPause()
    {
        using var settings = new SettingsScope("General.SubtitleSingleClickAction");
        using var host = new Host(); using var media = new MediaFixture(host);
        var vm = host.Vm; var control = vm.GetVideoPlayerControl(); Assert.NotNull(control);
        control.Duration = media.Player.Duration;
        control.PositionChanged += vm.OnVideoPlayerPositionSet;
        vm.SubtitleGrid.SelectedItem = vm.Subtitles[1];
        var tick = typeof(MainViewModel).GetMethod("UpdatePlayheadEstimate", BindingFlags.Instance | BindingFlags.NonPublic)!;

        foreach (var action in new[]
        {
            SubtitleSingleClickActionType.GoToSubtitleAndPause,
            SubtitleSingleClickActionType.GoToSubtitleAndPlay,
            SubtitleSingleClickActionType.GoToSubtitleAndSetVideoPosition,
        })
        {
            Se.Settings.General.SubtitleSingleClickAction = action.ToString();
            media.Player.Pause();
            control.SeekTo(2.2);
            SetField(vm, "_playheadEstimateSeconds", 2.45d);
            SetField(vm, "_playheadSeekTarget", null);
            SetField(vm, "_playheadValid", true);
            vm.RequestPausePlayheadFreeze();

            vm.OnSubtitleGridSingleTapped(vm.SubtitleGrid);

            Assert.Equal(6, media.Player.Position, 3);
            Assert.Equal(6, vm.AudioVisualizer!.CurrentVideoPositionSeconds, 3);
            Assert.Equal(6, (double)tick.Invoke(vm, [control, media.Player.IsPlaying])!, 3);
        }
    }

    [AvaloniaFact]
    public void PreviewRecentCutStartsAtTheCutInsteadOfTheOldPauseCursor()
    {
        using var host = new Host(); using var media = new MediaFixture(host);
        var vm = host.Vm; var control = vm.GetVideoPlayerControl(); Assert.NotNull(control);
        control.Duration = media.Player.Duration;
        control.PositionChanged += vm.OnVideoPlayerPositionSet;
        SetField(vm, "_synchronousAudioState", new SynchronousAudioState(media.Source, "hash", 0, "revision", "session"));
        SetField(vm, "_synchronousAudioCutPosition", 6d);
        control.SeekTo(2.2);
        SetField(vm, "_playheadEstimateSeconds", 2.45d);
        SetField(vm, "_playheadSeekTarget", null);
        SetField(vm, "_playheadValid", true);
        vm.RequestPausePlayheadFreeze();

        vm.SynchronousAudioPreviewCommand.Execute(null);

        Assert.Equal(4, media.Player.Position, 3); // two seconds before the cut
        Assert.Equal(4, vm.AudioVisualizer!.CurrentVideoPositionSeconds, 3);
    }

    [AvaloniaFact]
    public void SplitAtVideoPositionUsesTheSettledPlayerClockInsteadOfAStaleDisplayTick()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        var vm=host.Vm;var control=vm.GetVideoPlayerControl();Assert.NotNull(control);
        vm.SelectedSubtitle=vm.Subtitles[0];
        control.Duration=media.Player.Duration;
        control.SetPositionDisplayOnly(2.1); // the 50 ms display timer has not caught up
        media.Player.Position=2.3;
        // This case is specifically about the settled raw player clock, not an authoritative
        // waveform seek/pause pin. Make that precondition explicit so other UI tests running
        // in parallel cannot leave a playhead-estimate path armed through shared host state.
        SetField(vm,"_playheadSeekTarget",null);
        SetField(vm,"_audioPauseCursorSeconds",null);
        SetField(vm,"_playheadValid",false);
        media.Player.Pause();
        Assert.Same(vm.Subtitles[0],vm.SelectedSubtitle);
        Assert.Equal(2.1,control.Position,3);
        Assert.Equal(2.3,media.Player.Position,3);

        vm.SplitAtVideoPositionCommand.Execute(null);

        Assert.Equal(3,vm.Subtitles.Count);
        Assert.Equal(2.3,vm.Subtitles[1].StartTime.TotalSeconds,3);
    }

    [AvaloniaFact]
    public void SplitImmediatelyAfterClickingTheCursorUsesThePendingSeekTarget()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        var vm=host.Vm;var control=vm.GetVideoPlayerControl();Assert.NotNull(control);
        vm.SelectedSubtitle=vm.Subtitles[0];
        control.Duration=media.Player.Duration;
        media.Player.Position=1.5; // mpv has not applied the seek yet
        control.SetPositionDisplayOnly(2.4);
        typeof(MainViewModel).GetMethod("PinPlayheadTo",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(vm,[2.4d]);

        vm.SplitAtVideoPositionCommand.Execute(null);

        Assert.Equal(3,vm.Subtitles.Count);
        Assert.Equal(2.4,vm.Subtitles[1].StartTime.TotalSeconds,3);
    }

    [AvaloniaFact]
    public void ShortSelectedAudioDoesNotEndUntilThePlayerClockReachesItsEnd()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        var vm=host.Vm;var control=vm.GetVideoPlayerControl();Assert.NotNull(control);
        var line=new SubtitleLineViewModel(new Paragraph("brief",1000,1200),vm.SelectedSubtitleFormat);
        SetField(vm,"_playSelectionItem",new Nikse.SubtitleEdit.Features.Main.MainHelpers.PlaySelectionItem([line],line.EndTime,false));
        var reachedEnd=typeof(MainViewModel).GetMethod("HasPlaySelectionReachedEnd",BindingFlags.Instance|BindingFlags.NonPublic);
        Assert.NotNull(reachedEnd);
        media.Player.Speed=2;
        media.Player.Position=1.10;
        SetField(vm,"_playheadEstimateSeconds",1.35d);
        Assert.False((bool)reachedEnd.Invoke(vm,[control])!);
        media.Player.Position=1.20;
        Assert.True((bool)reachedEnd.Invoke(vm,[control])!);
    }

    [AvaloniaFact]
    public void ShortSelectedAudioWaitsForItsInitialSeekBeforeCheckingTheEnd()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        var vm=host.Vm;var control=vm.GetVideoPlayerControl();Assert.NotNull(control);
        var line=new SubtitleLineViewModel(new Paragraph("brief",1000,1200),vm.SelectedSubtitleFormat);
        SetField(vm,"_playSelectionItem",new Nikse.SubtitleEdit.Features.Main.MainHelpers.PlaySelectionItem([line],line.EndTime,false));
        media.Player.Speed=2;
        media.Player.Position=5; // the old mpv position before the selection seek lands
        typeof(MainViewModel).GetMethod("PinPlayheadTo",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(vm,[1d]);
        var reachedEnd=typeof(MainViewModel).GetMethod("HasPlaySelectionReachedEnd",BindingFlags.Instance|BindingFlags.NonPublic)!;

        Assert.False((bool)reachedEnd.Invoke(vm,[control])!);
        SetField(vm,"_playheadSeekTarget",null);
        media.Player.Position=1.2;
        Assert.True((bool)reachedEnd.Invoke(vm,[control])!);
    }

    [AvaloniaFact]
    public void LateTwoTimesSpeedTickReplaysTheNextVeryShortSelectedSubtitle()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        var vm=host.Vm;var control=vm.GetVideoPlayerControl();Assert.NotNull(control);
        var first=new SubtitleLineViewModel(new Paragraph("first",1000,1200),vm.SelectedSubtitleFormat);
        var shortNext=new SubtitleLineViewModel(new Paragraph("short",1200,1260),vm.SelectedSubtitleFormat);
        SetField(vm,"_playSelectionItem",new Nikse.SubtitleEdit.Features.Main.MainHelpers.PlaySelectionItem([first,shortNext],first.EndTime,false));
        media.Player.Speed=2;
        media.Player.Position=1.30; // a 50 ms UI tick can cross this 60 ms subtitle at 2x
        var advance=typeof(MainViewModel).GetMethod("AdvancePlaySelection",BindingFlags.Instance|BindingFlags.NonPublic);
        Assert.NotNull(advance);

        Assert.Same(shortNext,advance.Invoke(vm,[control]));
        var seekTarget=(double?)typeof(MainViewModel).GetField("_playheadSeekTarget",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm);
        Assert.Equal(1.20,seekTarget!.Value,3);
    }

    [AvaloniaFact]
    public void CompletedMpvSeekReleasesPlayheadPinWithoutWaitingForToleranceTimeout()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        var vm=host.Vm;var control=vm.GetVideoPlayerControl();Assert.NotNull(control);
        var method=typeof(MainViewModel).GetMethod("UpdatePlayheadEstimate",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var issued=System.Diagnostics.Stopwatch.GetTimestamp()-System.Diagnostics.Stopwatch.Frequency/2;
        SetField(vm,"_videoFileName","seek.wav");
        SetField(vm,"_playheadEstimateSeconds",3d);
        SetField(vm,"_playheadLastRealSeconds",3d);
        SetField(vm,"_playheadLastTimestamp",issued);
        SetField(vm,"_playheadSeekTarget",(double?)3d);
        SetField(vm,"_playheadSeekTargetTs",issued);
        SetField(vm,"_playheadValid",true);
        media.Player.Position=3.2;media.Player.Play();
        media.Player.SupportRestartEvents=true;
        media.Player.PlaybackRestartTimestamp=System.Diagnostics.Stopwatch.GetTimestamp();

        Assert.Equal(3.2,(double)method.Invoke(vm,[control,true])!,6);
        Assert.Null(typeof(MainViewModel).GetField("_playheadSeekTarget",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm));
    }

    [AvaloniaFact]
    public void NativeWaveformCursorDrawsAtZeroAndVisibleViewEdges()
    {
        var visualizer=new AudioVisualizer();
        var contextType=typeof(AudioVisualizer).GetNestedType("RenderContext",BindingFlags.NonPublic)!;
        var draw=typeof(AudioVisualizer).GetMethod("DrawCurrentVideoPosition",BindingFlags.Instance|BindingFlags.NonPublic)!;
        int Count(double position,double start)
        {
            var state=Activator.CreateInstance(contextType)!;
            foreach(var item in new (string Name,object Value)[] {
                ("Width",600d),("Height",100d),("CurrentVideoPositionSeconds",position),
                ("StartPositionSeconds",start),("SampleRate",100),("ZoomFactor",1d) })
                contextType.GetField(item.Name)!.SetValue(state,item.Value);
            var group=new Avalonia.Media.DrawingGroup();
            using(var context=group.Open())draw.Invoke(visualizer,[context,state]);
            return group.Children.Count;
        }
        Assert.True(Count(0,0)>0);
        Assert.True(Count(3,3)>0);
        Assert.True(Count(6,0)>0);
        Assert.Equal(0,Count(-.01,0));
    }

    [AvaloniaFact]
    public void OriginalAxisPointerDistinguishesSeekFromRangeAndEscape()
    {
        var track=new OriginalAxisTrack{AllowSelection=true};
        var rendered=new Window{Width=600,Height=200,Content=track};using var window=new TestWindowScope(rendered);
        track.SetView(null,null,true,10,.02);
        double? seek=null; (double A,double B)? range=null;
        track.SeekRequested+=t=>seek=t;track.RangeRequested+=(a,b)=>range=(a,b);
        rendered.MouseDown(new Point(100,80),Avalonia.Input.MouseButton.Left);rendered.MouseUp(new Point(100,80),Avalonia.Input.MouseButton.Left);
        Assert.Equal(12,seek);Assert.Null(range);seek=null;
        rendered.MouseDown(new Point(100,80),Avalonia.Input.MouseButton.Left);rendered.MouseMove(new Point(200,80));rendered.MouseUp(new Point(200,80),Avalonia.Input.MouseButton.Left);
        Assert.Null(seek);Assert.Equal((12d,14d),range);
        rendered.KeyPress(Avalonia.Input.Key.Escape,Avalonia.Input.RawInputModifiers.None,Avalonia.Input.PhysicalKey.Escape,null);
        Assert.Equal((0d,0d),range);
        var deleted=new AudioEditRegion("cut",AudioEditRegionKind.Deleted,12.4,13.2);
        track.SetView(null,null,true,10,.02,[deleted]);
        AudioEditRegion? selected=null;track.EditRegionRequested+=r=>selected=r;
        rendered.MouseDown(new Point(140,80),Avalonia.Input.MouseButton.Left);rendered.MouseUp(new Point(140,80),Avalonia.Input.MouseButton.Left);
        Assert.Equal(deleted,selected);Assert.Null(seek);
    }

    [AvaloniaFact]
    public void MenuOnlyExtensionLeavesNativeGeometryUntouchedAndComparisonIsReversible()
    {
        using var host=new Host();var vm=host.Vm;vm.AudioVisualizer=new AudioVisualizer();
        var inner=new Grid{RowDefinitions=new RowDefinitions("*,Auto")};inner.Children.Add(vm.AudioVisualizer);
        var footer=new TextBlock{Text="SE 播放控制"};Grid.SetRow(footer,1);inner.Children.Add(footer);
        var outer=new Grid{RowDefinitions=new RowDefinitions("*,8,150")};
        var wrapper=new Border{Child=inner};Grid.SetRow(wrapper,2);outer.Children.Add(wrapper);
        var splitter=new GridSplitter{Height=8,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Stretch};Grid.SetRow(splitter,1);outer.Children.Add(splitter);
        var rendered=new Window{Width=720,Height=720,Content=outer};using var window=new TestWindowScope(rendered);
        Dispatcher.UIThread.RunJobs();
        var nativeBounds=vm.AudioVisualizer.Bounds;var footerBounds=footer.Bounds;
        var outerHeight=outer.RowDefinitions[2].Height;var outerMinimum=outer.RowDefinitions[2].MinHeight;
        ReversibleAudioSyncUi.Attach(inner,vm);Dispatcher.UIThread.RunJobs();
        Assert.Equal(2,inner.RowDefinitions.Count);Assert.Same(inner,vm.AudioVisualizer.Parent);
        Assert.Equal(nativeBounds,vm.AudioVisualizer.Bounds);Assert.Equal(footerBounds,footer.Bounds);
        Assert.Empty(inner.GetVisualDescendants().OfType<OriginalAxisTrack>());
        var menu=vm.AudioVisualizer.MenuFlyout.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"音訊剪修"));
        var toggle=menu.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"顯示／隱藏原軌比對"));
        toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Dispatcher.UIThread.RunJobs();
        Assert.Equal(2,inner.GetVisualDescendants().OfType<OriginalAxisTrack>().Count());
        Assert.True(vm.AudioVisualizer.IsEffectivelyVisible);
        Assert.Equal(outerHeight,outer.RowDefinitions[2].Height);Assert.Equal(outerMinimum,outer.RowDefinitions[2].MinHeight);
        toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Dispatcher.UIThread.RunJobs();
        Assert.Empty(inner.GetVisualDescendants().OfType<OriginalAxisTrack>());
        Assert.Same(inner,vm.AudioVisualizer.Parent);
        Assert.Equal(nativeBounds,vm.AudioVisualizer.Bounds);Assert.Equal(footerBounds,footer.Bounds);
        Assert.Equal(outerHeight,outer.RowDefinitions[2].Height);Assert.Equal(outerMinimum,outer.RowDefinitions[2].MinHeight);
        var capture=Environment.GetEnvironmentVariable("REVERSIBLE_TIMELINE_TEST_SCREENSHOT");
        if(!string.IsNullOrEmpty(capture)){AvaloniaHeadlessPlatform.ForceRenderTimerTick();using var frame=rendered.CaptureRenderedFrame();frame!.Save(capture+".full.png",PngBitmapEncoderOptions.Default);}
    }

    [AvaloniaFact]
    public async Task DualTimelineMappingAndSwitchPreserveEditedState()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        await media.Cut();var vm=host.Vm;
        Assert.Equal(AudioSessionMode.Editing, vm.SynchronousAudioMode);
        var cut=vm.MakeUndoRedoObject("cut");
        Assert.Equal(5,vm.ComparisonPosition(3));
        var original=vm.ComparisonRevisionDirectory();Assert.NotNull(original);
        Assert.Equal(2,(await vm.ReadComparisonRevisionAsync(original!,CancellationToken.None)).Lines.Length);
        await vm.SwitchAudioTimelineAsync(true,5);
        Assert.True(vm.IsOriginalAudioTimeline);Assert.Equal(AudioSessionMode.OriginalPreview, vm.SynchronousAudioMode);Assert.Equal(3,vm.ComparisonPosition(5));
        Assert.Equal(8,media.Player.Duration);Assert.Equal(5,media.Player.Position);
        await vm.SwitchAudioTimelineAsync(false,3);
        Assert.False(vm.IsOriginalAudioTimeline);Assert.Equal(AudioSessionMode.Editing, vm.SynchronousAudioMode);Assert.Equal(6,media.Player.Duration);
        Assert.Equal(cut.SynchronousAudio!.Sha256,vm.MakeUndoRedoObject("after").SynchronousAudio!.Sha256);
    }

    [AvaloniaFact]
    public async Task UnfinishedAuditionRecoveryPromptsOnlyWhenNotExplicitlyDismissed()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        media.ConfigureAudition();
        await vm.SynchronousAudioOpenInAuditionCommand.ExecuteAsync(null);
        var files = Assert.IsType<AuditionHandoff>(vm.PendingAuditionFiles);
        var folder = Path.GetDirectoryName(files.EditPath)!;
        var markerFile = Path.Combine(folder, "handoff.dismissed");

        vm.SynchronousAudioCancelAuditionCommand.Execute(null);
        Assert.True(File.Exists(markerFile));
        await vm.TryOfferPendingAuditionRecoveryAsync();
        Assert.Null(vm.PendingAuditionFiles);

        File.Delete(markerFile);
        var offer = vm.TryOfferPendingAuditionRecoveryAsync();
        await MediaFixture.PumpUntil(() => media.Window.OwnedWindows.OfType<SeMessageBox>().Any());
        MediaFixture.Click(media.Window.OwnedWindows.OfType<SeMessageBox>().Single(), Se.Language.General.Yes);
        await offer;
        Assert.Equal(files.EditPath, vm.PendingAuditionFiles!.EditPath);
        Assert.False(File.Exists(markerFile));
        vm.SynchronousAudioCancelAuditionCommand.Execute(null);
    }

    [AvaloniaFact]
    public async Task RevisionRecoveryUsesHistoryListInsteadOfRevisionJsonPicker()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        await media.Cut();

        var open = vm.SynchronousAudioOpenRevisionCommand.ExecuteAsync(null);
        await MediaFixture.PumpUntil(() => media.Window.OwnedWindows.Any(w =>
            string.Equals(w.Title, "恢復歷史音訊／字幕版本", StringComparison.Ordinal)));
        var dialog = media.Window.OwnedWindows.Single(w =>
            string.Equals(w.Title, "恢復歷史音訊／字幕版本", StringComparison.Ordinal));
        var list = Assert.Single(dialog.GetVisualDescendants().OfType<ListBox>());
        Assert.NotEmpty(list.Items.Cast<object>());
        Assert.NotNull(list.SelectedItem);
        var cancel = dialog.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "取消"));
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await open.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(vm.MakeUndoRedoObject("after revision list").SynchronousAudio);
    }

    [AvaloniaFact]
    public async Task RecoverStoredAuditionHandoffPreservesRepairAndRejectsWrongBaseline()
    {
        using var host=new Host();using var media=new MediaFixture(host);
        media.ConfigureAudition();var vm=host.Vm;
        await vm.SynchronousAudioOpenInAuditionCommand.ExecuteAsync(null);
        var files=vm.PendingAuditionFiles!;
        vm.SynchronousAudioCancelAuditionCommand.Execute(null);
        var handoffPath=Path.Combine(Path.GetDirectoryName(files.EditPath)!,"handoff.json");
        await vm.RecoverAuditionHandoffAsync(handoffPath);
        Assert.Equal(files.EditPath,vm.PendingAuditionFiles!.EditPath);
        await vm.ApplyAuditionReturnAsync(CancellationToken.None);
        Assert.Null(vm.PendingAuditionFiles);Assert.Equal(AudioSessionMode.Editing, vm.SynchronousAudioMode);Assert.True(File.Exists(files.EditPath));
        SetState(vm,vm.MakeUndoRedoObject("wrong").SynchronousAudio! with {Sha256="wrong"});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>vm.RecoverAuditionHandoffAsync(handoffPath));
    }

    [AvaloniaFact]
    public async Task RealCutCommandConfirmationThenUndoRedoRestoresThePairedTimeline()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        var before = vm.GetUndoRedoHash();
        // The audio interval is independent of SE's new-subtitle selection slot.
        var selectedRange = vm.AudioVisualizer!.NewSelectionParagraph;
        vm.IsSynchronousAudioRangeSelectionEnabled = true;
        vm.AudioVisualizer.AudioRangeSelection = selectedRange;
        Assert.Null(vm.AudioVisualizer.NewSelectionParagraph);
        await media.Cut();
        var cut = vm.MakeUndoRedoObject("cut");
        Assert.NotNull(cut.SynchronousAudio);
        Assert.NotEqual(before, cut.Hash);
        Assert.Equal(6d, WaveInfo.Read(cut.SynchronousAudio.FileName).DurationSeconds);
        Assert.Equal(new[] { 1000d, 4000d }, vm.Subtitles.Select(p => p.StartTime.TotalMilliseconds));
        Assert.Equal(new[] { 3000d, 5000d }, vm.Subtitles.Select(p => p.EndTime.TotalMilliseconds));
        Assert.Equal(new[] { "crossing", "after" }, vm.Subtitles.Select(p => p.Text));
        Assert.All(vm.Subtitles, p => Assert.True(string.IsNullOrEmpty(p.Bookmark)));

        vm.UndoCommand.Execute(null);
        await media.WaitIdle();
        Assert.Equal(before, vm.GetUndoRedoHash());
        Assert.Equal(cut.SynchronousAudio.RenderBaselineFileName, media.Player.FileName);
        Assert.NotEqual(media.Source, media.Player.FileName);
        Assert.Equal(File.ReadAllBytes(media.Source), File.ReadAllBytes(media.Player.FileName));
        Assert.Equal(2, vm.Subtitles.Count);
        vm.RedoCommand.Execute(null);
        await media.WaitIdle();
        Assert.Equal(cut.Hash, vm.GetUndoRedoHash());
        Assert.Equal(cut.SynchronousAudio.FileName, media.Player.FileName);
        Assert.Equal(2, vm.Subtitles.Count);
        Assert.Equal(3, media.Loads);
    }

    [AvaloniaFact]
    public async Task RepeatedCutsKeepOnlyBaselineAndCurrentWorkAudioAndRebuildHistoryOnDemand()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;

        await media.Cut();
        var first = Assert.IsType<SynchronousAudioState>(vm.MakeUndoRedoObject("first cut").SynchronousAudio);
        var firstHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first.FileName)));
        Assert.False(string.IsNullOrWhiteSpace(first.RenderBaselineFileName));
        Assert.True(File.Exists(first.RenderBaselineFileName));
        Assert.Equal(2, Directory.EnumerateFiles(media.Work, "*.wav", SearchOption.AllDirectories).Count());

        vm.IsSynchronousAudioRangeSelectionEnabled = true;
        vm.AudioVisualizer!.AudioRangeSelection = new SubtitleLineViewModel(
            new Paragraph("", 500, 1000), vm.SelectedSubtitleFormat);
        await media.Cut();
        var second = Assert.IsType<SynchronousAudioState>(vm.MakeUndoRedoObject("second cut").SynchronousAudio);
        var secondHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(second.FileName)));
        Assert.False(File.Exists(first.FileName));
        Assert.True(File.Exists(second.FileName));
        Assert.True(File.Exists(second.RenderBaselineFileName));
        Assert.Equal(2, Directory.EnumerateFiles(media.Work, "*.wav", SearchOption.AllDirectories).Count());

        vm.UndoCommand.Execute(null);
        await media.WaitIdle();
        Assert.True(File.Exists(first.FileName));
        Assert.False(File.Exists(second.FileName));
        Assert.Equal(firstHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first.FileName))));
        Assert.Equal(first.FileName, media.Player.FileName);
        Assert.Equal(2, Directory.EnumerateFiles(media.Work, "*.wav", SearchOption.AllDirectories).Count());

        vm.RedoCommand.Execute(null);
        await media.WaitIdle();
        Assert.False(File.Exists(first.FileName));
        Assert.True(File.Exists(second.FileName));
        Assert.Equal(secondHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(second.FileName))));
        Assert.Equal(second.FileName, media.Player.FileName);
        Assert.Equal(2, Directory.EnumerateFiles(media.Work, "*.wav", SearchOption.AllDirectories).Count());
        Assert.Equal(File.ReadAllBytes(media.Source), File.ReadAllBytes(second.RenderBaselineFileName!));
    }

    [AvaloniaFact]
    public async Task PostPopFailureRestoresRealHostDocumentMediaAndBothStacks()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        await media.Cut();
        var vm = host.Vm;
        var before = vm.MakeUndoRedoObject("before failed undo");
        var undoHashes = host.History.UndoList.Select(s => s.Hash).ToArray();
        var redoHashes = host.History.RedoList.Select(s => s.Hash).ToArray();
        vm.SynchronousAudioAfterHistoryPop = () => throw new IOException("injected post-pop failure");
        vm.UndoCommand.Execute(null);
        await media.WaitIdle();
        await media.DismissErrors();
        Assert.Equal(before.Hash, vm.GetUndoRedoHash());
        Assert.Equal(before.SynchronousAudio!.FileName, media.Player.FileName);
        Assert.Equal(undoHashes, host.History.UndoList.Select(s => s.Hash));
        Assert.Equal(redoHashes, host.History.RedoList.Select(s => s.Hash));
        Assert.True(media.Window.IsEnabled);
        var recovery = File.ReadAllText(Path.Combine(media.Work, "current.syncaudio.json"));
        Assert.Contains(before.SynchronousAudio.RevisionId, recovery);
    }

    [AvaloniaFact]
    public async Task CancelDuringTargetLoadRollsBackWithoutCreatingACutEntry()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var before = host.Vm.GetUndoRedoHash();
        media.CancelNextLoad = true;
        await media.Cut(expectSuccess: false);
        Assert.Equal(before, host.Vm.GetUndoRedoHash());
        Assert.Null(host.Vm.MakeUndoRedoObject("after").SynchronousAudio);
        Assert.Equal(media.Source, media.Player.FileName);
        Assert.Single(host.History.UndoList);
        Assert.Empty(host.History.RedoList);
    }

    [AvaloniaFact]
    public async Task GuardRejectsChangedBytesAndPreventsWritesUntilDisposed()
    {
        var file = Path.Combine(Path.GetTempPath(), "se-guard-" + Guid.NewGuid().ToString("N") + ".wav");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        try
        {
            var state = new SynchronousAudioState(file, Convert.ToHexString(SHA256.HashData([1, 2, 3])), 0, "r", "s");
            using (await MainViewModel.OpenSynchronousAudioGuardAsync(state, CancellationToken.None))
                Assert.Throws<IOException>(() => { using var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
            await File.WriteAllBytesAsync(file, [9, 8, 7]);
            await Assert.ThrowsAsync<IOException>(() => MainViewModel.OpenSynchronousAudioGuardAsync(state, CancellationToken.None));
        }
        finally { File.Delete(file); }
    }

    [AvaloniaFact]
    public void BinaryCheckpointsAreRejectedAndPreviewPositionIsClamped()
    {
        Assert.Throws<InvalidOperationException>(() => MainViewModel.EnsureSynchronousAudioTextFormat(new Cavena890()));
        MainViewModel.EnsureSynchronousAudioTextFormat(new SubRip());
        Assert.Equal(0, MainViewModel.GetSynchronousAudioPreviewPosition(1));
        Assert.Equal(4, MainViewModel.GetSynchronousAudioPreviewPosition(6));

        var legacyV1 = System.Text.Json.JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Kind = "se-synchronous-audio",
            Audio = new { FileName = Path.GetFullPath("legacy.wav"), Sha256 = new string('a', 64), PositionSeconds = 0d, RevisionId = "", SessionId = "legacy" },
            SubtitleFormat = "SubRip",
            SubtitleFileName = "legacy.srt",
            SubtitleNative = "",
            SubtitleCount = 0,
            Bookmarks = Array.Empty<string?>(),
            CutPositionSeconds = 0d
        });
        Assert.Equal(1, MainViewModel.ReadAudioCheckpointSchemaVersionForTest(legacyV1));
        var newer = System.Text.Json.Nodes.JsonNode.Parse(legacyV1)!;
        newer["SchemaVersion"] = 2;
        Assert.Throws<InvalidDataException>(() => MainViewModel.ReadAudioCheckpointSchemaVersionForTest(newer.ToJsonString()));
    }
    [AvaloniaFact]
    public void PersonalPanelAttachesWithoutChangingTheOfficialShortcutTable()
    {
        using var host = new Host();
        var vm = host.Vm;
        vm.AudioVisualizer = new AudioVisualizer();
        var footer = new Border();
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(footer, 1);
        grid.Children.Add(vm.AudioVisualizer);
        grid.Children.Add(footer);

        Assert.Same(grid, ReversibleAudioSyncUi.Attach(grid, vm));
        Assert.Equal(2, grid.RowDefinitions.Count);
        Assert.Equal(1, Grid.GetRow(footer));
        Assert.Same(grid,vm.AudioVisualizer.Parent);
        var root=vm.AudioVisualizer.MenuFlyout.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"音訊剪修"));
        Assert.Contains(root.Items.OfType<MenuItem>(), c => Equals(c.Header, "音訊區間選取（可跨字幕）"));
        Assert.Contains(root.Items.OfType<MenuItem>(), b => ReferenceEquals(b.Command, vm.SynchronousAudioCutCommand));
        Assert.Contains(root.Items.OfType<MenuItem>(), b => ReferenceEquals(b.Command, vm.SynchronousAudioOpenInAuditionCommand));
        var cancelDirect = root.Items.OfType<MenuItem>().Single(i =>
            Equals(i.Header, "取消本次 AU 修音（保留副本）"));
        Assert.Same(vm.SynchronousAudioCancelAuditionCommand, cancelDirect.Command);
        Assert.False(cancelDirect.IsVisible);
        Assert.Contains(root.Items.OfType<MenuItem>(), i => Equals(i.Header,"驗證目前音檔…") &&
            ReferenceEquals(i.Command, vm.SynchronousAudioVerifyCommand));
        Assert.Contains(root.Items.OfType<MenuItem>(), i => Equals(i.Header,"輸出配對 WAV 與字幕…") &&
            ReferenceEquals(i.Command, vm.SynchronousAudioExportCommand));
        var review = root.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"變更點巡聽"));
        Assert.Contains(review.Items.OfType<MenuItem>(), i => ReferenceEquals(i.Command, vm.SynchronousAudioPreviousChangeCommand));
        Assert.Contains(review.Items.OfType<MenuItem>(), i => ReferenceEquals(i.Command, vm.SynchronousAudioNextChangeCommand));
        var project = root.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"專案與歷史"));
        Assert.DoesNotContain(project.Items.OfType<MenuItem>(), i => ReferenceEquals(i.Command, vm.SynchronousAudioVerifyCommand));
        var audition = root.Items.OfType<MenuItem>().Single(i=>Equals(i.Header,"AU 設定與恢復"));
        var auditionHeaders = audition.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToArray();
        Assert.Equal(new[]
        {
            "接回先前 AU 修音（重啟後）…", "設定 Audition.exe…",
        }, auditionHeaders);

        var shortcuts = ShortcutsMain.GetAllShortcuts(vm);
        Assert.DoesNotContain(shortcuts, s => s.Name == nameof(MainViewModel.SynchronousAudioCutCommand));
        Assert.NotNull(vm.SynchronousAudioCutCommand);
        Assert.NotNull(vm.SynchronousAudioPreviewCommand);
        Assert.NotNull(vm.SynchronousAudioPreviousChangeCommand);
        Assert.NotNull(vm.SynchronousAudioNextChangeCommand);
        Assert.NotNull(vm.SynchronousAudioOpenCommand);
        Assert.NotNull(vm.SynchronousAudioSaveCommand);
        Assert.NotNull(vm.SynchronousAudioVerifyCommand);
        Assert.NotNull(vm.SynchronousAudioExportCommand);
        Assert.NotNull(vm.SynchronousAudioOpenInAuditionCommand);
        Assert.NotNull(vm.SynchronousAudioReloadFromAuditionCommand);
        Assert.NotNull(vm.SynchronousAudioCancelAuditionCommand);
        Assert.NotNull(vm.SynchronousAudioSetAuditionPathCommand);
        Assert.NotNull(vm.UndoCommand);
        Assert.NotNull(vm.RedoCommand);
    }

    [AvaloniaFact]
    public async Task AsyncCommandsWithoutAHostWindowDoNotStartMediaOrMutateHistory()
    {
        using var host = new Host();
        var vm = host.Vm;
        Assert.Null(vm.Window);
        var before = vm.MakeUndoRedoObject("before");
        var history = host.History;
        history.Do(before);

        await vm.SynchronousAudioCutCommand.ExecuteAsync(null);
        await vm.SynchronousAudioOpenCommand.ExecuteAsync(null);
        await vm.SynchronousAudioExportCommand.ExecuteAsync(null);
        await vm.SynchronousAudioOpenInAuditionCommand.ExecuteAsync(null);
        await vm.SynchronousAudioReloadFromAuditionCommand.ExecuteAsync(null);
        await vm.SynchronousAudioSetAuditionPathCommand.ExecuteAsync(null);
        vm.SynchronousAudioCancelAuditionCommand.Execute(null);
        vm.SynchronousAudioPreviewCommand.Execute(null);

        Assert.Equal(before.Hash, vm.GetUndoRedoHash());
        Assert.Single(history.UndoList);
        Assert.Empty(history.RedoList);
        Assert.Null(vm.MakeUndoRedoObject("after").SynchronousAudio);
    }

    [AvaloniaFact]
    public async Task AuditionRoundTripUsesLatestCutAndCreatesUndoableImmutableRevision()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        await media.Cut();
        var cut = vm.MakeUndoRedoObject("cut");
        media.ConfigureAudition();
        media.Player.Position = 5;
        vm.SelectedSubtitleIndex = 1;
        vm.AudioVisualizer!.NewSelectionParagraph = new SubtitleLineViewModel(new Paragraph("", 5000, 5500), vm.SelectedSubtitleFormat);
        var loadsBeforeHandoff = media.Loads;
        await vm.SynchronousAudioOpenInAuditionCommand.ExecuteAsync(null);
        var files = Assert.IsType<AuditionHandoff>(vm.PendingAuditionFiles);
        Assert.Equal(6, files.Timeline.DurationSeconds); // original was eight seconds
        Assert.Equal(cut.SynchronousAudio!.Sha256, files.BeforeHash);
        Assert.NotNull(files.Region);
        Assert.Equal(5 * 8000, files.Region.StartSample);
        Assert.Equal(3, WaveInfo.Read(files.EditPath).DurationSeconds);
        Assert.NotEqual(files.EditPath, media.Player.FileName);
        Assert.Equal(files.BeforePath, media.Player.FileName);
        Assert.Equal(cut.SynchronousAudio.FileName, files.BeforePath);
        Assert.Equal(loadsBeforeHandoff, media.Loads); // unchanged validated WAV stays loaded
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(files.EditPath)!, "before.wav")));
        Assert.Equal(cut.Hash, vm.GetUndoRedoHash());
        var pendingRegion=Assert.Single(vm.DisplayAudioEditRegions,r=>r.Kind==AudioEditRegionKind.AuditionPending);
        Assert.Equal(7,pendingRegion.StartSeconds);Assert.Equal(7.5,pendingRegion.EndSeconds);
        Assert.Equal("● 音檔編修中｜AU 待接回（可取消） ▾", vm.SynchronousAudioProjectStatus);
        Assert.Equal(AudioSessionMode.AuditionPending, vm.SynchronousAudioMode);

        // Simulate AU saving while SE retains a read lock on its own playback snapshot.
        using (var playerLock = File.Open(files.BeforePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var auWriter = File.Open(files.EditPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            auWriter.Position = 44 + (files.Region.LocalStartSample + files.Region.RepairSamples / 2) * 2;
            auWriter.WriteByte(1);
        }
        var replacement = files.EditPath + ".save";
        File.Copy(files.EditPath, replacement);
        File.Move(replacement, files.EditPath, true); // AU's atomic replace-save is also unblocked
        Assert.Equal(cut.Hash, vm.GetUndoRedoHash()); // saving alone never auto-imports
        vm.Subtitles[0].Text = "交接期間尚未存檔的字幕";
        var subtitles = vm.Subtitles.Select(p => (p.Text, p.StartTime, p.EndTime)).ToArray();
        using (var auStillOpen = File.Open(files.EditPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            await vm.SynchronousAudioReloadFromAuditionCommand.ExecuteAsync(null);
        Assert.Null(vm.PendingAuditionFiles);
        Assert.False(vm.IsAuditionHandoffPending);
        Assert.Equal("● 音檔編修中 ▾", vm.SynchronousAudioProjectStatus);
        Assert.Equal(3, media.Player.Position);
        Assert.True(media.Player.IsPlaying);
        var repaired = vm.MakeUndoRedoObject("repaired");
        Assert.NotEqual(cut.SynchronousAudio.RevisionId, repaired.SynchronousAudio!.RevisionId);
        Assert.NotEqual(files.EditPath, repaired.SynchronousAudio.FileName);
        Assert.False(File.Exists(files.BeforePath));
        var patch = Assert.Single(AudioDeltaStore.ReadPatches(repaired.SynchronousAudio.AudioRepairPatchesJson));
        var patchPath = Path.Combine(media.Work, patch.RelativeFileName);
        Assert.True(File.Exists(patchPath));
        Assert.True(new FileInfo(patchPath).Length < new FileInfo(repaired.SynchronousAudio.FileName).Length);
        Assert.DoesNotContain(Directory.EnumerateFiles(media.Work, "*.wav", SearchOption.AllDirectories), path =>
            Path.GetFileName(path).StartsWith("repaired-", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).StartsWith("normalized-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(subtitles, vm.Subtitles.Select(p => (p.Text, p.StartTime, p.EndTime)));
        var savedHash = repaired.SynchronousAudio.Sha256;
        Assert.NotEqual(files.BeforeHash, savedHash);
        var repairRegion=Assert.Single(vm.DisplayAudioEditRegions,r=>r.Kind==AudioEditRegionKind.AuditionRepair);
        Assert.Equal(7,repairRegion.StartSeconds);Assert.Equal(7.5,repairRegion.EndSeconds);
        Assert.Equal(repaired.SynchronousAudio.RevisionId,repairRegion.Id);
        Assert.Equal(files.Timeline, WaveInfo.Read(repaired.SynchronousAudio.FileName));
        // Export the exact post-cut/post-AU host subtitle state, not the original source SRT.
        Assert.Equal(TimeSpan.FromSeconds(4), vm.Subtitles.Single(p => p.Text == "after").StartTime); // original 6s minus 2s cut; crossing line splits
        var exportSubtitle = new Subtitle();
        exportSubtitle.Paragraphs.AddRange(vm.Subtitles.Select(p => p.ToParagraph(vm.SelectedSubtitleFormat)));
        var exportText = exportSubtitle.ToText(new SubRip());
        var audioState = repaired.SynchronousAudio;
        var map = System.Text.Json.JsonSerializer.Deserialize<SourceSpan[]>(audioState.TimelineMapJson!)!;
        var exported = await new AudioTimelineService(new WorkflowSettings()).ExportPairAsync(new AudioExportRequest(
            audioState.FileName, Path.Combine(media.Work, "audit-export"), exportText, ".srt", savedHash,
            SubtitleTimeline: vm.Subtitles.Select(p => new SubtitleInterval(p.StartTime.TotalSeconds, p.EndTime.TotalSeconds)).ToArray(),
            ExpectedSampleCount: TimelineMap.Validate(map), ExpectedSampleRate: audioState.TimelineSampleRate), CancellationToken.None);
        Assert.Equal(exportText, File.ReadAllText(exported.SubtitlePath));
        Assert.Equal(File.ReadAllBytes(audioState.FileName), File.ReadAllBytes(exported.AudioPath));
        Assert.Equal(6, exported.DurationSeconds);
        using (var au = File.OpenWrite(files.EditPath)) { au.Position = au.Length - 1; au.WriteByte(2); }
        Assert.Equal(savedHash, AudioFileWatcher.Fingerprint(repaired.SynchronousAudio.FileName).Hash);

        vm.UndoCommand.Execute(null);
        await media.WaitIdle();
        Assert.Equal(files.BeforeHash, vm.MakeUndoRedoObject("undo").SynchronousAudio!.Sha256);
        Assert.Equal(files.BeforeHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(files.BeforePath))));
        Assert.False(File.Exists(repaired.SynchronousAudio.FileName));
        Assert.Equal(files.BeforePath, media.Player.FileName);
        Assert.Equal(subtitles, vm.Subtitles.Select(p => (p.Text, p.StartTime, p.EndTime)));
        Assert.DoesNotContain(vm.DisplayAudioEditRegions,r=>r.Kind==AudioEditRegionKind.AuditionRepair);
        vm.RedoCommand.Execute(null);
        await media.WaitIdle();
        Assert.Equal(savedHash, vm.MakeUndoRedoObject("redo").SynchronousAudio!.Sha256);
        Assert.Equal(savedHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(repaired.SynchronousAudio.FileName))));
        Assert.False(File.Exists(files.BeforePath));
        Assert.Single(vm.DisplayAudioEditRegions,r=>r.Kind==AudioEditRegionKind.AuditionRepair);
    }

    [AvaloniaFact]
    public async Task AuditionRejectsTimelineChangeAndSupportsRetryCancelAndPendingGuards()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        media.ConfigureAudition();
        await vm.SynchronousAudioOpenInAuditionCommand.ExecuteAsync(null);
        var files = vm.PendingAuditionFiles!;
        var hash = vm.GetUndoRedoHash();
        var loads = media.Loads;
        await vm.SynchronousAudioOpenInAuditionCommand.ExecuteAsync(null);
        await vm.SynchronousAudioCutCommand.ExecuteAsync(null); // no confirmation or cut while pending
        vm.UndoCommand.Execute(null);
        await media.WaitIdle();
        Assert.Equal(loads, media.Loads);
        Assert.Equal(hash, vm.GetUndoRedoHash());
        Assert.Same(files, vm.PendingAuditionFiles);
        // Valid WAV with a different sample count, not merely a broken header.
        var bytes = await File.ReadAllBytesAsync(files.EditPath);
        Array.Resize(ref bytes, bytes.Length - 2);
        BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
        BitConverter.GetBytes(bytes.Length - 44).CopyTo(bytes, 40);
        await File.WriteAllBytesAsync(files.EditPath, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => vm.ApplyAuditionReturnAsync(CancellationToken.None));
        Assert.Same(files, vm.PendingAuditionFiles);
        Assert.Equal(hash, vm.GetUndoRedoHash());
        Assert.Equal(files.BeforePath, media.Player.FileName);
        vm.SynchronousAudioCancelAuditionCommand.Execute(null);
        Assert.Null(vm.PendingAuditionFiles);
        Assert.True(File.Exists(files.EditPath));
        Assert.True(media.Player.IsPlaying);
    }

    [AvaloniaFact]
    public async Task AuditionLoadFailureRollsBackAndKeepsRepairAvailableForRetry()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        media.ConfigureAudition();
        await vm.SynchronousAudioOpenInAuditionCommand.ExecuteAsync(null);
        var files = vm.PendingAuditionFiles!;
        vm.Subtitles[0].Text = "不要遺失";
        var hash = vm.GetUndoRedoHash();
        var history = host.History.UndoList.Select(i => i.Hash).ToArray();
        media.CancelNextLoad = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => vm.ApplyAuditionReturnAsync(CancellationToken.None));
        Assert.Equal(hash, vm.GetUndoRedoHash());
        Assert.Equal(history, host.History.UndoList.Select(i => i.Hash));
        Assert.Equal(files.BeforePath, media.Player.FileName);
        Assert.Same(files, vm.PendingAuditionFiles);
        await vm.SynchronousAudioReloadFromAuditionCommand.ExecuteAsync(null);
        Assert.Null(vm.PendingAuditionFiles);
        Assert.Equal("不要遺失", vm.Subtitles[0].Text);
    }

    [AvaloniaFact]
    public async Task LocalAuditionFallsBackToSelectedSubtitleAndClampsContextAtFileEdges()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        media.ConfigureAudition();
        vm.AudioVisualizer!.NewSelectionParagraph = null;
        vm.SelectedSubtitleIndex = 1; // 6-7 s in the 8-second current audio
        Assert.Equal((6d, 7d), vm.GetAuditionLocalRange());
        await vm.SynchronousAudioOpenInAuditionCommand.ExecuteAsync(null);
        var files = vm.PendingAuditionFiles!;
        Assert.Equal(4, WaveInfo.Read(files.EditPath).DurationSeconds);
        Assert.Equal(4 * 8000, files.Region!.ClipStartSample);
        Assert.Equal(8 * 8000, files.Region.ClipEndSample);
        Assert.Equal(2 * 8000, files.Region.LocalStartSample);
        var pending = Assert.Single(vm.DisplayAudioEditRegions,
            region => region.Kind == AudioEditRegionKind.AuditionPending);
        vm.SelectAudioEditRegion(pending);
        await vm.SynchronousAudioCancelSelectedRegionCommand.ExecuteAsync(null);
        Assert.Null(vm.PendingAuditionFiles);
        Assert.DoesNotContain(vm.DisplayAudioEditRegions,
            region => region.Kind == AudioEditRegionKind.AuditionPending);
        Assert.Equal("● 音檔編修中 ▾", vm.SynchronousAudioProjectStatus);
        Assert.Equal(4, media.Player.Position);
        vm.SelectedSubtitleIndex = null;
        Assert.Throws<InvalidOperationException>(() => vm.GetAuditionLocalRange());
    }

    [AvaloniaFact]
    public async Task RevisionsPreserveHistoryWhileNormalSaveWritesCurrentSrtAndRestoreBothTimelines()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        var sourceSrt = Path.Combine(media.Work, "source.srt");
        Directory.CreateDirectory(media.Work);
        File.WriteAllText(sourceSrt, "source sentinel");
        SetField(vm, "_subtitleFileName", sourceSrt);
        SetField(vm, "_lastOpenSaveFormat", vm.SelectedSubtitleFormat);
        await media.Cut();
        var cut = vm.MakeUndoRedoObject("cut");
        Assert.NotNull(cut.SynchronousAudio!.TimelineMapJson);
        var revisions = Path.Combine(media.Work, "revisions", cut.SynchronousAudio.SessionId);
        var prior = Directory.GetDirectories(revisions).SelectMany(p => Directory.GetFiles(p)).ToDictionary(p => p, File.ReadAllBytes);
        vm.Subtitles[0].StartTime = TimeSpan.FromSeconds(0.7);
        var save = (Task<bool>)typeof(MainViewModel).GetMethod("SaveSubtitle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { false })!;
        Assert.True(await save);
        var savedSrt = File.ReadAllText(sourceSrt);
        Assert.Contains("00:00:00,700", savedSrt);
        Assert.Contains("crossing", savedSrt);
        Assert.All(prior, item => Assert.Equal(item.Value, File.ReadAllBytes(item.Key)));
        Assert.True(Directory.GetDirectories(revisions).Length >= 2);
        await vm.SynchronousAudioOriginalTimelineCommand.ExecuteAsync(null);
        Assert.Equal(8, media.Player.Duration);
        Assert.Equal(TimeSpan.FromSeconds(1), vm.Subtitles[0].StartTime);
        Assert.NotEqual(media.Source, media.Player.FileName);
        await vm.SynchronousAudioEditedTimelineCommand.ExecuteAsync(null);
        Assert.Equal(6, media.Player.Duration);
        Assert.Equal(TimeSpan.FromSeconds(0.7), vm.Subtitles[0].StartTime);
        Assert.Equal(cut.SynchronousAudio.Sha256, vm.MakeUndoRedoObject("restored").SynchronousAudio!.Sha256);
    }

    [AvaloniaFact]
    public void RecentAudioResolutionIsBoundedAndIncludesNestedAuMedia()
    {
        var root = Path.Combine(Path.GetTempPath(), "recent-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid().ToString("N");
        var checkpoint = Path.Combine(root, id, "current.syncaudio.json");
        Assert.Equal(checkpoint, MainViewModel.FindManagedAudioCheckpoint(Path.Combine(root, id, "cut.wav"), root));
        Assert.Equal(checkpoint, MainViewModel.FindManagedAudioCheckpoint(Path.Combine(root, id, "audition", "repair.wav"), root));
        Assert.Null(MainViewModel.FindManagedAudioCheckpoint(Path.Combine(root + "-other", id, "cut.wav"), root));
        Assert.Null(MainViewModel.FindManagedAudioCheckpoint(null, root));
        Assert.Throws<InvalidDataException>(() => MainViewModel.FindManagedAudioCheckpoint(Path.Combine(root, "unknown", "cut.wav"), root));
    }

    [AvaloniaFact]
    public async Task ReopenRestoresSavedEditedTimesInsteadOfSourceSrtEvenWhenSourceIsMissing()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        var sourceSrt = Path.Combine(media.Work, "missing-source.srt");
        SetField(vm, "_subtitleFileName", sourceSrt);
        await media.Cut();
        vm.SynchronousAudioSaveCommand.Execute(null);
        var expected = vm.Subtitles.Select(p => (p.Text, p.StartTime, p.EndTime)).ToArray();
        var expectedAudio = vm.MakeUndoRedoObject("saved").SynchronousAudio!;
        vm.Subtitles[1].StartTime = TimeSpan.FromSeconds(6); // stale source timing
        SetField(vm, "_synchronousAudioState", null); // restarted/unmanaged host
        await vm.SubtitleOpen(sourceSrt, expectedAudio.FileName);
        Assert.Equal(expected, vm.Subtitles.Select(p => (p.Text, p.StartTime, p.EndTime)));
        Assert.Equal(expectedAudio.Sha256, vm.MakeUndoRedoObject("restored").SynchronousAudio!.Sha256);
        Assert.Equal(6, media.Player.Duration);
        Assert.False(File.Exists(sourceSrt));
    }

    [AvaloniaFact]
    public async Task ReopenRejectsWrongProjectOrCorruptRevisionWithoutChangingTheCurrentPair()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        var sourceSrt = Path.Combine(media.Work, "source.srt");
        SetField(vm, "_subtitleFileName", sourceSrt);
        await media.Cut();
        var checkpoint = Path.Combine(media.Work, "current.syncaudio.json");
        var before = vm.GetUndoRedoHash();
        var audio = media.Player.FileName;
        await Assert.ThrowsAsync<InvalidDataException>(() => vm.RestoreAudioCheckpointAsync(checkpoint, sourceSrt + ".wrong"));
        var revision = (string)typeof(MainViewModel).GetField("_lastRevisionDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        File.AppendAllText(Path.Combine(revision, "subtitles.srt"), "tampered");
        await Assert.ThrowsAsync<InvalidDataException>(() => vm.RestoreAudioCheckpointAsync(checkpoint, sourceSrt));
        Assert.Equal(before, vm.GetUndoRedoHash());
        Assert.Equal(audio, media.Player.FileName);
    }

    [AvaloniaFact]
    public async Task Audit_RestoreMustRetainLastSavedListeningPosition()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        await media.Cut();
        media.Player.Position = 1;
        vm.SynchronousAudioSaveCommand.Execute(null);
        media.Player.Position = 5;
        vm.SynchronousAudioSaveCommand.Execute(null);
        await vm.RestoreAudioCheckpointAsync(Path.Combine(media.Work, "current.syncaudio.json"));
        Assert.Equal(5, media.Player.Position);
    }

    [AvaloniaFact]
    public async Task Audit_RestoreMustResetRevisionParentToTheLoadedProject()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        await media.Cut();
        var field = typeof(MainViewModel).GetField("_lastRevisionDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var expected = (string)field.GetValue(vm)!;
        field.SetValue(vm, null); // fresh launch has no in-memory revision parent
        await vm.RestoreAudioCheckpointAsync(Path.Combine(media.Work, "current.syncaudio.json"));
        Assert.Equal(expected, field.GetValue(vm));
        vm.Subtitles[0].Text = "new revision after reopen";
        vm.SynchronousAudioSaveCommand.Execute(null);
        var next = (string)field.GetValue(vm)!;
        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(next, "revision.json")));
        Assert.Equal(Path.GetFileName(expected), manifest.RootElement.GetProperty("ParentId").GetString());
    }

    [AvaloniaFact]
    public async Task RestoreRejectsOutOfBoundsListeningPositionWithoutLoadingMedia()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        await media.Cut();
        var file = Path.Combine(media.Work, "current.syncaudio.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        node["audio"]!["positionSeconds"] = 999;
        File.WriteAllText(file, node.ToJsonString());
        var loads = media.Loads;
        await Assert.ThrowsAsync<InvalidDataException>(() => vm.RestoreAudioCheckpointAsync(file));
        Assert.Equal(loads, media.Loads);
    }

    [AvaloniaFact]
    public async Task BeginProjectOwnsBaselineAndAutosaveVersionsTimingOnlyChanges()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        Directory.CreateDirectory(media.Work);
        var subtitleFile = Path.Combine(media.Work, "current.srt");
        File.WriteAllText(subtitleFile, "stale subtitle file");
        SetField(vm, "_subtitleFileName", subtitleFile);
        SetField(vm, "_lastOpenSaveFormat", vm.SelectedSubtitleFormat);

        await vm.SynchronousAudioBeginProjectCommand.ExecuteAsync(null);
        var state = vm.MakeUndoRedoObject("baseline").SynchronousAudio!;
        Assert.NotNull(state);
        Assert.NotEqual(media.Source, state.FileName);
        Assert.Equal(File.ReadAllBytes(media.Source), File.ReadAllBytes(state.FileName));
        var revisions = Path.Combine(media.Work, "revisions", state.SessionId);
        var old = Directory.GetDirectories(revisions).SelectMany(p => Directory.GetFiles(p)).ToDictionary(p => p, File.ReadAllBytes);

        vm.Subtitles[1].Text = "saved through normal SE path";
        vm.Subtitles[1].EndTime = TimeSpan.FromSeconds(7.5);
        var auto = (Task<bool>)typeof(MainViewModel).GetMethod("SaveSubtitle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { true })!;

        Assert.True(await auto);
        var savedText = File.ReadAllText(subtitleFile);
        Assert.Contains("saved through normal SE path", savedText);
        Assert.Contains("00:00:07,500", savedText);
        Assert.All(old, item => Assert.Equal(item.Value, File.ReadAllBytes(item.Key)));
        Assert.True(Directory.GetDirectories(revisions).Length >= 2);
    }

    [AvaloniaFact]
    public async Task AuditionLaunchFailureDoesNotCommitOrLoseHistory()
    {
        using var host = new Host();
        using var media = new MediaFixture(host);
        var vm = host.Vm;
        media.ConfigureAudition();
        var hash = vm.GetUndoRedoHash();
        vm.AuditionLaunchOverride = (_, _) => throw new IOException("injected launch failure");
        var task = vm.SynchronousAudioOpenInAuditionCommand.ExecuteAsync(null);
        await MediaFixture.PumpUntil(() => media.Window.OwnedWindows.OfType<SeMessageBox>().Any());
        await media.DismissErrors();
        await task;
        Assert.Null(vm.PendingAuditionFiles);
        Assert.Equal(hash, vm.GetUndoRedoHash());
        Assert.Equal(media.Source, media.Player.FileName);
        Assert.Null(vm.MakeUndoRedoObject("after").SynchronousAudio);
        Assert.All(host.History.UndoList, i => Assert.Null(i.SynchronousAudio));
    }

    [AvaloniaFact]
    public void BaselineAndSeekPreserveUndoHashWhileRevisionChangesOnlyUndoIdentity()
    {
        using var host = new Host();
        var vm = host.Vm;
        vm.Subtitles.Add(new SubtitleLineViewModel(new Paragraph("unsaved text", 1000, 5000), vm.SelectedSubtitleFormat));
        var originalHash = vm.GetUndoRedoHash();
        var saveHash = vm.GetFastHash();
        var baseline = new SynchronousAudioState(Path.GetFullPath("baseline.wav"), "sha-before", 2, "", "session");
        SetState(vm, baseline);
        Assert.Equal(originalHash, vm.GetUndoRedoHash());
        Assert.Equal(baseline, vm.MakeUndoRedoObject("baseline").SynchronousAudio);

        SetState(vm, baseline with { PositionSeconds = 50 });
        Assert.Equal(originalHash, vm.GetUndoRedoHash());
        var cut = baseline with { FileName = Path.GetFullPath("cut.wav"), Sha256 = "sha-after", RevisionId = "revision" };
        SetState(vm, cut);
        Assert.NotEqual(originalHash, vm.GetUndoRedoHash());
        Assert.Equal(saveHash, vm.GetFastHash());
        Assert.Equal(cut, UndoRedoItem.Clone(vm.MakeUndoRedoObject("cut"))!.SynchronousAudio);
    }

    [AvaloniaFact]
    public void NativeCutterMapsCrossingRowsAndBookmarksWithoutMutatingInput()
    {
        var subtitle = new Subtitle();
        subtitle.Paragraphs.Add(new Paragraph("before", 0, 1000));
        subtitle.Paragraphs.Add(new Paragraph("crossing", 1000, 5000) { Bookmark = "review splice" });
        subtitle.Paragraphs.Add(new Paragraph("removed", 2500, 3000));
        subtitle.Paragraphs.Add(new Paragraph("after", 6000, 7000));

        var cut = SubtitleSegmentCutter.RemoveSegments(subtitle, [(2d, 4d)], 8d);

        Assert.Equal(new[] { "before", "crossing", "crossing", "after" }, cut.Paragraphs.Select(p => p.Text));
        Assert.Equal(new[] { 0d, 1000d, 2000d, 4000d }, cut.Paragraphs.Select(p => p.StartTime.TotalMilliseconds));
        Assert.Equal(new[] { 1000d, 2000d, 3000d, 5000d }, cut.Paragraphs.Select(p => p.EndTime.TotalMilliseconds));
        Assert.Equal("review splice", cut.Paragraphs[1].Bookmark);
        Assert.Equal("review splice", cut.Paragraphs[2].Bookmark);
        Assert.Equal(4, subtitle.Paragraphs.Count);
        Assert.Equal(5000d, subtitle.Paragraphs[1].EndTime.TotalMilliseconds);
        Assert.Equal("removed", subtitle.Paragraphs[2].Text);
        Assert.NotSame(subtitle.Paragraphs[0], cut.Paragraphs[0]);
    }

    [AvaloniaFact]
    public void RealHostSnapshotsAndManagerCheckpointPreserveMediaAndUnsavedText()
    {
        using var host = new Host();
        var vm = host.Vm;
        vm.Subtitles.Add(new SubtitleLineViewModel(new Paragraph("before", 1000, 5000), vm.SelectedSubtitleFormat));
        var baseline = new SynchronousAudioState(Path.GetFullPath("baseline.wav"), "sha-before", 2, "", "session");
        SetState(vm, baseline);
        host.History.Do(vm.MakeUndoRedoObject("baseline"));
        SetState(vm, baseline with { RevisionId = "cut", FileName = Path.GetFullPath("cut.wav") });
        vm.Subtitles[0].Text = "cut plus unsaved text";
        var live = vm.MakeUndoRedoObject("cut");
        host.History.Do(live);
        var checkpoint = host.History.CaptureCheckpoint();

        Assert.Equal(baseline, host.History.PeekUndo()!.SynchronousAudio);
        host.History.Undo(); // simulate a pop followed by a failed host apply
        host.History.RestoreCheckpoint(checkpoint);

        Assert.Equal(2, host.History.UndoCount);
        Assert.Empty(host.History.RedoList);
        Assert.Equal(live.Hash, host.History.UndoList[^1].Hash);
        Assert.Equal(live.SynchronousAudio, host.History.UndoList[^1].SynchronousAudio);
        Assert.Equal("cut plus unsaved text", host.History.UndoList[^1].Subtitles[0].Text);
        Assert.Equal(live.Hash, vm.GetUndoRedoHash());
    }

    private static void SetState(MainViewModel vm, SynchronousAudioState state) =>
        typeof(MainViewModel).GetField("_synchronousAudioState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, state);

    private static void SetField(MainViewModel vm, string name, object? value) =>
        typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);

    private sealed class MediaFixture : IDisposable
    {
        private readonly Host _host;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "se-sync-host-" + Guid.NewGuid().ToString("N"));
        private readonly VideoPlayerControl _control;
        public Window Window { get; }
        public FakePlayer Player { get; } = new();
        public string Source { get; }
        public string Work { get; }
        public int Loads { get; private set; }
        public bool CancelNextLoad { get; set; }

        public MediaFixture(Host host)
        {
            _host = host;
            Directory.CreateDirectory(Path.Combine(_root, "Working"));
            Source = Path.Combine(_root, "Working", "test_WORK.wav");
            Work = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            WriteWave(Source);
            var vm = host.Vm;
            vm.RecentAudioWorkRootOverride = _root;
            vm.SelectedSubtitleFormat = new SubRip();
            vm.Subtitles.Add(new SubtitleLineViewModel(new Paragraph("crossing", 1000, 5000), vm.SelectedSubtitleFormat));
            vm.Subtitles.Add(new SubtitleLineViewModel(new Paragraph("after", 6000, 7000), vm.SelectedSubtitleFormat));
            vm.SubtitleGrid.ItemsSource = vm.Subtitles;
            vm.AudioVisualizer = new AudioVisualizer { NewSelectionParagraph = new SubtitleLineViewModel(new Paragraph("", 2000, 4000), vm.SelectedSubtitleFormat) };
            _control = new VideoPlayerControl(Player);
            vm.VideoPlayerControl = _control;
            Player.LoadFile(Source).GetAwaiter().GetResult();
            SetField(vm, "_videoFileName", Source);
            SetField(vm, "_synchronousAudioDirectory", Work);
            Window = new Window { Width = 1000, Height = 700, Content = new StackPanel { Children = { vm.SubtitleGrid, _control, vm.AudioVisualizer } } };
            vm.Window = Window;
            Window.Show();
            Dispatcher.UIThread.RunJobs();
            var initial=vm.MakeUndoRedoObject("initial");
            host.History.Do(initial);
            Assert.Equal(initial.Hash,host.History.LatestUndoHash);
            vm.SynchronousAudioMediaLoadOverride = async (state, token) =>
            {
                Loads++;
                _control.Close();
                await _control.Open(state.FileName, state.PositionSeconds);
                if (CancelNextLoad) { CancelNextLoad = false; throw new OperationCanceledException("injected cancellation"); }
                return Player.Duration;
            };
        }

        public void ConfigureAudition()
        {
            var executable = Path.Combine(_root, "Audition.exe");
            File.WriteAllBytes(executable, [1]);
            var manager = new SettingsManager(Path.Combine(_root, "settings"));
            manager.Save(new WorkflowSettings { AuditionPath = executable });
            _host.Vm.AuditionSettingsManagerOverride = manager;
            _host.Vm.AuditionLaunchOverride = (path, _) =>
            {
                Assert.EndsWith("_WORK.wav", path);
                Assert.NotEqual(path, Player.FileName);
            };
        }

        public async Task Cut(bool expectSuccess = true, bool viaDelete = false)
        {
            Task cut;
            if (viaDelete)
            {
                Window.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, _host.Vm.OnKeyDownHandler, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
                Assert.True(_host.Vm.AudioVisualizer!.Focus());
                Window.KeyPress(Avalonia.Input.Key.Delete, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Delete, null);
                cut = _host.Vm.SynchronousAudioCutCommand.ExecutionTask!;
                Assert.NotNull(cut);
            }
            else cut = _host.Vm.SynchronousAudioCutCommand.ExecuteAsync(null);
            await PumpUntil(() => Window.OwnedWindows.OfType<SeMessageBox>().Any());
            Click(Window.OwnedWindows.OfType<SeMessageBox>().Single(), Se.Language.General.Yes);
            await cut.WaitAsync(TimeSpan.FromSeconds(15));
            await DismissErrors();
            if (expectSuccess) Assert.NotNull(_host.Vm.MakeUndoRedoObject("cut").SynchronousAudio);
        }

        public Task WaitIdle() => PumpUntil(() => !(bool)typeof(MainViewModel)
            .GetField("_synchronousAudioBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_host.Vm)!);

        public async Task DismissErrors()
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
            foreach (var box in Window.OwnedWindows.OfType<SeMessageBox>().Where(w => w.IsVisible).ToArray()) Click(box, Se.Language.General.Ok);
            await Task.Yield();
        }

        internal static void Click(Window window, string label)
        {
            Dispatcher.UIThread.RunJobs();
            // MessageBox can be visible before its first headless layout pass. Drive
            // the real button from its panel, not a not-yet-materialized visual tree.
            var panel = (StackPanel)typeof(SeMessageBox).GetField("_buttonPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            static string ButtonText(Button b) => b.Content switch
            {
                AccessText accessText => accessText.Text ?? string.Empty,
                string text => text,
                _ => b.Content?.ToString() ?? string.Empty,
            };
            var button = panel.Children.OfType<Button>().FirstOrDefault(b =>
                string.Equals(ButtonText(b).Replace("_", ""), label.Replace("_", ""), StringComparison.Ordinal));
            Assert.True(button != null, $"Dialog '{window.Title}', expected '{label}', buttons: {string.Join(", ", panel.Children.OfType<Button>().Select(ButtonText))}");
            Assert.NotNull(button);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        public static async Task PumpUntil(Func<bool> done)
        {
            var end = DateTime.UtcNow.AddSeconds(15);
            while (!done() && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
            Assert.True(done(), "headless operation did not settle");
        }

        private static void WriteWave(string file)
        {
            const int rate = 8000, bytes = rate * 8 * 2;
            using var stream = File.Create(file);
            using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
            writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(bytes);
            writer.Write(new byte[bytes]);
        }

        public void Dispose()
        {
            _host.Vm.SynchronousAudioMediaLoadOverride = null;
            _host.Vm.ReleaseAudioRevisionSource();
            _host.Vm.ReleaseSynchronousAudioValidationCache();
            _host.Vm.SynchronousAudioAfterHistoryPop = null;
            _host.Vm.AuditionLaunchOverride = null;
            _host.Vm.AuditionSettingsManagerOverride = null;
            foreach (var owned in Window.OwnedWindows.ToArray()) owned.Close();
            Window.Close();
            _control.Close();
            _host.Vm.Window = null;
            _host.Vm.VideoPlayerControl = null;
            _host.Vm.AudioVisualizer = null;
            Directory.Delete(_root, true);
        }
    }

    private sealed class FakePlayer : IVideoPlayer
    {
        private double _position;
        public string Name => "synchronous audio test player";
        public string FileName { get; private set; } = "";
        public bool CanLoad() => true;
        public Task LoadFile(string fileName, double startPositionSeconds = 0)
        { FileName = fileName; Position = startPositionSeconds; Duration = WaveInfo.Read(fileName).DurationSeconds; return Task.CompletedTask; }
        public void CloseFile() { FileName = ""; Duration = 0; }
        public void Play() => IsPlaying = true;
        public void PlayOrPause() => IsPlaying = !IsPlaying;
        public void Pause() => IsPlaying = false;
        public void Stop() => IsPlaying = false;
        public AudioTrackInfo? ToggleAudioTrack() => null;
        public bool IsPlaying { get; private set; }
        public bool IsPaused => !IsPlaying;
        public int PositionReads { get; private set; }
        public double Position { get { PositionReads++;return _position; } set => _position=value; }
        public void ResetPositionReads()=>PositionReads=0;
        public double Duration { get; private set; }
        public int VolumeMaximum => 100;
        public double Volume { get; set; } = 50;
        public double Speed { get; set; } = 1;
        public bool SupportRestartEvents { get; set; }
        public long? PlaybackRestartTimestamp { get; set; }
        public bool SupportsPlaybackRestartEvents => SupportRestartEvents;
        public bool HasPlaybackRestartedSince(long stopwatchTimestamp) =>
            PlaybackRestartTimestamp is { } timestamp && timestamp > stopwatchTimestamp;
    }

    private sealed class Host : IDisposable
    {
        private readonly IServiceProvider _previousServices = Locator.Services;
        private readonly SettingsScope _settings = new("General.CheckForUpdatesOnStartup");
        private readonly ServiceProvider _services;
        public MainViewModel Vm { get; }
        public IUndoRedoManager History { get; }

        public Host(bool withMainView = false)
        {
            Se.Settings.General.CheckForUpdatesOnStartup = false;
            var services = new ServiceCollection();
            services.AddSubtitleEditServices();
            if (withMainView)
            {
                services.RemoveAll<MainViewModel>();
                services.AddSingleton<MainViewModel>();
            }
            _services = services.BuildServiceProvider();
            Locator.Services = _services;
            Vm = _services.GetRequiredService<MainViewModel>();
            History = (IUndoRedoManager)typeof(MainViewModel)
                .GetField("_undoRedoManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Vm)!;
            History.StopChangeDetection();
        }

        public void Dispose()
        {
            // No MainView/Window lifecycle was started; stop constructor-owned timers
            // without invoking application shutdown or save prompts.
            foreach (var name in new[] { "_positionTimer", "_cursorTimer", "_slowTimer", "_dropDownFormatsSearchTimer" })
            {
                var timer = typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Vm);
                timer?.GetType().GetMethod("Stop", Type.EmptyTypes)?.Invoke(timer, null);
            }
            _services.GetRequiredService<IAutoBackupService>().StopAutobackup();
            History.StopChangeDetection();
            Vm.VideoPlayerControl = null;
            Vm.AudioVisualizer = null;
            Locator.Services = _previousServices;
            _services.Dispose();
            _settings.Dispose();
        }
    }
}
