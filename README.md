# fft_tools

`fft_tools` is an experimental phase-aware audio processing toolkit for WAV files.

It focuses on high-quality stereo processing, dereverb analysis, dry-center extraction, and stereo-to-7.1 upmixing. The current upmix path uses a phase-aware center model, frequency-dependent phase tolerance based on milliseconds, and a reconstructive dry-center residual split.

## Main Features

* Phase-aware stereo-to-7.-dependent phase tolerance based on milliseconds, and a1 upmixing
* Dry center extraction with residual L/R reconstruction
* Dereverb analysis mode with dry, near reverb, and far reverb output
* Legacy-style spectrum upmix mode
* FFT-based harmonic enhancement mode
* Streaming/block-based processing for large files
* WAV input/output support
* PCM, Float32, and Float64 WAV handling
* x64 project configuration
* Center output kept at reference level while non-center channels are attenuated by -3 dB

## Modes

### `--upmix`

Creates a 7.1 output from a stereo WAV input.

The dry signal is split into:

* Front Left
* Center
* Front Right

The center is calculated as a complex Mid component and subtracted from Left and Right to create residual front channels:

```text
C = Mid * CenterMask
FrontL = L - C
FrontR = R - C
```

This keeps the dry front split phase-aware and downmix-friendly.

Near reverb is routed to Side Left/Right.
Far reverb is routed to Back Left/Right.
LFE remains silent.

### `--dereverb`

Creates a 6-channel analysis output:

```text
1. L dry
2. R dry
3. L near reverb
4. R near reverb
5. L far reverb
6. R far reverb
```

The decomposition is designed so that the original stereo signal can be reconstructed from:

```text
L original = L dry + L near + L far
R original = R dry + R near + R far
```

### `--upmix-spectrum`

Creates a 7.1 output using the spectrum-based upmix path.

This mode keeps the 7-point spectrum model but uses the current phase-aware center check.

### `--enhance`

FFT-based harmonic enhancement mode for extending or reinforcing high-frequency content.

## Phase-Aware Center Logic

The center mask is controlled by a frequency-dependent phase tolerance derived from milliseconds.

Current constants are defined in:

```text
Library/FFTToolsProcessor.cs
```

```csharp
private const double CenterPhaseFullDelayMs = 5.0;
private const double CenterPhaseZeroDelayMs = 10.0;
```

The idea is that a fixed phase angle is too strict at high frequencies. A small real-world time difference between left and right becomes a much larger phase angle as frequency rises. Therefore, the allowed phase tolerance is derived from delay in milliseconds instead of a fixed degree value.

## Output Level Model

For 7.1 output modes:

```text
Center = 0 dB reference
All non-center channels = -3 dB
```

This keeps the discrete center at reference level while compensating for phantom-center loudness in the other speakers.

## Example Usage

```bash
fft_tools.exe input.wav --upmix output_7_1.wav -o
```

```bash
fft_tools.exe input.wav --dereverb output_dereverb.wav -o
```

```bash
fft_tools.exe input.wav --upmix-spectrum output_spectrum_7_1.wav -o
```

```bash
fft_tools.exe input.wav --enhance output_enhanced.wav -o
```

## Notes

This project is experimental and intended for audio research, HiFi testing, and custom surround-processing workflows.

The current implementation is an independent phase-aware rework of stereo center/residual extraction and upmixing concepts. It keeps the FFT component as a separate utility dependency and replaces the former CenterCut-style processing path with a custom dry-center residual model.

## License

No license is currently granted.

This repository is published for review and development purposes only. Do not copy, redistribute, or reuse the code without explicit permission from the author.
