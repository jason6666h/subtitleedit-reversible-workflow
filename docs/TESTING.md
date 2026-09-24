# Testing

The repository includes automated tests for the project-owned core logic and the Subtitle Edit integration layer.

## Current regression groups

- **Reversible audio core** — timeline transforms, cuts, validation, export, revisions, and recovery behavior
- **Subtitle review core** — prompt generation, response parsing, glossary handling, missing/duplicate/conflicting IDs, and preview safety
- **Review session safety** — save/resume behavior and stale-session fingerprints
- **Subtitle Edit integration** — waveform range selection, audio workflow integration, and AI review UI integration

## Run tests

```powershell
pwsh ./scripts/setup-upstream.ps1
pwsh ./scripts/test.ps1
```

The setup script creates a clean Subtitle Edit 5.2.0 checkout and applies the integration patch before the UI tests run.

## Build validation

```powershell
pwsh ./scripts/build.ps1
```

The build script runs the test suite first, then publishes the integrated Windows x64 build.

## Test data

Audio fixtures are synthetic tones. Public text fixtures use generic examples only.
