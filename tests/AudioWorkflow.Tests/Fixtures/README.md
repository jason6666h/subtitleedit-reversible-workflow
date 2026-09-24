# Synthetic MP3 fixtures

These files are short 440 Hz sine-wave clips used only for decoder and sample-boundary regression tests.

They contain no speech and were not derived from user recordings or copyrighted media.

Filename format:

`sine-SAMPLERATE-CHANNELS.mp3`

The fixtures were generated with FFmpeg using a sine-wave source and LAME MP3 encoding, then committed as deterministic test inputs.
