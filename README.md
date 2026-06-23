# FFT Tools

FFT Tools is a collection of FFT-based audio processing tools designed for Hi-Fi experiments and advanced audio research.

The project currently focuses on:

* Spectral enhancement and harmonic reconstruction
* Stereo-to-multichannel upmixing
* Reverb separation and analysis
* Phase-aware FFT processing
* Experimental spatial audio reconstruction

The goal is to explore whether missing spectral information, room information, and spatial content can be reconstructed, separated, or redistributed using FFT-based techniques while preserving as much acoustic coherence as possible.

---

# Philosophy

FFT Tools is an experimental Hi-Fi project.

A fundamental limitation always applies:

> Lost information cannot be restored. It can only be reconstructed.

Modern recordings often suffer from lossy compression, bandwidth limitations, aggressive mastering, or codec artifacts. FFT Tools attempts to estimate and reconstruct plausible content based on spectral, harmonic, and phase relationships that are still present in the source material.

The project is intended for research, listening tests, and experimentation rather than commercial production workflows.

---

# Tested Equipment

The algorithms have primarily been evaluated using the following playback chain.

* Custom loudspeaker systems based on Manger Audio drivers
* Trinnov Altitude 16
* Nvidia Shield with VLC Player using bitstream audio output
* Zappiti 4K HDR Mini using Explorer playback with bitstream audio output

---

# Available Processing Modes

## Enhance

The Enhance module attempts to reconstruct missing upper octaves and lost high-frequency information.

The algorithm searches for the highest octave that still contains usable spectral information and uses:

* Harmonic generation
* FFT-based pitch shifting
* Spectral extrapolation

to reconstruct higher frequencies that may have been lost due to:

* Lossy compression
* Limited source bandwidth
* Low bitrate encoding
* Aggressive mastering

This is especially useful before upmixing heavily compressed material.

Without enhancement, compression artifacts may be interpreted as valid signal content during the upmix process and become spatially exaggerated. By reconstructing missing spectral information first, these artifacts often become less noticeable.

### Important

Enhance does not restore lost information.

It estimates plausible harmonic content based on the spectral structure that is still available within the source material.

### Sample Rate Handling

Enhance operates most efficiently when the target sample rate is a power-of-two multiple of the source sample rate.

Examples:

```text
48 kHz -> 96 kHz
48 kHz -> 192 kHz
44.1 kHz -> 88.2 kHz
44.1 kHz -> 176.4 kHz
```

If the requested output sample rate does not match a clean power-of-two multiplier, Enhance automatically selects the next higher internal processing rate and performs enhancement at that rate first.

After enhancement, a high-quality Hi-Fi resampling stage converts the signal to the requested output rate.

Example:

```text
48 kHz -> 176.4 kHz
```

Internally:

```text
48 kHz
→ 192 kHz Enhance Processing
→ Hi-Fi Resampling
→ 176.4 kHz Output
```

Whenever possible, clean power-of-two target rates are recommended because they avoid the additional resampling stage.

However, some playback chains and formats such as Dolby Atmos workflows only support specific sample rates. In those situations the additional high-quality resampling stage may be required.

---

## Upmix

The main 7.1 upmix engine.

The process begins with a three-stage dereverb analysis.

The stereo source is separated into:

* Dry signal
* Near reverb
* Far reverb

The resulting components are distributed as follows:

| Component              | Destination    |
| ---------------------- | -------------- |
| Dry + very near reverb | Front channels |
| Near reverb            | Side channels  |
| Far reverb             | Rear channels  |

### Center Extraction

Center extraction is not based solely on FFT magnitude.

The algorithm additionally evaluates:

* Phase coherence
* Frequency-dependent phase relationships
* Millisecond-based delay tolerance

This allows stable center detection even when magnitude information alone would not be sufficient.

### Reconstruction Goal

The upmix attempts to preserve both:

* Magnitude relationships
* Phase relationships

so that the acoustic sum of all loudspeakers remains as close as possible to the original stereo source.

The intended behavior is:

* Real loudspeaker playback recombines correctly
* Digital downmixing remains extremely close to the original stereo signal
* Only the intentional +3 dB center reference adjustment remains as a measurable difference

---

## Upmix Spectrum

Upmix Spectrum is the first experimental approach toward a fully spectral 7-channel upmix.

The concept assumes that a stereo image contains multiple center positions instead of only one.

The front stage is divided into:

```text
L → CL → C → CR → R
```

Instead of assigning content only to Left, Center, and Right, the complete spectrum is continuously distributed across these virtual positions.

### Rear Channel Extrapolation

The rear channels additionally use spectral extrapolation to generate ambient and wide information.

This creates a larger and more immersive soundstage while maintaining spectral consistency.

### Experimental Status

Upmix Spectrum is currently considered experimental and represents the earliest implementation of the project's multichannel spectral upmix concepts.

---

## Dereverb

Dereverb is primarily a test and analysis mode.

Unlike the main Upmix mode, it produces a dedicated multichannel export containing separated reverb components.

The algorithm separates:

* Dry signal
* Near reverb
* Far reverb

This allows inspection and evaluation of the reverb extraction process independently from the upmix engine.

### Output Format

The output channel layout differs from the normal upmix output and is intended for testing, analysis, and development purposes.

---

# Command Line Usage

General syntax:

```text
fft_tools.exe <input> <mode> <output> [options]
```

Example:

```text
fft_tools.exe input.wav --upmix output.wav -o
```

Only one processing mode can be active at a time.

Available modes:

```text
--enhance
--dereverb
--upmix
--upmix-spectrum
```

---

# WAV Format Support

Supported WAV input formats:

```text
PCM Integer:
8 Bit
16 Bit
24 Bit
32 Bit

IEEE Float:
32 Bit Float
64 Bit Float (Double)
```

Output format preservation:

```text
Integer Input -> Integer Output (same bit depth)
Float32 Input -> Float32 Output
Float64 Input -> Float64 Output
```

---

# Global Options

## -o

Overwrite existing output files without confirmation.

Default:

```text
disabled
```

---

## -t <count>

Worker thread count.

Default:

```text
0
```

Allowed range:

```text
1 .. 32
```

Examples:

```text
-t 4
-t 8
-t 16
```

---

# Enhance Mode

Usage:

```text
fft_tools.exe input.wav --enhance output.wav
```

## Parameters

### --rate / -r

Target sample rate.

Values:

```text
0 = keep original sample rate
1 = keep original sample rate
2 = source rate × 2
4 = source rate × 4
8 = source rate × 8
96000 = absolute sample rate
192000 = absolute sample rate
```

Examples:

```text
--rate 2
--rate 4
--rate 96000
-r 192000
```

---

### -n <db>

Peak normalization after processing.

Examples:

```text
-n -1
-n -0.5
-n -3
```

Recommended:

```text
-1 dB
```

---

# Dereverb Mode

Usage:

```text
fft_tools.exe input.wav --dereverb output_6ch.wav
```

Output channels:

```text
1 = Left Dry
2 = Right Dry
3 = Left Near Reverb
4 = Right Near Reverb
5 = Left Far Reverb
6 = Right Far Reverb
```

## Parameters

### --dereverb-lowcut <hz>

Default:

```text
100
```

Recommended:

```text
80
100
120
150
```

---

### --dereverb-early-start <ms>

Default:

```text
40
```

Beginning of near-reverb detection.

---

### --dereverb-early-full <ms>

Default:

```text
80
```

Near-reverb reaches full strength.

---

### --dereverb-late-start <ms>

Default:

```text
160
```

Transition from near reverb to far reverb.

---

### --dereverb-max-ms <ms>

Default:

```text
600
```

Far reverb reaches full strength.

---

### --dereverb-strength <pct>

Default:

```text
100
```

Examples:

```text
50
100
150
200
```

Controls overall reverb extraction strength.

---

### --dereverb-tonal-protect <value>

Default:

```text
0.00
```

Examples:

```text
0.75
0.90
1.00
```

Protects stable tonal content from being extracted into reverb channels.

---

# Upmix Mode

Usage:

```text
fft_tools.exe input.wav --upmix output_7_1.wav
```

Output channel layout:

```text
1 = FL
2 = FR
3 = C
4 = LFE
5 = SL
6 = SR
7 = BL
8 = BR
```

## Parameters

### -a <percent>

Global output gain.

Default:

```text
100
```

---

### --center-gain <pct>

Default:

```text
100
```

Controls focused center energy.

Recommended:

```text
80
100
120
150
```

---

### --wide-gain <pct>

Default:

```text
35
```

Controls spectral wide extraction strength.

Recommended:

```text
20
35
60
100
```

---

### --wide-exp <value>

Default:

```text
2.5
```

Controls how aggressively wide information is detected.

Recommended:

```text
0.5
1.0
2.5
4.0
```

---

### --wide-lowcut <hz>

Default:

```text
250
```

Low-frequency protection for wide extraction.

Recommended:

```text
100
150
250
400
```

---

### --wide-phase <value>

Default:

```text
1.0
```

Controls phase contribution during wide detection.

Range:

```text
0.0 .. 2.0
```

---

### --wide-smooth <ms>

Default:

```text
80
```

Currently reserved for compatibility and has no audible effect in the current implementation.

---

### --pan-sharpness <value>

Default:

```text
1.0
```

Controls focus between:

```text
L
CL
C
CR
R
```

Recommended:

```text
0.5
1.0
1.5
2.0
```

---

### --clcr-pos <value>

Default:

```text
0.5
```

Controls the position of CL and CR anchors.

Range:

```text
0.05 .. 0.95
```

Examples:

```text
0.333
0.5
0.75
```

---

# Upmix Spectrum Mode

Usage:

```text
fft_tools.exe input.wav --upmix-spectrum output_7_1.wav
```

Output channel layout:

```text
1 = FL
2 = FR
3 = C
4 = LFE
5 = SL
6 = SR
7 = BL
8 = BR
```

Uses the same front-stage spectral parameters as Upmix but does not perform dereverb separation.

Available parameters:

```text
-a
--center-gain
--wide-gain
--wide-exp
--wide-lowcut
--wide-phase
--wide-smooth
--pan-sharpness
--clcr-pos
```

---

# Internal FFT Defaults

```text
44.1 / 48 kHz  -> FFT Size 8192
96 kHz         -> FFT Size 16384
192 kHz        -> FFT Size 32768
```

Dereverb overlap:

```text
16x
```

Enhance harmonic fade:

```text
1/48 octave
```

Enhance bin selection:

```text
Automatic
```

---

# Typical Commands

Enhance to 96 kHz:

```text
fft_tools.exe input.wav --enhance output.wav --rate 96000 -n -1 -o
```

Main 7.1 Upmix:

```text
fft_tools.exe input.wav --upmix output_7_1.wav -o
```

Wide and aggressive Upmix:

```text
fft_tools.exe input.wav --upmix output_7_1.wav --wide-gain 100 --wide-exp 1.0 -o
```

Dereverb Export:

```text
fft_tools.exe input.wav --dereverb output_6ch.wav -o
```

Spectral Upmix:

```text
fft_tools.exe input.wav --upmix-spectrum output_7_1.wav -o
```

---

# Project Status

FFT Tools is an active experimental research project focused on:

* FFT-based enhancement
* Harmonic reconstruction
* Phase-aware center extraction
* Reverb separation
* Immersive audio upmixing
* Spectral audio analysis

The algorithms are continuously refined and should currently be considered experimental research tools intended for critical Hi-Fi listening and technical evaluation.

# License / Usage Notice

This project currently does not provide a final open-source license.

FFT Tools is still in a testing and research phase, and the future direction of the project is intentionally still open.

Some parts of the code may have used existing implementations as technical references or starting points. This was not done to copy concepts, but because in certain low-level areas there are only a few practical or reasonable ways to implement the required functionality.

The tool may be used for private testing at home.

No warranty, liability, or guarantee of correctness, safety, compatibility, or audio quality is provided.

Public tests, demonstrations, presentations, publications, comparisons, or similar uses must be discussed with the repository author in advance.

The same applies if the tool, parts of the tool, or derived work based on the tool should be reused, redistributed, published online, or integrated into another project. Permission must be requested from the repository author as long as the license has not been precisely defined.

In the highly unlikely event of a pull request, the contributor must be aware that the project license may still change in the future. By submitting a pull request, the contributor does so with this knowledge and automatically waives any claim to co-ownership, co-management, license control, project governance, or decision-making rights regarding the project.

you can contact me by opening an issue.

Regards, C.Hackl
