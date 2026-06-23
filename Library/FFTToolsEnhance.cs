using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FFTTools {
	public class EnhanceOptions {
		public int Factor = 1;
		public int TargetRate = 0;
		public double ThresholdDb = -70.0;
		public double ConfirmMinOctaves = 1.0 / 12.0;
		public double FollowDownMaxOctaves = 1.0 / 12.0;
		public double FollowDownDropDb = 6.0;
	}

	public static class FFTToolsEnhance {
		private const double TwoPi = Math.PI * 2.0;

		// Current r96 enhancement model:
		// - full original FFT spectrum is copied to the output first; harmonics are additive overlays.
		// - harmonic material is generated in one-octave cascades with intentional gaps caused by pitch-shift style mapping.
		// - TransitionOctavesEachSide is kept from the r79 line for harmonic fade-in/out boundaries.
		// - Base gain is later doubled to compensate energy lost by the gapped harmonic system.
		private const double TransitionOctavesEachSide = 1.0 / 48.0;
		private const double EnhanceLoweringOctaves = 1.0 / 48.0;
		private const double TargetNyquistFadeOctaves = 1.0 / 48.0;

		// 10% FFT hop.  The actual hop is resolved through ResolveOddEnhanceHopSize(),
		// which forces an odd integer hop to spread periodic overlap errors less regularly.
		private const double EnhanceHopFraction = 0.1;
		private static readonly double TransitionDownFactor = Math.Pow(2.0, -TransitionOctavesEachSide);
		private static readonly double TransitionUpFactor = Math.Pow(2.0, TransitionOctavesEachSide);
		private static readonly double EnhanceLoweringDivisor = Math.Pow(2.0, EnhanceLoweringOctaves);
		private static readonly double TargetNyquistFadeDownFactor = Math.Pow(2.0, -TargetNyquistFadeOctaves);
		private static readonly double MaxHarmonicScale = Math.Pow(10.0, -0.1 / 20.0);

		public static double[,] Process(double[,] input, int inputFrames, int channels, int sampleRate, int windowSize, EnhanceOptions options, Action<double> progress) {
			if (options == null) options = new EnhanceOptions();
			int factor = options.Factor;
			if (factor != 1 && factor != 2 && factor != 4 && factor != 8) {
				throw new Exception("Enhance supports internal rate factor 1, 2, 4 or 8 only.");
			}
			int targetRate = options.TargetRate > 0 ? options.TargetRate : sampleRate * factor;
			if (windowSize < 1024 || (windowSize & (windowSize - 1)) != 0) {
				throw new Exception("Enhance FFT window size must be a power of two.");
			}

			// Use the configured 10% hop, but force an odd integer hop.
			// The odd hop is intentional: with the gapped harmonic pitch-shift system it avoids
			// repeating exactly the same FFT-bin/OLA alignment pattern on every block.
			int hopIn = ResolveOddEnhanceHopSize(windowSize);
			int outWindowSize = windowSize * factor;
			int hopOut = hopIn * factor;
			int outputFrames = inputFrames * factor;
			double[,] output = new double[outputFrames, channels];
			double[] inWindow = CreateRaisedCosineWindow(windowSize, 1.0);
			double[] outWindow = CreateRaisedCosineWindow(outWindowSize, 2.0);
			double[] outNormWindow = CreateRaisedCosineWindow(outWindowSize, 1.0);
			double[] olaNormWindow = new double[outWindowSize];
			// Dynamic OLA normalization keeps the output stable for arbitrary hop sizes.
			// This is required because the hop is no longer a simple fixed 4x overlap.
			double inverseFftScale = (double)outWindowSize / 2.0;
			for (int i = 0; i < outWindowSize; i++) {
				olaNormWindow[i] = inverseFftScale * outNormWindow[i] * outWindow[i];
			}

			RealFFT fftIn = new RealFFT(windowSize);
			RealFFT fftOut = new RealFFT(outWindowSize);
			double[] inSpectrum = new double[windowSize];
			double[] outSpectrum = new double[outWindowSize];
			double[] magnitudes = new double[(windowSize / 2) + 1];
			double[] phases = new double[(windowSize / 2) + 1];
			double[] harmonicTargetAdvance = CreatePhaseAdvanceTable((outWindowSize / 2) + 1, hopOut, outWindowSize);
			double[] harmonicSourceAdvance = CreatePhaseAdvanceTable((windowSize / 2) + 1, hopOut, outWindowSize);

			int firstStart = -windowSize + hopIn;
			int lastStart = inputFrames - 1;
			int blocksPerChannel = ((lastStart - firstStart) / hopIn) + 1;
			int totalBlocks = Math.Max(1, blocksPerChannel * channels);
			int processedBlocks = 0;
			for (int channel = 0; channel < channels; channel++) {
				double[] outputNorm = new double[outputFrames];
				int harmonicPhaseStateStride = (outWindowSize / 2) + 1;
				int harmonicPhaseStateLength = harmonicPhaseStateStride * 5;
				double[] harmonicPhaseState = new double[harmonicPhaseStateLength];
				bool[] harmonicPhaseInitialized = new bool[harmonicPhaseStateLength];
				double[] harmonicSourcePhaseState = new double[harmonicPhaseStateLength];
				double[] currentBlockPhase = new double[harmonicPhaseStateLength];
				int[] currentBlockPhaseStamp = new int[harmonicPhaseStateLength];
				double[] harmonicMagnitudeOut = new double[(outWindowSize / 2) + 1];
				double[] harmonicPhaseOut = new double[(outWindowSize / 2) + 1];
				double[] harmonicPhaseWeight = new double[(outWindowSize / 2) + 1];
				bool[] harmonicTouched = new bool[(outWindowSize / 2) + 1];
				int[] harmonicTouchedBins = new int[(outWindowSize / 2) + 1];
				double[] levelSlopeX = new double[96];
				double[] levelSlopeY = new double[96];
				double[] levelSlopeSorted = new double[96];
				int blockPhaseStamp = 1;
				for (int blockStart = firstStart; blockStart <= lastStart; blockStart += hopIn) {
					Array.Clear(inSpectrum, 0, inSpectrum.Length);
					int inputCopyStart = Math.Max(0, -blockStart);
					int inputCopyEnd = Math.Min(windowSize, inputFrames - blockStart);
					for (int i = inputCopyStart; i < inputCopyEnd; i++) {
						inSpectrum[i] = input[blockStart + i, channel] * inWindow[i];
					}

					fftIn.ComputeForward(inSpectrum);
					int maxSearchBin = BuildSpectrumMagnitudePhase(inSpectrum, magnitudes, phases, true);
					int enhanceBin = DetectEnhanceBin(magnitudes, maxSearchBin, sampleRate, windowSize, options);
					enhanceBin = LowerEnhanceBinByOctaves(enhanceBin, maxSearchBin, EnhanceLoweringDivisor);

					Array.Clear(outSpectrum, 0, outSpectrum.Length);
					if (enhanceBin > 4) {
						AppendInterpolatedHarmonics(inSpectrum, outSpectrum, enhanceBin, factor, sampleRate, targetRate, windowSize, options, magnitudes, phases, harmonicMagnitudeOut, harmonicPhaseOut, harmonicPhaseWeight, harmonicTouched, harmonicTouchedBins, levelSlopeX, levelSlopeY, levelSlopeSorted, harmonicPhaseState, harmonicPhaseInitialized, harmonicSourcePhaseState, currentBlockPhase, currentBlockPhaseStamp, blockPhaseStamp, harmonicPhaseStateStride, harmonicTargetAdvance, harmonicSourceAdvance, hopOut, outWindowSize, channel, blockStart);
						blockPhaseStamp++;
						if (blockPhaseStamp == Int32.MaxValue) {
							Array.Clear(currentBlockPhaseStamp, 0, currentBlockPhaseStamp.Length);
							blockPhaseStamp = 1;
						}
					} else {
						CopyBaseSpectrum(inSpectrum, outSpectrum, enhanceBin, factor);
					}
					ApplyTargetNyquistFade(outSpectrum, sampleRate * factor, targetRate);

					fftOut.ComputeReverse(outSpectrum);
					int outBlockStart = blockStart * factor;
					int outputAddStart = Math.Max(0, -outBlockStart);
					int outputAddEnd = Math.Min(outWindowSize, outputFrames - outBlockStart);
					for (int i = outputAddStart; i < outputAddEnd; i++) {
						int outputIndex = outBlockStart + i;
						output[outputIndex, channel] += outSpectrum[i] * outWindow[i];
						outputNorm[outputIndex] += olaNormWindow[i];
					}
					processedBlocks++;
					if (progress != null) {
						progress((double)processedBlocks / (double)totalBlocks);
					}
				}

				for (int i = 0; i < outputFrames; i++) {
					double norm = outputNorm[i];
					if (norm > 1.0e-30) {
						output[i, channel] /= norm;
					}
					else {
						output[i, channel] = 0.0;
					}
				}
			}

			return output;
		}

		private static void CopyBaseSpectrum(double[] src, double[] dst, int enhanceBin, int factor) {
			// r75 safety: even without harmonic rendering the complete original spectrum
			// is copied into the destination FFT.  enhanceBin is intentionally ignored
			// here; any later harmonic rendering must only add to the copied spectrum,
			// never replace existing original bins above the enhance boundary.
			CopyOriginalSpectrumToOutput(src, dst, factor);
		}

		private static void CopyOriginalSpectrumToOutput(double[] src, double[] dst, int factor) {
			// r75 safety fallback: the destination FFT must always contain the complete
			// original spectrum. Harmonic rendering is additive only; it must never
			// replace existing original bins, including bins above enhanceBin.
			dst[0] = src[0] * factor;

			int srcNyquistBin = src.Length / 2;
			int srcMaxRegularBin = srcNyquistBin - 1;
			int dstMaxRegularBin = (dst.Length / 2) - 1;
			int maxBin = Math.Min(srcMaxRegularBin, dstMaxRegularBin);

			for (int bin = 1; bin <= maxBin; bin++) {
				int si = bin * 2;
				dst[si] = src[si] * factor;
				dst[si + 1] = src[si + 1] * factor;
			}

			// In the packed real FFT format src[1] is the input Nyquist bin.
			// In an upsampled output FFT this bin becomes a regular complex bin.
			if (srcNyquistBin <= dstMaxRegularBin) {
				int di = srcNyquistBin * 2;
				dst[di] = src[1] * factor;
				dst[di + 1] = 0.0;
			}
		}

		// Legacy helper structs retained for older non-active render helpers below.
		// The active r96 path is AppendInterpolatedHarmonics -> RenderOneOctaveWindowLobeStage.
#pragma warning disable 0649
		private struct HarmonicBlock {
			public int Index;
			public int NominalStart;
			public int NominalEnd;
			public int TargetCount;
			public int RenderStart;
			public int RenderEnd;
			public double Scale;
			public double PhaseOffset;
			public double BoundaryPhase;
			public int TransitionLow;
			public int TransitionHigh;
			public int EndTransitionLow;
			public int EndTransitionHigh;
			public int NextBoundarySafeStart;
			public double PhaseMultiplier;
		}
#pragma warning restore 0649

		private struct SpectrumPoint {
			public double Magnitude;
			public double Phase;
		}

		private static void AppendInterpolatedHarmonics(double[] src, double[] dst, int enhanceBin, int factor, int sampleRate, int targetRate, int fftSize, EnhanceOptions options, double[] magnitudes, double[] phases, double[] magnitudeOut, double[] phaseOut, double[] phaseWeight, bool[] touched, int[] touchedBins, double[] levelSlopeX, double[] levelSlopeY, double[] levelSlopeSorted, double[] harmonicPhaseState, bool[] harmonicPhaseInitialized, double[] harmonicSourcePhaseState, double[] currentBlockPhase, int[] currentBlockPhaseStamp, int blockPhaseStamp, int harmonicPhaseStateStride, double[] harmonicTargetAdvance, double[] harmonicSourceAdvance, int hopOut, int outWindowSize, int channel, int blockStart) {
			// r75 safety: the edited FFT is seeded with the complete original spectrum
			// first. Cascaded +12 octave stages add harmonic material into this same
			// output spectrum; existing original bins are never faded or overwritten.
			CopyOriginalSpectrumToOutput(src, dst, factor);

			int srcStart = Math.Max(1, enhanceBin / 2);
			int srcEnd = Math.Min(enhanceBin, (src.Length / 2) - 1);
			if (srcEnd <= srcStart) return;

			int maxSourceBin = Math.Min((src.Length / 2) - 1, Math.Min(magnitudes.Length, phases.Length) - 2);
			int renderSrcStart = Math.Max(1, ShiftBinDown(srcStart, TransitionDownFactor));
			int renderSrcEnd = Math.Min(maxSourceBin, ShiftBinUp(srcEnd, TransitionUpFactor));
			if (renderSrcEnd < renderSrcStart) return;

			double slopeDbPerOctave = EstimateRobustLevelSlopeDbPerOctave(magnitudes, srcStart, srcEnd, levelSlopeX, levelSlopeY, levelSlopeSorted);
			double startMag = EstimateMagnitudeAtBin(magnitudes, srcStart);
			double endMag = startMag * Math.Pow(10.0, slopeDbPerOctave / 20.0);
			// The spectral slope between enhanceBin/2 and enhanceBin predicts how much a
			// one-octave-up copy should be attenuated.  The r79/r96 harmonic system still
			// contains intentional spectral gaps because it is a pitch-shift style harmonic
			// overlay, not a full broadband resynthesis.  Therefore the effective harmonic
			// base gain is doubled to compensate the missing energy.
			double baseGainCorrection = Clamp(Math.Pow(10.0, slopeDbPerOctave / 20.0), 0.0, MaxHarmonicScale);
			double effectiveBaseGain = baseGainCorrection * 2.0;
			int maxDstBin = (dst.Length / 2) - 1;
			int harmonicTargetMaxBin = maxDstBin;
			int enhancedRate = sampleRate * factor;
			if (targetRate > 0 && targetRate < enhancedRate) {
				long targetBinLong = ((long)targetRate * (long)dst.Length) / ((long)2 * (long)enhancedRate);
				if (targetBinLong < 1L) targetBinLong = 1L;
				if (targetBinLong < harmonicTargetMaxBin) harmonicTargetMaxBin = (int)targetBinLong;
			}

			// Active harmonic renderer:
			// A source bin is treated as a bin-centered windowed sinusoidal component and
			// rebuilt one octave higher through the normalized complex response of the
			// raised-cosine/Hann analysis window.  This is intentionally a harmonic
			// pitch-shift overlay with gaps, because the smoother full-spectrum interpolation
			// attempts smeared the window lobe and caused worse artifacts.  Harmonics are
			// stage-rendered and then added; the already copied original spectrum is never
			// replaced.
			const int maxHarmonicCount = 4;
			int currentStart = srcStart;
			int currentEnd = srcEnd;
			for (int harmonicIndex = 1; harmonicIndex <= maxHarmonicCount; harmonicIndex++) {
				int nominalStart = Math.Max(1, currentStart * 2);
				int nominalEnd = Math.Min(harmonicTargetMaxBin, currentEnd * 2);
				if (nominalStart > harmonicTargetMaxBin || nominalEnd < nominalStart) break;

				int readStart = Math.Max(1, ShiftBinDown(currentStart, TransitionDownFactor));
				int readEnd = Math.Min(harmonicTargetMaxBin, ShiftBinUp(currentEnd, TransitionUpFactor));
				if (readEnd <= readStart) break;

				int transitionLow = ShiftBinDown(nominalStart, TransitionDownFactor);
				int transitionHigh = ShiftBinUp(nominalStart, TransitionUpFactor);
				int endTransitionLow = ShiftBinDown(nominalEnd + 1, TransitionDownFactor);
				int endTransitionHigh = ShiftBinUp(nominalEnd + 1, TransitionUpFactor);

				int touchedCount = 0;
				RenderOneOctaveWindowLobeStage(dst, readStart, readEnd, harmonicTargetMaxBin, harmonicIndex, effectiveBaseGain, magnitudeOut, phaseOut, touched, touchedBins, ref touchedCount, harmonicPhaseState, harmonicPhaseInitialized, harmonicSourcePhaseState, currentBlockPhase, currentBlockPhaseStamp, blockPhaseStamp, harmonicPhaseStateStride, hopOut, outWindowSize, nominalStart, nominalEnd, transitionLow, transitionHigh, endTransitionLow, endTransitionHigh);
				FinalizeComplexStageToSpectrum(dst, magnitudeOut, phaseOut, touched, touchedBins, touchedCount);

				currentStart = nominalStart;
				currentEnd = nominalEnd;
			}

			ExportFirstFftDebugIfNeeded(src, dst, sampleRate, fftSize, factor, channel, blockStart, enhanceBin, srcStart, srcEnd, renderSrcStart, renderSrcEnd, startMag, endMag, slopeDbPerOctave, effectiveBaseGain);
		}


		private static void RenderOneOctaveWindowLobeStage(double[] spectrum, int readStart, int readEnd, int maxDstBin, int harmonicIndex, double gain, double[] stageRe, double[] stageIm, bool[] touched, int[] touchedBins, ref int touchedCount, double[] harmonicPhaseState, bool[] harmonicPhaseInitialized, double[] harmonicSourcePhaseState, double[] currentBlockPhase, int[] currentBlockPhaseStamp, int blockPhaseStamp, int harmonicPhaseStateStride, int hopOut, int outWindowSize, int nominalStart, int nominalEnd, int transitionLow, int transitionHigh, int endTransitionLow, int endTransitionHigh) {
			if (spectrum == null || spectrum.Length < 4) return;
			if (stageRe == null || stageIm == null || touched == null || touchedBins == null) return;
			if (readEnd <= readStart || gain == 0.0) return;

			int maxSourceBin = Math.Min(readEnd, (spectrum.Length / 2) - 1);
			if (maxSourceBin <= readStart) return;

			// For the normalized raised-cosine/Hann analysis window a bin-centered
			// sinusoidal component has non-zero main-lobe samples at d = -1, 0, +1
			// and zeros at d = +/-2.  Rendering only these three bins is therefore
			// enough for the exact bin-centered sinus case and keeps the hot loop cheap.
			const int kernelRadius = 1;

			for (int sourceBin = readStart; sourceBin <= maxSourceBin; sourceBin++) {
				double sourceRe, sourceIm;
				ReadSpectrumComplex(spectrum, sourceBin, out sourceRe, out sourceIm);
				double sourceMagnitude = Math.Sqrt((sourceRe * sourceRe) + (sourceIm * sourceIm));
				if (sourceMagnitude <= 0.0) continue;

				double sourcePhase = Math.Atan2(sourceIm, sourceRe);
				double targetCenterBin = (double)sourceBin * 2.0;
				int targetCenterInt = sourceBin * 2;
				if (targetCenterInt + kernelRadius < 1 || targetCenterInt - kernelRadius > maxDstBin) continue;

				double targetPhase = GetOneOctavePartialTargetPhase(sourceBin, harmonicIndex, (double)sourceBin, targetCenterBin, sourcePhase, harmonicPhaseState, harmonicPhaseInitialized, harmonicSourcePhaseState, currentBlockPhase, currentBlockPhaseStamp, blockPhaseStamp, harmonicPhaseStateStride, hopOut, outWindowSize);
				double anchorRe = Math.Cos(targetPhase) * sourceMagnitude * gain;
				double anchorIm = Math.Sin(targetPhase) * sourceMagnitude * gain;

				for (int offset = -kernelRadius; offset <= kernelRadius; offset++) {
					int targetBin = targetCenterInt + offset;
					if (targetBin < 1 || targetBin > maxDstBin) continue;

					double fade = GetHarmonicTargetFade(targetBin, nominalStart, nominalEnd, transitionLow, transitionHigh, endTransitionLow, endTransitionHigh);
					if (fade <= 0.0) continue;

					double kernelRe, kernelIm;
					GetRaisedCosineSpectrumKernel((double)offset, out kernelRe, out kernelIm);
					if (kernelRe == 0.0 && kernelIm == 0.0) continue;

					double re = ((anchorRe * kernelRe) - (anchorIm * kernelIm)) * fade;
					double im = ((anchorRe * kernelIm) + (anchorIm * kernelRe)) * fade;
					AddComplexStageContribution(targetBin, re, im, stageRe, stageIm, touched, touchedBins, ref touchedCount);
				}
			}
		}


		private static void RenderOneOctaveSpectrumResample(double[] spectrum, int readStart, int readEnd, int maxDstBin, int harmonicIndex, double gain, double[] stageRe, double[] stageIm, bool[] touched, int[] touchedBins, ref int touchedCount, double[] harmonicPhaseState, bool[] harmonicPhaseInitialized, double[] harmonicSourcePhaseState, double[] currentBlockPhase, int[] currentBlockPhaseStamp, int blockPhaseStamp, int harmonicPhaseStateStride, int hopOut, int outWindowSize, int nominalStart, int nominalEnd, int transitionLow, int transitionHigh, int endTransitionLow, int endTransitionHigh) {
			if (spectrum == null || spectrum.Length < 4) return;
			if (stageRe == null || stageIm == null || touched == null || touchedBins == null) return;
			if (readEnd <= readStart || gain == 0.0) return;

			int maxSourceBin = Math.Min(readEnd, (spectrum.Length / 2) - 1);
			if (maxSourceBin <= readStart) return;

			int targetStart = Math.Max(1, Math.Max(transitionLow, readStart * 2));
			int targetEnd = Math.Min(maxDstBin, Math.Min(endTransitionHigh, readEnd * 2));
			if (targetEnd < targetStart) return;

			for (int targetBin = targetStart; targetBin <= targetEnd; targetBin++) {
				double fade = GetHarmonicTargetFade(targetBin, nominalStart, nominalEnd, transitionLow, transitionHigh, endTransitionLow, endTransitionHigh);
				if (fade <= 0.0) continue;

				double sourcePos = (double)targetBin * 0.5;
				if (sourcePos < (double)readStart || sourcePos > (double)maxSourceBin) continue;

				double sourceMagnitude, sourcePhase;
				if (!InterpolateSpectrumMagnitudePhase(spectrum, sourcePos, maxSourceBin, out sourceMagnitude, out sourcePhase)) continue;
				if (sourceMagnitude <= 0.0) continue;

				double targetPhase = GetOneOctaveResampledTargetPhase(targetBin, harmonicIndex, sourcePos, sourcePhase, harmonicPhaseState, harmonicPhaseInitialized, harmonicSourcePhaseState, currentBlockPhase, currentBlockPhaseStamp, blockPhaseStamp, harmonicPhaseStateStride, hopOut, outWindowSize);
				double magnitude = sourceMagnitude * gain * fade;
				AddComplexStageContribution(targetBin, Math.Cos(targetPhase) * magnitude, Math.Sin(targetPhase) * magnitude, stageRe, stageIm, touched, touchedBins, ref touchedCount);
			}
		}

		private static bool InterpolateSpectrumMagnitudePhase(double[] spectrum, double sourcePos, int maxSourceBin, out double magnitude, out double phase) {
			magnitude = 0.0;
			phase = 0.0;
			if (spectrum == null || sourcePos < 1.0 || maxSourceBin < 1) return false;

			int leftBin = (int)Math.Floor(sourcePos);
			if (leftBin < 1) leftBin = 1;
			if (leftBin > maxSourceBin) leftBin = maxSourceBin;
			int rightBin = leftBin + 1;
			if (rightBin > maxSourceBin) rightBin = leftBin;

			double frac = sourcePos - (double)leftBin;
			if (rightBin == leftBin) frac = 0.0;
			else frac = Clamp(frac, 0.0, 1.0);

			double leftRe, leftIm, rightRe, rightIm;
			ReadSpectrumComplex(spectrum, leftBin, out leftRe, out leftIm);
			ReadSpectrumComplex(spectrum, rightBin, out rightRe, out rightIm);
			double leftMag = Math.Sqrt((leftRe * leftRe) + (leftIm * leftIm));
			double rightMag = Math.Sqrt((rightRe * rightRe) + (rightIm * rightIm));
			if (leftMag <= 0.0 && rightMag <= 0.0) return false;

			double leftPhase = Math.Atan2(leftIm, leftRe);
			double rightPhase = Math.Atan2(rightIm, rightRe);
			magnitude = leftMag + ((rightMag - leftMag) * frac);
			phase = WrapPhase(leftPhase + (WrapPhase(rightPhase - leftPhase) * frac));
			return true;
		}

		private static double GetOneOctaveResampledTargetPhase(int targetBin, int harmonicIndex, double sourcePos, double sourcePhase, double[] harmonicPhaseState, bool[] harmonicPhaseInitialized, double[] harmonicSourcePhaseState, double[] currentBlockPhase, int[] currentBlockPhaseStamp, int blockPhaseStamp, int harmonicPhaseStateStride, int hopOut, int outWindowSize) {
			if (harmonicPhaseState == null || harmonicPhaseInitialized == null || harmonicSourcePhaseState == null || currentBlockPhase == null || currentBlockPhaseStamp == null) return WrapPhase(sourcePhase * 2.0);
			if (hopOut <= 0 || outWindowSize <= 0 || harmonicPhaseStateStride <= 0) return WrapPhase(sourcePhase * 2.0);
			int stateIndex = GetHarmonicPhaseStateIndex(targetBin, harmonicIndex, harmonicPhaseStateStride, harmonicPhaseState.Length);
			if (stateIndex < 0) return WrapPhase(sourcePhase * 2.0);
			if (stateIndex >= harmonicSourcePhaseState.Length || stateIndex >= currentBlockPhase.Length || stateIndex >= currentBlockPhaseStamp.Length) return WrapPhase(sourcePhase * 2.0);

			double phaseAdvanceScale = TwoPi * (double)hopOut / (double)outWindowSize;
			double initialPhase = WrapPhase(sourcePhase * 2.0);
			if (currentBlockPhaseStamp[stateIndex] != blockPhaseStamp) {
				if (harmonicPhaseInitialized[stateIndex]) {
					double expectedSourceAdvance = phaseAdvanceScale * sourcePos;
					double sourceDeviation = WrapPhase(sourcePhase - harmonicSourcePhaseState[stateIndex] - expectedSourceAdvance);
					double trueSourceBin = sourcePos + (sourceDeviation / phaseAdvanceScale);
					double trueTargetBin = trueSourceBin * 2.0;
					double targetAdvance = phaseAdvanceScale * trueTargetBin;
					harmonicPhaseState[stateIndex] = WrapPhase(harmonicPhaseState[stateIndex] + targetAdvance);
				} else {
					harmonicPhaseState[stateIndex] = initialPhase;
					harmonicPhaseInitialized[stateIndex] = true;
				}
				harmonicSourcePhaseState[stateIndex] = sourcePhase;
				currentBlockPhase[stateIndex] = harmonicPhaseState[stateIndex];
				currentBlockPhaseStamp[stateIndex] = blockPhaseStamp;
			}
			return currentBlockPhase[stateIndex];
		}


		private static double EstimatePeakOffset(double leftMag, double centerMag, double rightMag) {
			double denom = leftMag - (2.0 * centerMag) + rightMag;
			if (Math.Abs(denom) < 1.0e-30) return 0.0;
			double offset = 0.5 * (leftMag - rightMag) / denom;
			return Clamp(offset, -0.5, 0.5);
		}

		private static double GetOneOctavePartialTargetPhase(int sourceBin, int harmonicIndex, double sourceCenterBin, double targetCenterBin, double sourcePhase, double[] harmonicPhaseState, bool[] harmonicPhaseInitialized, double[] harmonicSourcePhaseState, double[] currentBlockPhase, int[] currentBlockPhaseStamp, int blockPhaseStamp, int harmonicPhaseStateStride, int hopOut, int outWindowSize) {
			if (harmonicPhaseState == null || harmonicPhaseInitialized == null || harmonicSourcePhaseState == null || currentBlockPhase == null || currentBlockPhaseStamp == null) return WrapPhase(sourcePhase * 2.0);
			if (hopOut <= 0 || outWindowSize <= 0 || harmonicPhaseStateStride <= 0) return WrapPhase(sourcePhase * 2.0);
			int stateIndex = GetHarmonicPhaseStateIndex(sourceBin, harmonicIndex, harmonicPhaseStateStride, harmonicPhaseState.Length);
			if (stateIndex < 0) return WrapPhase(sourcePhase * 2.0);
			if (stateIndex >= harmonicSourcePhaseState.Length || stateIndex >= currentBlockPhase.Length || stateIndex >= currentBlockPhaseStamp.Length) return WrapPhase(sourcePhase * 2.0);

			double phaseAdvanceScale = TwoPi * (double)hopOut / (double)outWindowSize;
			double initialPhase = WrapPhase(sourcePhase * 2.0);
			if (currentBlockPhaseStamp[stateIndex] != blockPhaseStamp) {
				if (harmonicPhaseInitialized[stateIndex]) {
					double expectedSourceAdvance = phaseAdvanceScale * sourceCenterBin;
					double sourceDeviation = WrapPhase(sourcePhase - harmonicSourcePhaseState[stateIndex] - expectedSourceAdvance);
					double trueSourceBin = sourceCenterBin + (sourceDeviation / phaseAdvanceScale);
					double trueTargetBin = trueSourceBin * 2.0;
					double targetAdvance = phaseAdvanceScale * trueTargetBin;
					harmonicPhaseState[stateIndex] = WrapPhase(harmonicPhaseState[stateIndex] + targetAdvance);
				} else {
					harmonicPhaseState[stateIndex] = initialPhase;
					harmonicPhaseInitialized[stateIndex] = true;
				}
				harmonicSourcePhaseState[stateIndex] = sourcePhase;
				currentBlockPhase[stateIndex] = harmonicPhaseState[stateIndex];
				currentBlockPhaseStamp[stateIndex] = blockPhaseStamp;
			}
			return currentBlockPhase[stateIndex];
		}

		private static void GetRaisedCosineSpectrumKernel(double d, out double re, out double im) {
			// Normalized complex main-lobe response of the raised-cosine/Hann analysis
			// window. K(0)=1, K(+/-1)=-0.5 and K(+/-2)=0.  This gives both magnitude
			// and the relative phase that neighbouring FFT bins must have for a single
			// windowed sinusoidal partial.
			double ad = Math.Abs(d);
			if (ad >= 2.0) {
				re = 0.0;
				im = 0.0;
				return;
			}
			if (ad < 1.0e-12) {
				re = 1.0;
				im = 0.0;
				return;
			}
			if (Math.Abs(ad - 1.0) < 1.0e-8) {
				re = -0.5;
				im = 0.0;
				return;
			}

			double sinc = Math.Sin(Math.PI * d) / (Math.PI * d);
			double envelope = sinc / (1.0 - (d * d));
			double phase = -Math.PI * d;
			re = envelope * Math.Cos(phase);
			im = envelope * Math.Sin(phase);
		}

		private static void AddComplexStageContribution(int bin, double re, double im, double[] stageRe, double[] stageIm, bool[] touched, int[] touchedBins, ref int touchedCount) {
			if (bin < 1 || bin >= stageRe.Length || bin >= stageIm.Length) return;
			if (!touched[bin]) {
				touched[bin] = true;
				touchedBins[touchedCount++] = bin;
			}
			stageRe[bin] += re;
			stageIm[bin] += im;
		}

		private static void FinalizeComplexStageToSpectrum(double[] spectrum, double[] stageRe, double[] stageIm, bool[] touched, int[] touchedBins, int touchedCount) {
			for (int i = 0; i < touchedCount; i++) {
				int bin = touchedBins[i];
				int si = bin * 2;
				if (si + 1 < spectrum.Length) {
					// r75: harmonics are overlays.  Preserve the complete original
					// spectrum and any earlier cascaded stage by complex addition.
					spectrum[si] += stageRe[bin];
					spectrum[si + 1] += stageIm[bin];
				}
				stageRe[bin] = 0.0;
				stageIm[bin] = 0.0;
				touched[bin] = false;
			}
		}

		private static void ComplexDivide(double ar, double ai, double br, double bi, out double qr, out double qi) {
			double denom = (br * br) + (bi * bi);
			if (denom <= 1.0e-300) {
				qr = 0.0;
				qi = 0.0;
				return;
			}
			qr = ((ar * br) + (ai * bi)) / denom;
			qi = ((ai * br) - (ar * bi)) / denom;
		}

		private static double GetSpectrumMagnitude(double[] spectrum, int bin) {
			double re, im;
			ReadSpectrumComplex(spectrum, bin, out re, out im);
			return Math.Sqrt((re * re) + (im * im));
		}

		private static void ReadSpectrumComplex(double[] spectrum, int bin, out double re, out double im) {
			int maxRegularBin = (spectrum.Length / 2) - 1;
			if (bin < 1 || bin > maxRegularBin) {
				re = 0.0;
				im = 0.0;
				return;
			}
			int si = bin * 2;
			re = spectrum[si];
			im = spectrum[si + 1];
		}

		private static double GetHarmonicTargetFade(int bin, int nominalStart, int nominalEnd, int transitionLow, int transitionHigh, int endTransitionLow, int endTransitionHigh) {
			double fadeIn = BoundaryBlendPosition(bin, transitionLow, transitionHigh, nominalStart);
			double fadeOut = 1.0 - BoundaryBlendPosition(bin, endTransitionLow, endTransitionHigh, nominalEnd + 1);
			return fadeIn * fadeOut;
		}

		private static double GetR3PitchScaleHarmonicPhase(int sourceBin, int harmonicIndex, double pitchScale, double sourcePhase, double[] harmonicPhaseState, bool[] harmonicPhaseInitialized, double[] harmonicSourcePhaseState, double[] currentBlockPhase, int[] currentBlockPhaseStamp, int blockPhaseStamp, int harmonicPhaseStateStride, int hopOut, int outWindowSize) {
			if (harmonicPhaseState == null || harmonicPhaseInitialized == null || harmonicSourcePhaseState == null || currentBlockPhase == null || currentBlockPhaseStamp == null) return WrapPhase(sourcePhase * pitchScale);
			if (hopOut <= 0 || outWindowSize <= 0 || harmonicPhaseStateStride <= 0) return WrapPhase(sourcePhase * pitchScale);

			// State is intentionally keyed by harmonic + sourceBin, not targetBin.
			// This keeps neighbouring source partials independent even when their
			// shifted magnitudes distribute into the same target bin pair.
			int stateIndex = GetHarmonicPhaseStateIndex(sourceBin, harmonicIndex, harmonicPhaseStateStride, harmonicPhaseState.Length);
			if (stateIndex < 0) return WrapPhase(sourcePhase * pitchScale);
			if (stateIndex >= harmonicSourcePhaseState.Length || stateIndex >= currentBlockPhase.Length || stateIndex >= currentBlockPhaseStamp.Length) return WrapPhase(sourcePhase * pitchScale);

			double phaseAdvanceScale = TwoPi * (double)hopOut / (double)outWindowSize;
			double initialPhase = WrapPhase(sourcePhase * pitchScale);

			if (currentBlockPhaseStamp[stateIndex] != blockPhaseStamp) {
				if (harmonicPhaseInitialized[stateIndex]) {
					double expectedSourceAdvance = phaseAdvanceScale * (double)sourceBin;
					double sourceDeviation = WrapPhase(sourcePhase - harmonicSourcePhaseState[stateIndex] - expectedSourceAdvance);
					double trueSourceBin = (double)sourceBin + (sourceDeviation / phaseAdvanceScale);
					double trueTargetBin = trueSourceBin * pitchScale;
					double targetAdvance = phaseAdvanceScale * trueTargetBin;
					harmonicPhaseState[stateIndex] = WrapPhase(harmonicPhaseState[stateIndex] + targetAdvance);
				} else {
					harmonicPhaseState[stateIndex] = initialPhase;
					harmonicPhaseInitialized[stateIndex] = true;
				}
				harmonicSourcePhaseState[stateIndex] = sourcePhase;
				currentBlockPhase[stateIndex] = harmonicPhaseState[stateIndex];
				currentBlockPhaseStamp[stateIndex] = blockPhaseStamp;
			}

			return currentBlockPhase[stateIndex];
		}


		private static double EstimateRobustLevelSlopeDbPerOctave(double[] magnitudes, int startBin, int endBin, double[] xs, double[] ys, double[] sorted) {
			const double minMagnitude = 1.0e-30;
			const int segmentCount = 12;

			if (magnitudes == null || magnitudes.Length == 0) return 0.0;
			int maxBin = magnitudes.Length - 1;
			if (startBin < 1) startBin = 1;
			if (endBin > maxBin) endBin = maxBin;
			if (endBin <= startBin) return 0.0;
			if (xs == null || ys == null || xs.Length < segmentCount || ys.Length < segmentCount) return 0.0;

			double octaveSpan = Math.Log((double)endBin / (double)startBin, 2.0);
			if (octaveSpan <= 1.0e-12) return 0.0;

			int used = 0;
			int previousEnd = startBin - 1;

			// r72: Use 12 logarithmic segments over the measured octave. Each segment
			// level is the RMS magnitude of all bins in that segment, converted to dB.
			// This tracks the broad spectral envelope and is less sensitive to narrow
			// notches than the previous fine-grained point sampling.
			for (int segment = 0; segment < segmentCount; segment++) {
				int binStart = previousEnd + 1;
				if (binStart > endBin) break;

				int binEnd;
				if (segment == segmentCount - 1) {
					binEnd = endBin;
				} else {
					double segmentEndD = (double)startBin * Math.Pow(2.0, octaveSpan * (double)(segment + 1) / (double)segmentCount);
					binEnd = (int)Math.Floor(segmentEndD);
					if (binEnd < binStart) binEnd = binStart;
					if (binEnd > endBin) binEnd = endBin;
				}

				double sumSquares = 0.0;
				int count = 0;
				for (int bin = binStart; bin <= binEnd; bin++) {
					double mag = magnitudes[bin];
					if (mag < minMagnitude) mag = minMagnitude;
					sumSquares += mag * mag;
					count++;
				}

				if (count > 0) {
					double rmsMag = Math.Sqrt(sumSquares / (double)count);
					double centerBin = Math.Sqrt((double)binStart * (double)binEnd);
					xs[used] = Math.Log(centerBin / (double)startBin, 2.0);
					ys[used] = MagnitudeToDb(rmsMag);
					used++;
				}

				previousEnd = binEnd;
			}

			if (used < 2) {
				double firstMag = Math.Max(EstimateMagnitudeAtBin(magnitudes, startBin), minMagnitude);
				double lastMag = Math.Max(EstimateMagnitudeAtBin(magnitudes, endBin), minMagnitude);
				return (MagnitudeToDb(lastMag) - MagnitudeToDb(firstMag)) / octaveSpan;
			}

			double sumX = 0.0;
			double sumY = 0.0;
			double sumXX = 0.0;
			double sumXY = 0.0;

			for (int i = 0; i < used; i++) {
				double x = xs[i];
				double y = ys[i];
				sumX += x;
				sumY += y;
				sumXX += x * x;
				sumXY += x * y;
			}

			double denominator = ((double)used * sumXX) - (sumX * sumX);
			if (Math.Abs(denominator) < 1.0e-20) return 0.0;
			return (((double)used * sumXY) - (sumX * sumY)) / denominator;
		}

		private static double EstimateMagnitudeAtBin(double[] magnitudes, int centerBin) {
			if (magnitudes == null || magnitudes.Length == 0) return 0.0;
			int maxBin = magnitudes.Length - 1;
			if (centerBin < 1) centerBin = 1;
			if (centerBin > maxBin) centerBin = maxBin;

			double sum = magnitudes[centerBin];
			int count = 1;
			int bin0 = centerBin - 1;
			int bin2 = centerBin + 1;
			if (bin0 >= 1) {
				sum += magnitudes[bin0];
				count++;
			}
			if (bin2 <= maxBin) {
				sum += magnitudes[bin2];
				count++;
			}
			return sum / (double)count;
		}

		private static double PercentileFromSorted(double[] sorted, int count, double percentile) {
			if (sorted == null || count <= 0) return 0.0;
			if (percentile <= 0.0) return sorted[0];
			if (percentile >= 1.0) return sorted[count - 1];

			double index = percentile * (double)(count - 1);
			int lo = (int)Math.Floor(index);
			int hi = (int)Math.Ceiling(index);
			if (lo == hi) return sorted[lo];

			double frac = index - (double)lo;
			return sorted[lo] + ((sorted[hi] - sorted[lo]) * frac);
		}

		private static void ExportFirstFftDebugIfNeeded(double[] originalSpectrum, double[] editedSpectrum, int sampleRate, int fftSize, int factor, int channel, int blockStart, int enhanceBin, int srcStart, int srcEnd, int renderSrcStart, int renderSrcEnd, double startMag, double endMag, double slopeDbPerOctave, double baseGainCorrection) {
			// Debug export for the first enhance FFT block. Set to false to disable all file output.
			bool exportFirstFftDebug = false;
			// true = export the first fully real input block at blockStart 0.
			bool exportFirstFullInputBlockOnly = true;

			if (!exportFirstFftDebug) return;
			if (channel != 0) return;
			if (exportFirstFullInputBlockOnly && blockStart != 0) return;

			string directory = Directory.GetCurrentDirectory();
			ExportPackedSpectrum(Path.Combine(directory, "fft_original.txt"), originalSpectrum, sampleRate, fftSize);
			ExportPackedSpectrum(Path.Combine(directory, "fft_edit.txt"), editedSpectrum, sampleRate * factor, fftSize * factor);

			using (StreamWriter writer = new StreamWriter(Path.Combine(directory, "fft_data.txt"), false)) {
				writer.WriteLine("key	value");
				writer.WriteLine("channel	" + channel.ToString(CultureInfo.InvariantCulture));
				writer.WriteLine("block_start	" + blockStart.ToString(CultureInfo.InvariantCulture));
				writer.WriteLine("input_sample_rate	" + sampleRate.ToString(CultureInfo.InvariantCulture));
				writer.WriteLine("output_sample_rate	" + (sampleRate * factor).ToString(CultureInfo.InvariantCulture));
				writer.WriteLine("factor	" + factor.ToString(CultureInfo.InvariantCulture));
				writer.WriteLine("fft_size_original	" + fftSize.ToString(CultureInfo.InvariantCulture));
				writer.WriteLine("fft_size_edit	" + (fftSize * factor).ToString(CultureInfo.InvariantCulture));
				writer.WriteLine("enhance_bin	" + enhanceBin.ToString(CultureInfo.InvariantCulture));
				writer.WriteLine("enhance_bin_hz	" + (((double)enhanceBin * (double)sampleRate / (double)fftSize).ToString("0.##########", CultureInfo.InvariantCulture)));
				writer.WriteLine("src_start_bin	" + srcStart.ToString(CultureInfo.InvariantCulture));
				writer.WriteLine("src_start_hz	" + (((double)srcStart * (double)sampleRate / (double)fftSize).ToString("0.##########", CultureInfo.InvariantCulture)));
				writer.WriteLine("src_end_bin	" + srcEnd.ToString(CultureInfo.InvariantCulture));
				writer.WriteLine("src_end_hz	" + (((double)srcEnd * (double)sampleRate / (double)fftSize).ToString("0.##########", CultureInfo.InvariantCulture)));
				writer.WriteLine("render_src_start_bin	" + renderSrcStart.ToString(CultureInfo.InvariantCulture));
				writer.WriteLine("render_src_start_hz	" + (((double)renderSrcStart * (double)sampleRate / (double)fftSize).ToString("0.##########", CultureInfo.InvariantCulture)));
				writer.WriteLine("render_src_end_bin	" + renderSrcEnd.ToString(CultureInfo.InvariantCulture));
				writer.WriteLine("render_src_end_hz	" + (((double)renderSrcEnd * (double)sampleRate / (double)fftSize).ToString("0.##########", CultureInfo.InvariantCulture)));
				writer.WriteLine("level_down_linear_enhance_half	" + startMag.ToString("0.################", CultureInfo.InvariantCulture));
				writer.WriteLine("level_down_db_enhance_half	" + MagnitudeToDb(startMag).ToString("0.######", CultureInfo.InvariantCulture));
				writer.WriteLine("level_up_linear_enhance	" + endMag.ToString("0.################", CultureInfo.InvariantCulture));
				writer.WriteLine("level_up_db_enhance	" + MagnitudeToDb(endMag).ToString("0.######", CultureInfo.InvariantCulture));
				writer.WriteLine("level_slope_db_per_octave	" + slopeDbPerOctave.ToString("0.######", CultureInfo.InvariantCulture));
				writer.WriteLine("base_gain_correction_linear	" + baseGainCorrection.ToString("0.################", CultureInfo.InvariantCulture));
				writer.WriteLine("base_gain_correction_db	" + MagnitudeToDb(baseGainCorrection).ToString("0.######", CultureInfo.InvariantCulture));
				writer.WriteLine("debug_export_first_full_input_block_only	" + exportFirstFullInputBlockOnly.ToString(CultureInfo.InvariantCulture));
			}
		}

		private static void ExportPackedSpectrum(string path, double[] spectrum, int sampleRate, int fftSize) {
			int nyquistBin = fftSize / 2;
			using (StreamWriter writer = new StreamWriter(path, false)) {
				writer.WriteLine("bin	frequency_hz	level_db	phase_deg");
				for (int bin = 0; bin <= nyquistBin; bin++) {
					double re;
					double im;
					ReadPackedSpectrumBin(spectrum, fftSize, bin, out re, out im);
					double magnitude = Math.Sqrt((re * re) + (im * im));
					double phase = Math.Atan2(im, re) * 180.0 / Math.PI;
					double frequency = (double)bin * (double)sampleRate / (double)fftSize;
					writer.WriteLine(bin.ToString(CultureInfo.InvariantCulture) + "	" + frequency.ToString("0.##########", CultureInfo.InvariantCulture) + "	" + MagnitudeToDb(magnitude).ToString("0.######", CultureInfo.InvariantCulture) + "	" + phase.ToString("0.######", CultureInfo.InvariantCulture));
				}
			}
		}

		private static void ReadPackedSpectrumBin(double[] spectrum, int fftSize, int bin, out double re, out double im) {
			int nyquistBin = fftSize / 2;
			if (bin <= 0) {
				re = spectrum[0];
				im = 0.0;
				return;
			}
			if (bin >= nyquistBin) {
				re = spectrum[1];
				im = 0.0;
				return;
			}
			int index = bin * 2;
			re = spectrum[index];
			im = spectrum[index + 1];
		}

		private static double MagnitudeToDb(double magnitude) {
			return 20.0 * Math.Log10(Math.Max(magnitude, 1.0e-300));
		}

		private static void AddMagnitudeContribution(int bin, double magnitude, double phase, double[] magnitudeOut, double[] phaseOut, double[] phaseWeight, bool[] touched, int[] touchedBins, ref int touchedCount) {
			if (bin < 1 || bin >= magnitudeOut.Length || magnitude <= 0.0) return;
			if (!touched[bin]) {
				touched[bin] = true;
				touchedBins[touchedCount++] = bin;
			}
			magnitudeOut[bin] += magnitude;
			if (magnitude >= phaseWeight[bin]) {
				phaseWeight[bin] = magnitude;
				phaseOut[bin] = WrapPhase(phase);
			}
		}

		private static double[] BuildCascadedOutPhase(double[] sourcePhases, int enhanceBin, int maxSourceBin, int maxDstBin) {
			double[] outPhase = new double[(maxDstBin + 1) * 2];
			int originalEnd = Math.Min(Math.Min(enhanceBin, maxSourceBin), maxDstBin);
			for (int bin = 0; bin <= originalEnd; bin++) {
				outPhase[bin] = WrapPhase(sourcePhases[bin]);
			}

			int currentBoundary = Math.Max(1, originalEnd);
			while (currentBoundary < maxDstBin) {
				int targetStart = currentBoundary;
				int targetEnd = Math.Min(maxDstBin, currentBoundary * 2);
				if (targetEnd <= targetStart) break;

				double sourceStart = (double)targetStart * 0.5;
				double sourceStartPhase = InterpolatePhaseFromOutPhase(outPhase, sourceStart, targetStart);
				double boundaryPhase = WrapPhase(outPhase[targetStart]);
				double offset = WrapPhase(boundaryPhase - sourceStartPhase);

				for (int bin = targetStart; bin <= targetEnd; bin++) {
					double sourcePos = (double)bin * 0.5;
					double sourcePhase = InterpolatePhaseFromOutPhase(outPhase, sourcePos, targetStart);
					outPhase[bin] = WrapPhase(sourcePhase + offset);
				}

				currentBoundary = targetEnd;
			}

			return outPhase;
		}

		private static double InterpolatePhaseFromOutPhase(double[] outPhase, double pos, int maxReadyBin) {
			if (pos <= 0.0) return WrapPhase(outPhase[0]);
			int leftBin = (int)Math.Floor(pos);
			double frac = pos - (double)leftBin;
			if (leftBin < 0) return WrapPhase(outPhase[0]);
			if (leftBin >= maxReadyBin) return WrapPhase(outPhase[maxReadyBin]);
			int rightBin = Math.Min(maxReadyBin, leftBin + 1);
			double leftPhase = WrapPhase(outPhase[leftBin]);
			double delta = ShortestAngleDelta(leftPhase, outPhase[rightBin]);
			return WrapPhase(leftPhase + (delta * frac));
		}

		private static double RenderHarmonicMagnitude(int bin, HarmonicBlock block, int srcStart, int srcCount, double[] magnitudes) {
			int local = bin - block.NominalStart;

			double pos = (block.TargetCount <= 1) ? 0.0 : ((double)local * (double)(srcCount - 1) / (double)(block.TargetCount - 1));
			int posFloor = (int)Math.Floor(pos);
			double frac = pos - (double)posFloor;
			int maxSourceBin = magnitudes.Length - 2;
			int leftBin = srcStart + posFloor;
			if (leftBin < 1) {
				leftBin = 1;
				frac = 0.0;
			} else if (leftBin >= maxSourceBin) {
				leftBin = maxSourceBin;
				frac = 0.0;
			}
			int rightBin = Math.Min(maxSourceBin, leftBin + 1);

			return Lerp(magnitudes[leftBin], magnitudes[rightBin], frac) * block.Scale;
		}

		private static SpectrumPoint ReadOriginalSpectrumPoint(double[] src, int bin) {
			SpectrumPoint point = new SpectrumPoint();
			int maxRegularBin = (src.Length / 2) - 1;
			if (bin < 1 || bin > maxRegularBin) {
				point.Magnitude = 0.0;
				point.Phase = 0.0;
				return point;
			}

			int si = bin * 2;
			double re = src[si];
			double im = src[si + 1];
			point.Magnitude = Math.Sqrt((re * re) + (im * im));
			point.Phase = Math.Atan2(im, re);
			return point;
		}

		private static SpectrumPoint RenderHarmonicSpectrumPoint(int bin, HarmonicBlock block, int srcStart, int srcEnd, int srcCount, int harmonicTargetCount, double[] magnitudes, double[] phases) {
			return RenderHarmonicSpectrumPoint(bin, block, srcStart, srcEnd, srcCount, harmonicTargetCount, magnitudes, phases, null, null, null, null, null, 0, 0, null, null, 0, 0);
		}

		private static SpectrumPoint RenderHarmonicSpectrumPoint(int bin, HarmonicBlock block, int srcStart, int srcEnd, int srcCount, int harmonicTargetCount, double[] magnitudes, double[] phases, double[] harmonicPhaseState, bool[] harmonicPhaseInitialized, double[] harmonicSourcePhaseState, double[] currentBlockPhase, int[] currentBlockPhaseStamp, int blockPhaseStamp, int harmonicPhaseStateStride, double[] harmonicTargetAdvance, double[] harmonicSourceAdvance, int hopOut, int outWindowSize) {
			int local = bin - block.NominalStart;

			// r40: do not clamp the source position before interpolation.
			// For transition bins below NominalStart or above NominalEnd, the harmonic
			// block is intentionally rendered larger and maps to source bins below
			// srcStart or above srcEnd. Only the final source bin indices are clamped
			// to the available FFT range.
			double pos = (block.TargetCount <= 1) ? 0.0 : ((double)local * (double)(srcCount - 1) / (double)(block.TargetCount - 1));
			int posFloor = (int)Math.Floor(pos);
			double frac = pos - (double)posFloor;
			int maxSourceBin = magnitudes.Length - 2;
			int leftBin = srcStart + posFloor;
			if (leftBin < 1) {
				leftBin = 1;
				frac = 0.0;
			} else if (leftBin >= maxSourceBin) {
				leftBin = maxSourceBin;
				frac = 0.0;
			}
			int rightBin = Math.Min(maxSourceBin, leftBin + 1);

			SpectrumPoint point = new SpectrumPoint();
			point.Magnitude = Lerp(magnitudes[leftBin], magnitudes[rightBin], frac) * block.Scale;

			double sourcePhase = Lerp(phases[leftBin], phases[rightBin], frac);
			double sourceStartPhase = phases[srcStart];
			double sourceDeltaFromStart = sourcePhase - sourceStartPhase;
			double attachedPhase = WrapPhase(block.BoundaryPhase + (block.PhaseMultiplier * sourceDeltaFromStart));
			double sourceAdvanceForPosition = (harmonicSourceAdvance != null) ? Lerp(harmonicSourceAdvance[leftBin], harmonicSourceAdvance[rightBin], frac) : 0.0;
			if (harmonicPhaseState != null && harmonicPhaseInitialized != null && currentBlockPhase != null && currentBlockPhaseStamp != null && harmonicTargetAdvance != null && harmonicSourceAdvance != null && hopOut > 0 && outWindowSize > 0) {
				if (harmonicSourcePhaseState != null && harmonicPhaseStateStride > 0) {
					point.Phase = GetOscillatorHarmonicPhase(bin, block.Index, sourcePhase, sourceAdvanceForPosition, attachedPhase, block.PhaseMultiplier, harmonicPhaseState, harmonicPhaseInitialized, harmonicSourcePhaseState, currentBlockPhase, currentBlockPhaseStamp, blockPhaseStamp, harmonicPhaseStateStride, harmonicTargetAdvance);
				} else {
					point.Phase = GetContinuousHarmonicPhase(bin, block.Index, attachedPhase, harmonicPhaseState, harmonicPhaseInitialized, currentBlockPhase, currentBlockPhaseStamp, blockPhaseStamp, harmonicPhaseStateStride, harmonicTargetAdvance);
				}
			} else {
				point.Phase = attachedPhase;
			}
			return point;
		}


		private static int GetHarmonicPhaseStateIndex(int bin, int harmonicIndex, int harmonicPhaseStateStride, int stateLength) {
			if (bin < 0 || harmonicIndex < 0 || harmonicPhaseStateStride <= 0) return -1;
			int index = (harmonicIndex * harmonicPhaseStateStride) + bin;
			if (index < 0 || index >= stateLength) return -1;
			return index;
		}

		private static double GetContinuousHarmonicPhase(int bin, int harmonicIndex, double initialPhase, double[] harmonicPhaseState, bool[] harmonicPhaseInitialized, double[] currentBlockPhase, int[] currentBlockPhaseStamp, int blockPhaseStamp, int harmonicPhaseStateStride, double[] harmonicTargetAdvance) {
			int stateIndex = GetHarmonicPhaseStateIndex(bin, harmonicIndex, harmonicPhaseStateStride, harmonicPhaseState.Length);
			if (stateIndex < 0) return initialPhase;
			if (stateIndex >= currentBlockPhase.Length || stateIndex >= currentBlockPhaseStamp.Length) return initialPhase;
			if (bin < 0 || bin >= harmonicTargetAdvance.Length) return initialPhase;

			if (currentBlockPhaseStamp[stateIndex] != blockPhaseStamp) {
				if (harmonicPhaseInitialized[stateIndex]) {
					harmonicPhaseState[stateIndex] = WrapPhase(harmonicPhaseState[stateIndex] + harmonicTargetAdvance[bin]);
				} else {
					harmonicPhaseState[stateIndex] = WrapPhase(initialPhase);
					harmonicPhaseInitialized[stateIndex] = true;
				}
				currentBlockPhase[stateIndex] = harmonicPhaseState[stateIndex];
				currentBlockPhaseStamp[stateIndex] = blockPhaseStamp;
			}

			return currentBlockPhase[stateIndex];
		}

		private static double GetOscillatorHarmonicPhase(int bin, int harmonicIndex, double sourcePhase, double sourceAdvanceForPosition, double initialPhase, double phaseMultiplier, double[] harmonicPhaseState, bool[] harmonicPhaseInitialized, double[] harmonicSourcePhaseState, double[] currentBlockPhase, int[] currentBlockPhaseStamp, int blockPhaseStamp, int harmonicPhaseStateStride, double[] harmonicTargetAdvance) {
			int stateIndex = GetHarmonicPhaseStateIndex(bin, harmonicIndex, harmonicPhaseStateStride, harmonicPhaseState.Length);
			if (stateIndex < 0) return initialPhase;
			if (stateIndex >= currentBlockPhase.Length || stateIndex >= currentBlockPhaseStamp.Length || stateIndex >= harmonicSourcePhaseState.Length) return initialPhase;
			if (bin < 0 || bin >= harmonicTargetAdvance.Length) return initialPhase;

			if (currentBlockPhaseStamp[stateIndex] != blockPhaseStamp) {
				if (harmonicPhaseInitialized[stateIndex]) {
					// r65: oscillator / phase-vocoder state is separated per harmonic.
					// Do not reset only because the interpolated source bin changes between
					// blocks; use the interpolated source phase deviation continuously instead.
					double sourceDeviation = WrapPhase(sourcePhase - harmonicSourcePhaseState[stateIndex] - sourceAdvanceForPosition);
					harmonicPhaseState[stateIndex] = WrapPhase(harmonicPhaseState[stateIndex] + harmonicTargetAdvance[bin] + (phaseMultiplier * sourceDeviation));
				} else {
					harmonicPhaseState[stateIndex] = WrapPhase(initialPhase);
					harmonicPhaseInitialized[stateIndex] = true;
				}
				harmonicSourcePhaseState[stateIndex] = sourcePhase;
				currentBlockPhase[stateIndex] = harmonicPhaseState[stateIndex];
				currentBlockPhaseStamp[stateIndex] = blockPhaseStamp;
			}

			return currentBlockPhase[stateIndex];
		}

		private static int ResolveOddEnhanceHopSize(int windowSize) {
			// Start with the configured fractional hop.  The user-visible meaning is
			// "10% hop", not "10% overlap"; higher overlap follows from the small hop.
			int hop = Math.Max(1, (int)Math.Round((double)windowSize * EnhanceHopFraction));

			// Force an odd integer hop. If the rounded hop is even, add one exactly as
			// requested.  A final safety clamp keeps the hop inside the FFT window.
			if ((hop & 1) == 0) hop++;
			if (hop >= windowSize) hop = windowSize - 1;
			if ((hop & 1) == 0) hop = Math.Max(1, hop - 1);
			return hop;
		}

		private static double[] CreatePhaseAdvanceTable(int length, int hopOut, int outWindowSize) {
			double[] table = new double[length];
			double scale = TwoPi * (double)hopOut / (double)outWindowSize;
			for (int bin = 0; bin < length; bin++) {
				table[bin] = WrapPhase(scale * (double)bin);
			}
			return table;
		}

		private static double WrapPhase(double phase) {
			phase = Math.IEEERemainder(phase, TwoPi);
			if (phase <= -Math.PI) return phase + TwoPi;
			if (phase > Math.PI) return phase - TwoPi;
			return phase;
		}

		private static SpectrumPoint BlendSpectrumPoints(SpectrumPoint a, SpectrumPoint b, double t) {
			t = Clamp(t, 0.0, 1.0);
			SpectrumPoint result = new SpectrumPoint();
			result.Magnitude = Lerp(a.Magnitude, b.Magnitude, t);
			// r36 test: keep the magnitude crossfade, but do not interpolate phase.
			// The phase switches at the midpoint; block offsets are attached at the
			// boundaries and every selected phase is wrapped into the safe range.
			result.Phase = WrapPhase((t < 0.5) ? a.Phase : b.Phase);
			return result;
		}

		private static double ShortestAngleDelta(double from, double to) {
			return WrapPhase(to - from);
		}

		private static double BoundaryBlendPosition(int bin, int low, int high, int boundaryBin) {
			if (high <= low) return bin >= boundaryBin ? 1.0 : 0.0;
			return Clamp((double)(bin - low) / (double)(high - low), 0.0, 1.0);
		}

		private static int ShiftBinDown(int bin, double factor) {
			if (bin <= 1) return 1;
			return Math.Max(1, (int)Math.Round((double)bin * factor));
		}

		private static int ShiftBinUp(int bin, double factor) {
			if (bin <= 1) return 1;
			return Math.Max(1, (int)Math.Round((double)bin * factor));
		}

		private static int FindHarmonicBoundaryIndex(int bin, List<HarmonicBlock> blocks) {
			for (int i = 0; i < blocks.Count - 1; i++) {
				HarmonicBlock next = blocks[i + 1];
				if (bin >= next.TransitionLow && bin <= next.TransitionHigh) return i;
			}
			return -1;
		}

		private static HarmonicBlock FindActiveHarmonicBlock(int bin, List<HarmonicBlock> blocks) {
			for (int i = blocks.Count - 1; i >= 0; i--) {
				if (bin >= blocks[i].RenderStart && bin <= blocks[i].RenderEnd) return blocks[i];
			}
			return default(HarmonicBlock);
		}

		private static int PositiveModulo(int value, int divisor) {
			if (divisor <= 0) return 0;
			int result = value % divisor;
			return result < 0 ? result + divisor : result;
		}

		private static void ApplyTargetNyquistFade(double[] spectrum, int enhancedRate, int targetRate) {
			if (spectrum == null || spectrum.Length < 4) return;
			if (enhancedRate <= 0) return;
			if (targetRate <= 0) targetRate = enhancedRate;

			int maxBin = (spectrum.Length / 2) - 1;
			if (maxBin < 1) {
				spectrum[1] = 0.0;
				return;
			}

			// r70: The old generic top-of-FFT fade is intentionally removed.
			// The only HF fade now follows TARGET_RATE / 2, independent of the
			// temporary enhanced FFT rate. The bin at the target Nyquist edge and
			// every bin above it are forced to zero so a later downsample stage never
			// receives intentional content above the requested output Nyquist limit.
			double targetNyquistBin = ((double)targetRate * (double)spectrum.Length) / (2.0 * (double)enhancedRate);
			if (targetNyquistBin < 1.0) targetNyquistBin = 1.0;

			double fadeStartBin = targetNyquistBin * TargetNyquistFadeDownFactor;
			if (fadeStartBin < 1.0) fadeStartBin = 1.0;
			double fadeWidth = targetNyquistBin - fadeStartBin;

			for (int bin = 1; bin <= maxBin; bin++) {
				int si = bin * 2;
				if ((double)bin >= targetNyquistBin) {
					spectrum[si] = 0.0;
					spectrum[si + 1] = 0.0;
				} else if ((double)bin > fadeStartBin && fadeWidth > 1.0e-12) {
					double x = ((double)bin - fadeStartBin) / fadeWidth;
					if (x < 0.0) x = 0.0;
					else if (x > 1.0) x = 1.0;
					double gain = 0.5 * (1.0 + Math.Cos(Math.PI * x));
					spectrum[si] *= gain;
					spectrum[si + 1] *= gain;
				}
			}

			// Packed real-FFT Nyquist bin is always the absolute upper edge.
			spectrum[1] = 0.0;
		}

		private static int BuildSpectrumMagnitudePhase(double[] spectrum, double[] magnitudes, double[] phases, bool unwrapPhases) {
			int maxBin = Math.Min((spectrum.Length / 2) - 1, Math.Min(magnitudes.Length, phases.Length) - 2);
			if (maxBin < 1) return 0;

			magnitudes[0] = Math.Abs(spectrum[0]);
			phases[0] = 0.0;
			for (int bin = 1; bin <= maxBin; bin++) {
				int si = bin * 2;
				double re = spectrum[si];
				double im = spectrum[si + 1];
				magnitudes[bin] = Math.Sqrt((re * re) + (im * im));
				phases[bin] = Math.Atan2(im, re);
			}

			for (int bin = maxBin + 1; bin < magnitudes.Length; bin++) {
				magnitudes[bin] = 0.0;
			}
			for (int bin = maxBin + 1; bin < phases.Length; bin++) {
				phases[bin] = 0.0;
			}

			if (unwrapPhases) {
				UnwrapPhases(phases, 1, maxBin);
			}

			return maxBin;
		}

		private static double AverageOriginalMagnitudeFromCenterUp(double[] spectrum, int centerBin, int sampleRate, int fftSize, double octavesUp) {
			int maxBin = (spectrum.Length / 2) - 1;
			if (centerBin < 1 || centerBin > maxBin) return 0.0;

			int highBin = (int)Math.Floor((double)centerBin * Math.Pow(2.0, octavesUp));
			highBin = Math.Max(1, Math.Min(maxBin, highBin));
			return AverageOriginalMagnitudeRange(spectrum, centerBin, highBin);
		}

		private static double AverageOriginalMagnitudeFromCenterDown(double[] spectrum, int centerBin, int sampleRate, int fftSize, double octavesDown) {
			int maxBin = (spectrum.Length / 2) - 1;
			if (centerBin < 1 || centerBin > maxBin) return 0.0;

			int lowBin = (int)Math.Ceiling((double)centerBin / Math.Pow(2.0, octavesDown));
			lowBin = Math.Max(1, Math.Min(maxBin, lowBin));
			return AverageOriginalMagnitudeRange(spectrum, lowBin, centerBin);
		}

		private static double AverageOriginalMagnitudeRange(double[] spectrum, int lowBin, int highBin) {
			int maxBin = (spectrum.Length / 2) - 1;
			lowBin = Math.Max(1, Math.Min(maxBin, lowBin));
			highBin = Math.Max(1, Math.Min(maxBin, highBin));
			if (highBin < lowBin) return 0.0;

			double sum = 0.0;
			int count = 0;
			for (int bin = lowBin; bin <= highBin; bin++) {
				int si = bin * 2;
				double re = spectrum[si];
				double im = spectrum[si + 1];
				sum += Math.Sqrt((re * re) + (im * im));
				count++;
			}

			if (count == 0) return 0.0;
			return sum / (double)count;
		}

		private static int DetectEnhanceBin(double[] magnitudes, int maxBin, int sampleRate, int fftSize, EnhanceOptions options) {
			int nyquistBin = fftSize / 2;
			maxBin = Math.Min(maxBin, magnitudes.Length - 2);
			if (maxBin <= 4) return nyquistBin;

			// Search only the upper half of the usable input FFT bins, but normalize the
			// noise-floor threshold against the maximum magnitude of the whole usable
			// FFT. AUTO_NORMALIZED_NOISE_FLOOR_DB therefore means "dB below the global
			// FFT peak", not dB below the peak inside the high-frequency search range.
			int searchLowBin = Math.Max(1, maxBin / 2);

			double globalPeak = 0.0;
			for (int bin = 1; bin <= maxBin; bin++) {
				double mag = magnitudes[bin];
				if (mag > globalPeak) globalPeak = mag;
			}
			if (globalPeak <= 0.0) return nyquistBin;

			double threshold = globalPeak * Math.Pow(10.0, options.ThresholdDb / 20.0);

			// Walk down from the top until the spectrum first rises above the normalized
			// threshold. This is the provisional upper edge. Then continue downward and
			// keep the strongest local peak. The peak search stops once the current bin
			// is more than FollowDownDropDb below the best peak or more than
			// FollowDownMaxOctaves below the current best peak.
			int provisionalBin = -1;
			for (int bin = maxBin; bin >= searchLowBin; bin--) {
				if (magnitudes[bin] >= threshold) {
					provisionalBin = bin;
					break;
				}
			}
			if (provisionalBin < searchLowBin) return nyquistBin;

			return FindPeakAboveThresholdEdge(magnitudes, provisionalBin, maxBin, sampleRate, fftSize, options);
		}

		private static int FindPeakAboveThresholdEdge(double[] magnitudes, int startBin, int maxBin, int sampleRate, int fftSize, EnhanceOptions options) {
			int peakBin = startBin;
			double peakMag = Math.Max(magnitudes[startBin], 1.0e-300);
			double dropFactor = Math.Pow(10.0, -options.FollowDownDropDb / 20.0);
			double minBinFactor = Math.Pow(2.0, -Math.Max(0.0, options.FollowDownMaxOctaves));
			int minAllowedBin = Math.Max(1, (int)Math.Floor((double)peakBin * minBinFactor));

			for (int bin = startBin - 1; bin >= 1; bin--) {
				if (bin < minAllowedBin) break;

				double mag = magnitudes[bin];
				if (mag > peakMag) {
					peakMag = mag;
					peakBin = bin;
					minAllowedBin = Math.Max(1, (int)Math.Floor((double)peakBin * minBinFactor));
					continue;
				}

				if (mag < peakMag * dropFactor) break;
			}

			return peakBin;
		}

		private static int LowerEnhanceBinByOctaves(int enhanceBin, int maxSearchBin, double divisor) {
			// Only shift a genuinely detected input bin. The fallback value is nyquistBin
			// and is larger than maxSearchBin, so it must stay untouched; otherwise a
			// frame with no valid high-frequency edge would incorrectly generate harmonics.
			if (enhanceBin <= 1 || enhanceBin > maxSearchBin || divisor <= 1.0) return enhanceBin;

			int shiftedBin = (int)Math.Floor((double)enhanceBin / divisor);
			return Math.Max(1, Math.Min(maxSearchBin, shiftedBin));
		}

		private static bool RegionConfirmed(int lowBin, int highBin, int sampleRate, int fftSize, double minOctaves) {
			double lowFrequency = (double)lowBin * (double)sampleRate / (double)fftSize;
			double highFrequency = (double)highBin * (double)sampleRate / (double)fftSize;
			if (lowFrequency <= 0.0 || highFrequency <= lowFrequency) return false;
			double octaves = Math.Log(highFrequency / lowFrequency) / Math.Log(2.0);
			return octaves >= minOctaves;
		}

		private static int FollowDown(double[] magnitudes, int startBin, int sampleRate, int fftSize, EnhanceOptions options, int minAllowedBin, int fallbackBin) {
			double startFrequency = (double)startBin * (double)sampleRate / (double)fftSize;
			double minFrequency = startFrequency / Math.Pow(2.0, options.FollowDownMaxOctaves);
			int currentBin = startBin;
			int peakBin = startBin;
			double peakMag = magnitudes[startBin];
			double dropFactor = Math.Pow(10.0, -options.FollowDownDropDb / 20.0);

			while (true) {
				int nextBin = currentBin - 1;
				if (nextBin < minAllowedBin) break;
				double nextFrequency = (double)nextBin * (double)sampleRate / (double)fftSize;
				if (nextFrequency < minFrequency) return startBin;

				double nextMag = magnitudes[nextBin];
				if (nextMag <= peakMag * dropFactor) break;

				currentBin = nextBin;
				if (nextMag > peakMag) {
					peakMag = nextMag;
					peakBin = nextBin;
				}
			}

			return (peakBin >= minAllowedBin) ? peakBin : fallbackBin;
		}

		private static void UnwrapPhases(double[] phases, int start, int end) {
			for (int bin = start + 1; bin <= end; bin++) {
				double prev = phases[bin - 1];
				double cur = phases[bin];
				while ((cur - prev) > Math.PI) cur -= TwoPi;
				while ((cur - prev) < -Math.PI) cur += TwoPi;
				phases[bin] = cur;
			}
		}

		private static double Lerp(double a, double b, double t) {
			return a + ((b - a) * t);
		}

		private static double Clamp(double value, double min, double max) {
			if (value < min) return min;
			if (value > max) return max;
			return value;
		}

		private static double[] CreateRaisedCosineWindow(int n, double power) {
			return WindowMath.BuildPowerSineWindow(n, power);
		}
	}
}
