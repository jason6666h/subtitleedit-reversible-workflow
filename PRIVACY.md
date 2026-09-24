# Privacy

This project is built around subtitle and audio workflows, so it is important to keep real user content out of the repository.

## What belongs in the repository

Only commit material that is safe to publish, such as:

- source code
- generic text fixtures
- synthetic test audio
- public documentation
- reproducible test data that contains no personal or confidential information

## What must not be committed

Do not commit or attach:

- real user audio or video
- production subtitle files
- AI review sessions or raw AI responses containing user content
- revision, checkpoint, or recovery folders created from real projects
- API keys, access tokens, cookies, passwords, or account files
- absolute local paths that reveal user or machine names
- logs, crash dumps, or screenshots containing private filenames or content

## Test data

Audio fixtures in this repository are synthetic test tones. Text fixtures use generic or fictional examples.

If a bug can only be reproduced with private material, create the smallest possible synthetic reproduction before opening a public issue.
