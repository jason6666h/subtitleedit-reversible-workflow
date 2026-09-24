using AudioWorkflow;
using Nikse.SubtitleEdit.Logic.UndoRedo;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nikse.SubtitleEdit.Features.Main;

public partial class MainViewModel
{
    private const long AudioDiskSafetyBytes = 256L * 1024 * 1024;

    private static void EnsureFreeAudioWorkSpace(string directory, long requiredBytes, string operation)
    {
        if (requiredBytes <= 0) return;
        var full = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root)) return;
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.AvailableFreeSpace >= requiredBytes) return;

        static string GiB(long bytes) => (bytes / 1073741824d).ToString("N2");
        throw new IOException(
            $"{operation}需要約 {GiB(requiredBytes)} GiB 可用空間，目前僅剩 {GiB(drive.AvailableFreeSpace)} GiB。\n" +
            "請先釋放工作磁碟空間，再重新執行；原始音檔與字幕尚未變更。");
    }

    private async Task EnsureSynchronousAudioEditSpaceAsync(string source, string directory,
        bool firstManagedEdit, CancellationToken token)
    {
        long pcmBytes;
        if (Path.GetExtension(source).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            pcmBytes = new FileInfo(source).Length;
        }
        else if (Path.GetExtension(source).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var probe = await new FfmpegService(new WorkflowSettings(), AppContext.BaseDirectory)
                .ProbeAsync(source, token);
            var duration = probe.DurationSeconds ?? GetVideoPlayerControl()?.VideoPlayer.Duration ?? 0;
            if (!double.IsFinite(duration) || duration <= 0) return;
            var estimated = duration * probe.SampleRate * probe.Channels * 3d + 1024 * 1024;
            if (!double.IsFinite(estimated) || estimated <= 0 || estimated > long.MaxValue) return;
            pcmBytes = checked((long)Math.Ceiling(estimated));
        }
        else return;

        // First managed edit may briefly hold a baseline and a new current WAV together.
        // Later compact edits already own the baseline, so one additional working WAV is enough.
        var workingBytes = firstManagedEdit ? checked(pcmBytes * 2) : pcmBytes;
        var safety = Math.Max(AudioDiskSafetyBytes, workingBytes / 10);
        EnsureFreeAudioWorkSpace(directory, checked(workingBytes + safety), "這次音訊剪輯");
    }

    private async Task<SynchronousAudioState> EnsureOwnedRevisionBaselineAsync(
        SynchronousAudioState state, string directory, CancellationToken token)
    {
        var root = RevisionRoot(state, directory);
        var hasPublishedRevision = Directory.Exists(root) &&
            Directory.EnumerateDirectories(root)
                .Any(p => !Path.GetFileName(p).StartsWith(".pending-", StringComparison.Ordinal));
        if (hasPublishedRevision || HasCompactAudio(state) || IsManagedAudioPath(state.FileName, directory))
            return state;

        var sourceLength = new FileInfo(state.FileName).Length;
        EnsureFreeAudioWorkSpace(directory,
            checked(sourceLength + Math.Max(AudioDiskSafetyBytes / 2, sourceLength / 10)),
            "建立可逆音訊專案");

        Directory.CreateDirectory(directory);
        var original = Path.Combine(directory,
            "original-" + Guid.NewGuid().ToString("N") + Path.GetExtension(state.FileName));
        var validated = await AudioRevisionSource.CopyValidatedAsync(
            state.FileName, original, state.Sha256, token);
        CacheValidatedAudioSource(validated);
        return state with { FileName = original };
    }
}
