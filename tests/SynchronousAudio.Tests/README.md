# Audio engine regression tests

These tests exercise the reversible audio core without launching the Subtitle Edit UI.

Run from the repository root:

```powershell
dotnet run --project tests/SynchronousAudio.Tests/SynchronousAudio.Tests.csproj
```

An additional large-file test is available:

```powershell
dotnet run --project tests/SynchronousAudio.Tests/SynchronousAudio.Tests.csproj -- --large-rf64
```

The large-file test creates a sparse RF64 file and is intentionally opt-in.

## What is covered

The suite verifies:

- exact PCM cuts across supported WAV/RF64 layouts;
- sample-boundary rounding and range validation;
- source/output SHA-256 validation;
- cancellation and partial-file cleanup;
- MP3 decode followed by exact PCM editing;
- chained edits and timeline mapping;
- revision/checkpoint integrity;
- export/load contracts;
- Adobe Audition handoff validation;
- recomposition of earlier edits;
- cleanup guards and path-safety rules.

Generated files are written under `tests/SynchronousAudio.Tests/runs/` and are ignored by Git.

## MP3 fixtures and FFmpeg

The committed MP3 fixtures are synthetic sine waves from `tests/AudioWorkflow.Tests/Fixtures/`.

MP3 decoding requires FFmpeg. In an unbundled source checkout, the tool resolver looks for `ffmpeg.exe` / `ffprobe.exe` on an absolute PATH entry unless an explicit tool path is configured. This repository does not redistribute FFmpeg.

## Safety model

The audio engine does not edit the input media in place.

A successful edit produces a new validated output, while the original media remains unchanged. Validation uses hashes, lengths, sample counts, and timeline metadata. For host integration, validation leases are held through the media/subtitle commit so that the audio and subtitle state can be updated as one guarded operation.

Exports use relative paths, content hashes, and a checkpoint/report pair so moved export folders can be validated before restore.

These checks protect against accidental corruption and stale state; they are integrity checks, not cryptographic authenticity guarantees.
