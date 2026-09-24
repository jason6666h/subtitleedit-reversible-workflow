using System;

namespace Nikse.SubtitleEdit.Features.Main;

public enum AudioSessionMode
{
    Inactive,
    Editing,
    OriginalPreview,
    AuditionPending,
    Faulted,
}

public partial class MainViewModel
{
    public AudioSessionMode SynchronousAudioMode =>
        _synchronousAudioFaulted ? AudioSessionMode.Faulted :
        _originalTimelinePreview ? AudioSessionMode.OriginalPreview :
        IsAuditionHandoffPending ? AudioSessionMode.AuditionPending :
        _synchronousAudioState != null ? AudioSessionMode.Editing :
        AudioSessionMode.Inactive;

    public bool CanStartAuditionFromUi =>
        SynchronousAudioMode is AudioSessionMode.Inactive or AudioSessionMode.Editing;
    public bool CanNavigateAudioChanges => SynchronousAudioMode == AudioSessionMode.Editing;
    public bool CanVerifyAudio => SynchronousAudioMode is AudioSessionMode.Editing or AudioSessionMode.AuditionPending;
    public bool CanExportAudio => SynchronousAudioMode == AudioSessionMode.Editing;
    public bool CanPreviewOriginalTimeline => SynchronousAudioMode == AudioSessionMode.Editing;
    public bool CanReturnEditedTimeline => SynchronousAudioMode == AudioSessionMode.OriginalPreview;

    internal void NotifySynchronousAudioModeChanged()
    {
        OnPropertyChanged(nameof(SynchronousAudioProjectStatus));
        OnPropertyChanged(nameof(SynchronousAudioMode));
        OnPropertyChanged(nameof(CanStartAuditionFromUi));
        OnPropertyChanged(nameof(CanNavigateAudioChanges));
        OnPropertyChanged(nameof(CanVerifyAudio));
        OnPropertyChanged(nameof(CanExportAudio));
        OnPropertyChanged(nameof(CanPreviewOriginalTimeline));
        OnPropertyChanged(nameof(CanReturnEditedTimeline));
    }
}
