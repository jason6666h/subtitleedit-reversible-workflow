# Architecture

This project is split into three layers so most logic can be tested without running the full Subtitle Edit UI.

## 1. Core libraries

### AudioWorkflow.Core

Owns the reversible audio model:

- audio cuts and exact sample-boundary handling
- original/edited timeline mapping
- revisions and checkpoints
- integrity validation and hashes
- export/load contracts
- Adobe Audition handoff validation

### SubtitleReview.Core

Owns the AI review contract:

- batch IDs and prompt construction
- Markdown response parsing
- missing/duplicate/conflicting ID detection
- glossary and protected-phrase handling
- session fingerprints and stale-result protection
- preview-before-apply safety rules

## 2. Subtitle Edit integration

`integration/ReversibleAudioSync/` and `integration/SubtitleReview/` connect the core libraries to Subtitle Edit's waveform, player, Undo/Redo system, menus, and dialogs.

The project intentionally avoids carrying a full fork of Subtitle Edit. Instead, `integration/host-hooks.patch` contains the small set of changes required in the supported upstream version.

## 3. Tests

The test suite includes:

- deterministic audio engine tests
- AI review protocol and parser tests
- review-session safety tests
- Subtitle Edit UI integration tests

Audio fixtures are synthetic. No user recordings are required.

## Build model

`scripts/setup-upstream.ps1` checks out the supported Subtitle Edit commit and applies the integration patch. Tests and builds then run against that checkout.

This keeps the public repository focused on the added workflow while making the upstream dependency explicit.
