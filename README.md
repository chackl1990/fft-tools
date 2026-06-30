# FFT Tools

Revision r158 is based on r157 and keeps the intended Dry residual Front/Side pan split. The Dry Center is calculated by the neutral CenterExtract sum/difference implementation, RF64 output and RIFF/RF64 input support remain active, and the correct reconstruction equation includes the optional Dry Side split.
Inspiration the general Center Cut concept by Avery Lee.
Internal WAV I/O by chackl; FFT backend retained separately.

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

| Component   | Destination    |
| ----------- | -------------- |
| Dry         | Front channels |
| Near reverb | Side channels  |
| Far reverb  | Rear channels  |

### Center Extraction

The Dry Center is calculated with the CenterExtract sum/difference common-signal estimate instead of the earlier min-magnitude/common-phase approximation.

For every FFT bin, the algorithm evaluates the complex `L` and `R` bins and calculates the common part with the classic sum/difference relationship:


```text
sum  = L + R
diff = L - R
alpha = 0.5 - 0.5 * sqrt(|diff|^2 / |sum|^2)
C = sum * alpha
```

In r158 the Center stage uses the neutral `CenterExtract` helper. It does not use the older pan gate, millisecond phase-delay gate, signed-amplitude fold, or Center fragment pruner.

After the Center has been subtracted from the Dry signal, the remaining Dry residual can be split between Front and Side for very clear lateral stereo material. This is controlled by two intentionally easy-to-find code constants in `Library/FFTToolsUpmix.cs`:

```csharp
private const double sidepan = 0.8;
private const double front_side_mix = 0.5;
```

`sidepan = 0.8` means that only the outermost 20% of the pan range are eligible for the Front/Side split. The transition from `0.8` to `1.0` uses SmoothStep interpolation. `front_side_mix = 0.5` means that a fully side-panned Dry residual bin is split 50% Front / 50% Side. `front_side_mix = 0.0` would move it fully to Side, while `front_side_mix = 1.0` keeps it fully in Front. The split is routing, not an addition, so `frontShare + sideShare = 1.0`.

### Reconstruction Goal

The normal `--upmix` mode keeps the Dry/Center split reconstructable, but the optional Dry residual Front/Side pan split must be included in the technical mixdown equation. With unity gains (`--nocetergain 0.0`, `--centergain 0.0`, `--center-gain 100`, `-a 100`) the intended Dry reconstruction is:

```text
FL + drySideSplitL + C = dryL
FR + drySideSplitR + C = dryR
```

Because `drySideSplitL/R` is routed into the Side channels together with Near reverb, the full technical stereo reconstruction is:

```text
L = FL + C + SL + BL
R = FR + C + SR + BR
```

where `SL/SR` contains Near reverb plus the optional far-panned Dry residual split, and `BL/BR` contains Far reverb. This is a technical unity-sum reconstruction, not a consumer AVR downmix with -3 dB Center or Surround coefficients.

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

Important `--upmix` final output gain options:

```text
--nocetergain <dB>   default -6.0 dB, non-center channels
--centergain <dB>    default  0.0 dB, Center channel
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

Container support:

```text
Input:  RIFF/WAVE and RF64/WAVE
Output: RF64/WAVE is always written
```

RF64 output is used even for small files so that long renders are not limited by the classic RIFF 32-bit chunk-size boundary. The audio sample format is still preserved:

```text
Integer Input -> Integer RF64 Output (same bit depth)
Float32 Input -> Float32 RF64 Output
Float64 Input -> Float64 RF64 Output
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
100
```

Beginning of near-reverb detection.

---

### --dereverb-early-full <ms>

Default:

```text
200
```

Near-reverb reaches full strength.

---

### --dereverb-late-start <ms>

Default:

```text
300
```

Transition from near reverb to far reverb.

---

### --dereverb-max-ms <ms>

Default:

```text
500
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

With unity output gains (`--nocetergain 0.0`, `--centergain 0.0`, `--center-gain 100`, `-a 100`), the dry stage remains reconstructable. Because very lateral Dry residual can now be split to Side, the technical reconstruction is:

```text
FL + SL(dry split) + C = dry L
FR + SR(dry split) + C = dry R
```

The full dereverb/upmix split is now:

```text
Front = dry residual that stays in Front + Center
Side  = near reverb + optional clear lateral dry residual split
Rear  = far reverb
```

## Parameters

### -a <percent>

Global output gain.

Default:

```text
100
```

---

### --nocetergain <dB>

Final output gain for all non-center channels in the dereverb based `--upmix` mode.

This affects:

```text
FL
FR
SL
SR
BL
BR
```

Default:

```text
-6.0
```

The value is specified in decibels and may be positive or negative.

Examples:

```text
--nocetergain -6.0
--nocetergain -3.0
--nocetergain 0.0
```

For convenience, the corrected spellings `--nocentergain`, `--noncentergain`, `--noncenter-gain`, and `--no-center-gain` are accepted as aliases.

---

### --centergain <dB>

Final output gain for the Center channel in the dereverb based `--upmix` mode.

Default:

```text
0.0
```

The value is specified in decibels and may be positive or negative.

Examples:

```text
--centergain 0.0
--centergain 3.0
--centergain -1.5
```

This is a final output-level adjustment. It is separate from `--center-gain <pct>`, which still controls the internal center extraction/residual amount.

---

### --center-gain <pct>

Default:

```text
100
```

Controls the internal focused-center extraction/residual amount.

This is not the final Center output gain. For final Center channel level in dB, use `--centergain <dB>`.

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

The Center detector in this mode uses the same CenterExtract sum/difference formula as the dereverb based `--upmix` mode. The resulting Center bin is subtracted from the original spectral L/R bins. The remaining Front/Side distribution uses the same `sidepan` / `front_side_mix` SmoothStep handoff as the dereverb based mode.

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

Upmix with explicit final Center/non-center output gains:

```text
fft_tools.exe input.wav --upmix output_7_1.wav --nocetergain -6.0 --centergain 0.0 -o
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

# Historical Reference

The CLI banner and this README retain a historical reference to the general Center Cut concept by Avery Lee. The implementation code itself uses neutral `CenterExtract` naming.

---

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

# About the Author

FFT Tools is the result of more than two decades of experimentation with audio processing, DSP concepts, software development, and audio reproduction.

My name is chackl, and my interest in audio processing started long before FFT Tools existed.

### Early Audio and DSP Development

Around 2007–2008, I became involved with the SynthMaker community, which later evolved into FlowStone. This environment gave me one of my first practical opportunities to experiment with DSP concepts, audio routing, signal processing, and plugin development.

Over the years, I created numerous audio tools, experiments, and VST plugins under the name "chackl". Some of them may still be found on the internet.

During the same period, I was active in several audio-related communities, including Mainstream Audio, VirtualDJ plugin development, and FlowStone DSP development. These communities helped me continuously explore audio processing techniques, plugin architectures, signal analysis, and real-time DSP experimentation.

### Professional and Large-Scale Technical Projects

Between approximately 2013 and 2019, I was involved in various software, networking, and infrastructure projects related to a local festival.

These projects ranged from software development to network and technical infrastructure tasks and provided valuable practical experience with large-scale systems, reliability, and real-world deployment environments.

### The Home Hi-Fi Experiment

Beginning around 2019, I became increasingly interested in what could be achieved using affordable consumer AV receivers and home audio systems.

Instead of focusing only on high-end studio environments, I started exploring how advanced DSP techniques could improve real-world listening experiences using reasonably priced equipment.

This became the foundation for many of the ideas that would later evolve into FFT Tools.

### HFSoundRestore and the Beginning of FFT Research

In 2020, I developed HFSoundRestore, an early experiment focused on reconstructing missing high-frequency information in compressed audio material.

The project explored whether lost upper-frequency content could be estimated and recreated from the spectral information still available within the recording.

HFSoundRestore marked the beginning of a much deeper interest in FFT-based audio processing.

Many of the following experiments never became public projects, but each of them contributed ideas, failures, listening impressions, and lessons that eventually found their way into FFT Tools.

### Why FFT Tools Exists Today

For many years, the ideas behind FFT Tools were significantly larger than what could realistically be implemented and maintained by a single person working on the project in their spare time.

Recent advances in AI-assisted development have changed that situation considerably.

Modern AI tools make it possible to:

* Manage larger codebases
* Explore complex DSP concepts faster
* Prototype algorithms more efficiently
* Maintain projects that would previously have required significantly larger development teams

FFT Tools is therefore not a sudden idea, but the result of many years of experimentation, research, listening tests, and DSP exploration finally becoming practical to implement.

The project represents the current stage of an ongoing journey that started with early SynthMaker experiments, continued through FlowStone DSP development, plugin creation, VirtualDJ plugin work, Hi-Fi experimentation, HFSoundRestore, and years of research and learning.

Regards, C. Hackl