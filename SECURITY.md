# Security Policy

This project edits media, rewrites subtitle timing, restores revisions, and can exchange files with external tools. Bugs in these areas may cause data loss or unsafe file operations, so security and data-integrity reports are taken seriously.

## Please report privately when possible

Use GitHub Private Vulnerability Reporting if it is enabled for this repository.

If private reporting is not available, open a public issue with only a short, non-sensitive description and ask for a private contact channel. Do **not** post credentials, private media, private subtitle files, or sensitive paths in a public issue.

## Issues that should be treated as security-sensitive

Examples include:

- arbitrary file overwrite or deletion
- path traversal
- command execution
- credential or token exposure
- unsafe external-tool handoff
- revision/checkpoint restore that can replace the wrong file
- AI review application that can modify rows outside the approved selection

## Reproduction data

Please use synthetic media and generic subtitle text whenever possible.
