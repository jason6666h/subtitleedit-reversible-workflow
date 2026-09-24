# Contributing

Contributions are welcome.

The main goal is to improve audio/subtitle editing without making normal Subtitle Edit behavior less predictable or less safe.

## Good contribution areas

- English UI localization
- regression tests
- compatibility with newer stable Subtitle Edit releases
- reducing the number of host hooks
- safer revision/checkpoint handling
- better audio-review ergonomics
- AI review validation and workflow improvements

## Before opening a pull request

1. Keep test fixtures synthetic or generic.
2. Do not add private media, real subtitle projects, credentials, or machine-specific paths.
3. Keep Subtitle Edit host changes as small as possible.
4. Put reusable logic in the project-owned core/integration folders rather than expanding the host patch unnecessarily.
5. Run:

```powershell
pwsh ./scripts/test.ps1
```

6. For build-related changes, also run:

```powershell
pwsh ./scripts/build.ps1
```

## UI changes

For UI work:

- preserve normal Subtitle Edit behavior outside this workflow
- avoid adding extra clicks to common operations
- keep destructive actions explicit
- keep keyboard shortcuts configurable or removable
- prefer clear, direct labels over internal terminology

## Localization

Traditional Chinese is currently the most complete UI language. English localization contributions are especially welcome.

Please keep both language versions aligned when changing public documentation.
