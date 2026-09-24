using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Nikse.SubtitleEdit.Controls.AudioVisualizerControl;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.Media;

namespace UITests.Controls;

// Regression: a cut interval must start inside a subtitle and cross its neighbours.
// Drive real pointer/key events, not a preconstructed selection injected into the host.
public class AudioRangeSelectionTests
{
    [AvaloniaTheory]
    [InlineData(2, 5)] // inside a cue, across the next cue
    [InlineData(1, 5)] // exactly on the left resize handle
    [InlineData(3, 5)] // exactly on the right resize handle
    [InlineData(5, 2)] // backwards across multiple cues
    [InlineData(.5, 5)] // from a gap across both cues, AllowOverlap remains false
    public void AudioDragCrossesCuesWithoutChangingSubtitles(double from, double to)
    {
        using var fixture = new Fixture();
        fixture.Drag(from, to);
        fixture.AssertRange(Math.Min(from, to), Math.Max(from, to));
        fixture.AssertSubtitlesUnchanged();
        Assert.Null(fixture.Av.NewSelectionParagraph);
        Assert.False(Se.Settings.Waveform.AllowOverlap);
    }

    [AvaloniaFact]
    public void RightClickPreservesTheAudioRangeAndDoesNotSelectOrMoveACue()
    {
        using var fixture = new Fixture();
        var selections = 0;
        fixture.Av.OnSelectRequested += (_, _) => selections++;
        fixture.Drag(2, 5);
        fixture.Av.MenuFlyout.Items.Add(new MenuItem { Header = "test" });
        fixture.Window.MouseDown(Fixture.PointAt(4), MouseButton.Right);
        fixture.Window.MouseUp(Fixture.PointAt(4), MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        fixture.AssertRange(2, 5);
        fixture.AssertSubtitlesUnchanged();
        Assert.Equal(0, selections);
        fixture.Av.MenuFlyout.Hide();
    }

    [AvaloniaTheory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Delete)]
    public void AudioRangeIsNotInsertedOrDeletedAsASubtitle(Key key)
    {
        using var fixture = new Fixture();
        var edits = 0;
        fixture.Av.OnNewSelectionInsert += (_, _) => edits++;
        fixture.Av.OnDeletePressed += (_, _) => edits++;
        fixture.Drag(2, 5);
        fixture.Av.Focus();
        fixture.Window.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, edits);
        fixture.AssertRange(2, 5);
        fixture.AssertSubtitlesUnchanged();
    }

    [AvaloniaFact]
    public void EscapeClearsRangeAndKeepsAudioMode()
    {
        using var fixture = new Fixture();
        fixture.Drag(2, 5);
        fixture.Av.Focus();
        fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(fixture.Av.AudioRangeSelection);
        Assert.True(fixture.Av.IsAudioRangeSelectionMode);
        fixture.AssertSubtitlesUnchanged();
    }

    [AvaloniaFact]
    public void TurningModeOffClearsRangeAndRestoresNativeSubtitleDragging()
    {
        using var fixture = new Fixture();
        fixture.Drag(2, 5);
        fixture.Av.IsAudioRangeSelectionMode = false;
        Assert.Null(fixture.Av.AudioRangeSelection);
        Assert.Null(fixture.Av.NewSelectionParagraph);
        fixture.Drag(2, 2.25);
        Assert.Equal(1.25, fixture.Lines[0].StartTime.TotalSeconds, 2);
        Assert.Equal(3.25, fixture.Lines[0].EndTime.TotalSeconds, 2);
        Assert.Null(fixture.Av.AudioRangeSelection);
    }

    [AvaloniaFact]
    public void AudioRangeClampsAtBothMediaEndsEvenWhenPointerLeavesTheControl()
    {
        using var fixture = new Fixture();
        fixture.Drag(2, -1);
        fixture.AssertRange(0, 2);
        fixture.Drag(5, 9);
        fixture.AssertRange(5, 8);
        fixture.AssertSubtitlesUnchanged();
    }

    [AvaloniaFact]
    public void LockedSubtitleTimesDoNotBlockReadOnlyAudioRangeSelection()
    {
        using var fixture = new Fixture();
        fixture.Av.IsReadOnly = true;
        fixture.Drag(2, 5);
        fixture.AssertRange(2, 5);
        fixture.AssertSubtitlesUnchanged();
    }

    [AvaloniaFact]
    public void ModeOffMidDragCancelsRatherThanRetimingTheCue()
    {
        using var fixture = new Fixture();
        fixture.Window.MouseDown(Fixture.PointAt(2), MouseButton.Left);
        fixture.Window.MouseMove(Fixture.PointAt(4), RawInputModifiers.LeftMouseButton);
        fixture.Av.IsAudioRangeSelectionMode = false;
        fixture.Window.MouseMove(Fixture.PointAt(5), RawInputModifiers.LeftMouseButton);
        fixture.Window.MouseUp(Fixture.PointAt(5), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(fixture.Av.AudioRangeSelection);
        fixture.AssertSubtitlesUnchanged();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly bool _allowOverlap = Se.Settings.Waveform.AllowOverlap;
        public Window Window { get; }
        public AudioVisualizer Av { get; }
        public List<SubtitleLineViewModel> Lines { get; } =
        [
            new() { Text = "cue one", StartTime = TimeSpan.FromSeconds(1), EndTime = TimeSpan.FromSeconds(3) },
            new() { Text = "cue two", StartTime = TimeSpan.FromSeconds(4), EndTime = TimeSpan.FromSeconds(6) },
        ];

        public Fixture()
        {
            Se.Settings.Waveform.AllowOverlap = false;
            Av = new AudioVisualizer
            {
                Width = 1008, Height = 200,
                WavePeaks = new WavePeakData2(126, Enumerable.Repeat(new WavePeak2(8000, -8000), 126 * 8).ToArray()),
                IsAudioRangeSelectionMode = true,
            };
            Av.SetPosition(0, Lines, 0, 0, []);
            Window = new Window { Width = 1008, Height = 200, Content = Av };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public static Point PointAt(double seconds) => new(seconds * 126, 100);
        public void Drag(double from, double to)
        {
            Window.MouseDown(PointAt(from), MouseButton.Left);
            Window.MouseMove(PointAt(to), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            Window.MouseUp(PointAt(to), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
        public void AssertRange(double start, double end)
        {
            Assert.NotNull(Av.AudioRangeSelection);
            Assert.Equal(start, Av.AudioRangeSelection.StartTime.TotalSeconds, 3);
            Assert.Equal(end, Av.AudioRangeSelection.EndTime.TotalSeconds, 3);
        }
        public void AssertSubtitlesUnchanged()
        {
            Assert.Equal(new[] { 1d, 4d }, Lines.Select(p => p.StartTime.TotalSeconds));
            Assert.Equal(new[] { 3d, 6d }, Lines.Select(p => p.EndTime.TotalSeconds));
            Assert.Equal(new[] { "cue one", "cue two" }, Lines.Select(p => p.Text));
        }
        public void Dispose()
        {
            Av.MenuFlyout.Hide();
            Av.IsAudioRangeSelectionMode = false;
            Window.Close();
            Se.Settings.Waveform.AllowOverlap = _allowOverlap;
        }
    }
}
