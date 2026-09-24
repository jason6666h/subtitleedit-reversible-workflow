using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;
using Nikse.SubtitleEdit.Logic.UndoRedo;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private sealed record SynchronousAudioHostSnapshot(
        Subtitle Identity,
        SubtitleFormat Format,
        string MediaFile,
        string? Directory,
        Func<UndoRedoItem, SynchronousAudioState?, Task> RestoreAsync);

    private SynchronousAudioHostSnapshot CaptureSynchronousAudioHostSnapshot()
    {
        var history = _undoRedoManager.CaptureCheckpoint();
        var identity = _subtitle;
        var format = SelectedSubtitleFormat;
        var original = new Subtitle(_subtitleOriginal, generateNewId: false);
        var originalBeforeEdit = _subtitleOriginalBeforeEditMode;
        var originalUi = (ShowColumnOriginalText, IsOriginalReadOnly, IsShowingOriginalNonMatchingLines, IsEditOriginalMode);
        var secondary = (_subtitleSecondary, _subtitleSecondaryFileName, IsSubtitleSecondaryVisible);
        var saveState = (_converted, _saveAsFileNameSuggestion, _changeSubtitleHash, _changeSubtitleHashOriginal);
        var revisionState = (_revisionFingerprint, _lastRevisionDirectory);
        var directory = _synchronousAudioDirectory;
        var cutPosition = _synchronousAudioCutPosition;
        var selection = AudioVisualizer?.NewSelectionParagraph;
        var audioRangeSelection = AudioVisualizer?.AudioRangeSelection;
        var mediaFile = _videoFileName ?? string.Empty;
        var mediaVersion = _synchronousAudioMediaOpenVersion;
        var formatChanging = _changingFormatProgrammatically;

        async Task RestoreAsync(UndoRedoItem before, SynchronousAudioState? rollbackMedia)
        {
            _undoRedoManager.RestoreCheckpoint(history);
            _subtitle = identity;
            _subtitleOriginal = original;
            _subtitleOriginalBeforeEditMode = originalBeforeEdit;
            (_subtitleSecondary, _subtitleSecondaryFileName, IsSubtitleSecondaryVisible) = secondary;
            (ShowColumnOriginalText, IsOriginalReadOnly, IsShowingOriginalNonMatchingLines, IsEditOriginalMode) = originalUi;
            (_converted, _saveAsFileNameSuggestion, _changeSubtitleHash, _changeSubtitleHashOriginal) = saveState;
            (_revisionFingerprint, _lastRevisionDirectory) = revisionState;

            _changingFormatProgrammatically = true;
            try { SelectedSubtitleFormat = format; }
            finally { _changingFormatProgrammatically = formatChanging; }

            SetSynchronousAudioState(before.SynchronousAudio); // null must clear a failed first cut too
            _synchronousAudioDirectory = directory;
            _synchronousAudioCutPosition = cutPosition;
            RestoreUndoRedoState(before);

            if (AudioVisualizer != null)
            {
                AudioVisualizer.NewSelectionParagraph = selection;
                AudioVisualizer.AudioRangeSelection = audioRangeSelection;
            }

            if (_synchronousAudioMediaOpenVersion != mediaVersion)
            {
                if (rollbackMedia != null)
                    await LoadSynchronousAudioAsync(rollbackMedia, CancellationToken.None);
                else
                    VideoCloseFile();
            }

            _updateAudioVisualizer = true;
        }

        return new SynchronousAudioHostSnapshot(identity, format, mediaFile, directory, RestoreAsync);
    }
}
