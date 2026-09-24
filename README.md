# Subtitle Edit Reversible Audio Workflow

[繁體中文](README.zh-TW.md)

A source-level extension for **Subtitle Edit 5.2.0** that adds two workflows:

1. **Reversible audio editing** — cut or revise audio while keeping subtitle timing, edit history, and recovery data in sync.
2. **Safety-focused AI subtitle review** — review large subtitle batches with explicit checks for missing rows, conflicting replies, stale sessions, glossary rules, and preview-before-apply.

This project is intended for long-form subtitle work such as interviews, lectures, podcasts, oral-history recordings, and other projects where audio and subtitle timing must remain consistent.

## What it adds

### Reversible audio workflow

- Cut a selected audio range directly from the Subtitle Edit waveform.
- Recalculate subtitle timing after the cut.
- Keep audio and subtitle changes together in Undo/Redo.
- Compare the original and edited timelines.
- Jump between changed regions for review.
- Keep immutable revisions and checkpoints for recovery.
- Send audio to Adobe Audition and validate it before bringing it back.
- Configure, change, or disable audio-editing shortcuts.

### AI subtitle review

- Split long subtitle sets into manageable review batches.
- Keep stable row IDs so AI replies map back to the correct subtitle rows.
- Detect missing IDs, unknown IDs, duplicates, and conflicting suggestions.
- Preview every proposed change before applying it.
- Support glossaries, protected phrases, reference material, and previous-review attachments.
- Resume saved review sessions with fingerprint checks to reduce stale-session mistakes.
- Accept both Traditional Chinese and English review-table formats.

## Project status

The integration is tested against:

- **Subtitle Edit 5.2.0**
- Baseline commit: `d8e3b8b41e856a896c541ce7e59b490a99c21196`
- Windows x64
- .NET 10

This is **not an official Subtitle Edit plugin or distribution**. It uses a small source-level host patch because the current plugin API does not expose all waveform, media-replacement, and compound Undo/Redo capabilities required by the workflow.

## Language support

- Documentation: English + Traditional Chinese
- AI review prompt/response protocol: English + Traditional Chinese
- Main integration UI: Traditional Chinese is currently the most complete
- English UI localization: in progress

The codebase uses one implementation; there are not separate Chinese and English forks.

## Quick start

Requirements:

- Git
- PowerShell 7+
- .NET 10 SDK
- Windows x64

```powershell
git clone https://github.com/jason6666h/subtitleedit-reversible-workflow.git
cd subtitleedit-reversible-workflow

pwsh ./scripts/setup-upstream.ps1
pwsh ./scripts/test.ps1
pwsh ./scripts/build.ps1
```

### What the scripts do

`setup-upstream.ps1`

- clones the official Subtitle Edit repository
- resets it to the supported 5.2.0 baseline
- applies `integration/host-hooks.patch`

`test.ps1`

- runs the reversible-audio core tests
- runs the subtitle-review core/session tests
- runs the relevant Subtitle Edit UI integration tests

`build.ps1`

- publishes a Windows x64 source-integrated build to `artifacts/SubtitleEdit-win-x64`

The repository intentionally does **not** redistribute FFmpeg, libmpv, or official Subtitle Edit binaries. A locally published build may still need compatible runtime files before it can be used as a complete portable package.

## Repository structure

```text
integration/
  ReversibleAudioSync/   Subtitle Edit audio workflow integration
  SubtitleReview/        Subtitle Edit AI review UI/integration
  host-hooks.patch       minimal host changes applied to Subtitle Edit

src/
  AudioWorkflow.Core/    audio timeline, revisions, validation, export, DAW handoff
  SubtitleReview.Core/   review protocol, parser, glossary, evidence, session safety

tests/
  ...                    core and Subtitle Edit integration regression tests

scripts/
  setup-upstream.ps1
  test.ps1
  build.ps1
```

## Why it is source-integrated

Some features need access to Subtitle Edit internals that a normal plugin cannot currently reach, including:

- waveform range selection
- replacing the active audio source
- synchronizing subtitle timing with destructive audio edits
- compound audio/subtitle Undo/Redo
- player and waveform state during external-editor round trips

To keep the integration maintainable, the host changes are kept in a small, explicit patch and the main logic stays in separate project-owned source folders.

## Testing

The public source currently includes regression coverage for:

- audio timeline and cut behavior
- audio validation and export
- revision/checkpoint handling
- Adobe Audition round trips
- audio/subtitle Undo/Redo
- AI response parsing
- missing/duplicate/conflicting review rows
- session restore and stale-session protection
- Subtitle Edit UI integration

See [docs/TESTING.md](docs/TESTING.md).

## Privacy

The repository contains source code, generic text fixtures, and synthetic test audio only.

Do not submit real user media, private subtitle files, AI review sessions, revision folders, credentials, tokens, or machine-specific paths.

See [PRIVACY.md](PRIVACY.md).

## Contributing

Contributions are welcome, especially for:

- English UI localization
- additional regression tests
- reducing the number of Subtitle Edit host hooks
- compatibility with future stable Subtitle Edit releases
- workflow improvements that remain safe for existing Subtitle Edit users

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

Project-owned source is released under the MIT License.

Subtitle Edit and third-party dependencies remain under their own licenses. This repository does not relicense them.
