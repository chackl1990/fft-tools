using System;

namespace FFTTools {
    public static class FFTToolsUpmix {
        public const double CenterPhaseFullDelayMs = 5.0;
        public const double CenterPhaseZeroDelayMs = 10.0;

        public static double[] ExtractCenter(double[,] input, int inputFrames, int sampleRate, int windowSize, int overlapCount, double panSharpness, double centerPosition) {
            if (input == null) throw new ArgumentNullException("input");
            if (inputFrames < 0 || inputFrames > input.GetLength(0)) throw new Exception("Input frame count is invalid.");
            if (input.GetLength(1) != 2) throw new Exception("Center extraction input must be stereo.");
            if (sampleRate <= 0) throw new Exception("Sample rate is invalid.");
            if (windowSize < 1024 || (windowSize & (windowSize - 1)) != 0) throw new Exception("Center extraction FFT window size must be a power of two.");
            if (overlapCount < 2 || (windowSize % overlapCount) != 0) throw new Exception("Center extraction overlap count is invalid.");

            int hop = windowSize / overlapCount;
            int binCount = windowSize / 2;
            double binHz = (double)sampleRate / (double)windowSize;
            int firstStart = -windowSize + hop;
            int lastStart = inputFrames - 1;

            double[] centerOut = new double[inputFrames];
            double[] norm = new double[inputFrames];
            double[] inWindow = CreateRaisedCosineWindow(windowSize, 1.0);
            double[] outWindow = CreateRaisedCosineWindow(windowSize, 2.0);
            double[] outNormWindow = CreateRaisedCosineWindow(windowSize, 1.0);
            double[] normWindow = new double[windowSize];
            double inverseFftScale = (double)windowSize / 2.0;
            for (int i = 0; i < windowSize; i++) {
                normWindow[i] = inverseFftScale * outNormWindow[i] * outWindow[i];
            }

            RealFFT fft = new RealFFT(windowSize);
            double[] spectrumL = new double[windowSize];
            double[] spectrumR = new double[windowSize];
            double[] spectrumC = new double[windowSize];
            double[] rawMask = new double[binCount + 1];
            double[] smoothMask = new double[binCount + 1];

            panSharpness = Math.Max(0.1, panSharpness);
            centerPosition = Clamp(centerPosition, 0.05, 0.95);

            for (int blockStart = firstStart; blockStart <= lastStart; blockStart += hop) {
                Array.Clear(spectrumL, 0, spectrumL.Length);
                Array.Clear(spectrumR, 0, spectrumR.Length);
                Array.Clear(spectrumC, 0, spectrumC.Length);
                Array.Clear(rawMask, 0, rawMask.Length);
                Array.Clear(smoothMask, 0, smoothMask.Length);

                int inputCopyStart = Math.Max(0, -blockStart);
                int inputCopyEnd = Math.Min(windowSize, inputFrames - blockStart);
                for (int i = inputCopyStart; i < inputCopyEnd; i++) {
                    int sourceIndex = blockStart + i;
                    double window = inWindow[i];
                    spectrumL[i] = input[sourceIndex, 0] * window;
                    spectrumR[i] = input[sourceIndex, 1] * window;
                }

                fft.ComputeForward(spectrumL);
                fft.ComputeForward(spectrumR);

                for (int bin = 1; bin < binCount; bin++) {
                    int si = bin * 2;
                    double lR = spectrumL[si];
                    double lI = spectrumL[si + 1];
                    double rR = spectrumR[si];
                    double rI = spectrumR[si + 1];
                    double lMag = Math.Sqrt((lR * lR) + (lI * lI));
                    double rMag = Math.Sqrt((rR * rR) + (rI * rI));
                    double panPos = Clamp((rMag - lMag) / (rMag + lMag + 1.0e-20), -1.0, 1.0);
                    double levelGate = CenterLevelGateFromPan(panPos, centerPosition);
                    if (panSharpness != 1.0) levelGate = Math.Pow(levelGate, panSharpness);
                    double phaseGate = CenterPhaseGate(CosPhase(lR, lI, rR, rI), (double)bin * binHz);
                    rawMask[bin] = Clamp(levelGate * phaseGate, 0.0, 1.0);
                }

                SmoothCenterMaskByNeighbour(rawMask, smoothMask, binCount);

                for (int bin = 1; bin < binCount; bin++) {
                    int si = bin * 2;
                    double mask = smoothMask[bin];
                    spectrumC[si] = (spectrumL[si] + spectrumR[si]) * 0.5 * mask;
                    spectrumC[si + 1] = (spectrumL[si + 1] + spectrumR[si + 1]) * 0.5 * mask;
                }

                fft.ComputeReverse(spectrumC);

                int outputAddStart = Math.Max(0, -blockStart);
                int outputAddEnd = Math.Min(windowSize, inputFrames - blockStart);
                for (int i = outputAddStart; i < outputAddEnd; i++) {
                    int outputIndex = blockStart + i;
                    centerOut[outputIndex] += spectrumC[i] * outWindow[i];
                    norm[outputIndex] += normWindow[i];
                }
            }

            for (int i = 0; i < inputFrames; i++) {
                centerOut[i] = (norm[i] > 1.0e-30) ? (centerOut[i] / norm[i]) : 0.0;
            }

            return centerOut;
        }

        public static double CenterPhaseGate(double cosPhase, double frequencyHz) {
            cosPhase = Clamp(cosPhase, -1.0, 1.0);
            double angleDegrees = Math.Acos(cosPhase) * (180.0 / Math.PI);
            double fullDegrees = CenterPhaseDelayMsToDegrees(frequencyHz, CenterPhaseFullDelayMs);
            double zeroDegrees = CenterPhaseDelayMsToDegrees(frequencyHz, CenterPhaseZeroDelayMs);

            zeroDegrees = Math.Max(0.0, zeroDegrees);
            fullDegrees = Clamp(fullDegrees, 0.0, zeroDegrees);

            if (angleDegrees <= fullDegrees) return 1.0;
            if (angleDegrees >= zeroDegrees) return 0.0;
            double span = zeroDegrees - fullDegrees;
            if (span <= 1.0e-12) return 0.0;
            double t = (angleDegrees - fullDegrees) / span;
            t = SmoothStep(t);
            return 1.0 - t;
        }

        private static double CenterPhaseDelayMsToDegrees(double frequencyHz, double delayMs) {
            if (frequencyHz <= 0.0 || delayMs <= 0.0) return 0.0;
            return frequencyHz * delayMs * 0.36;
        }

        public static double CosPhase(double lR, double lI, double rR, double rI) {
            double lMagSq = (lR * lR) + (lI * lI);
            double rMagSq = (rR * rR) + (rI * rI);
            if (lMagSq <= 1.0e-30 || rMagSq <= 1.0e-30) return 1.0;
            return Clamp(((lR * rR) + (lI * rI)) / Math.Sqrt(lMagSq * rMagSq), -1.0, 1.0);
        }

        public static double CenterLevelGateFromPan(double panPos, double centerPosition) {
            panPos = Math.Abs(Clamp(panPos, -1.0, 1.0));
            centerPosition = Clamp(centerPosition, 0.05, 0.95);
            if (panPos >= centerPosition) return 0.0;
            return 1.0 - (panPos / centerPosition);
        }

        public static void SmoothCenterMaskByNeighbour(double[] rawGate, double[] smoothGate, int binCount) {
            for (int bin = 1; bin < binCount; bin++) {
                double raw = Clamp(rawGate[bin], 0.0, 1.0);
                double left = Clamp(rawGate[bin - 1], 0.0, 1.0);
                double right = (bin + 1 < binCount) ? Clamp(rawGate[bin + 1], 0.0, 1.0) : raw;
                double smoothed = (left * 0.25) + (raw * 0.50) + (right * 0.25);
                smoothGate[bin] = (raw <= 0.0) ? 0.0 : Clamp(smoothed, 0.0, 1.0);
            }
        }

        private static double[] CreateRaisedCosineWindow(int n, double power) {
            return WindowMath.BuildPowerSineWindow(n, power);
        }

        private static double SmoothStep(double t) {
            t = Clamp(t, 0.0, 1.0);
            return t * t * (3.0 - (2.0 * t));
        }

        private static double Clamp(double value, double min, double max) {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static double DbToLinearGain(double db) {
            return Math.Pow(10.0, db / 20.0);
        }

        public static double[,] RenderDereverbUpmixChunk(double[,] dereverb, int frames, int sampleRate, double ampFactor, LCRWideOptions options, Action<double> progress) {
            if (dereverb == null) throw new ArgumentNullException("dereverb");
            if (frames < 0 || frames > dereverb.GetLength(0)) throw new Exception("Upmix frame count is invalid.");
            if (dereverb.GetLength(1) < 6) throw new Exception("Upmix dereverb input must have six channels.");

            LCRWideOptions effectiveOptions = options ?? new LCRWideOptions();
            const double fixedNearFrontKeep = 0.50;
            int centerWindowSize = 8192;
            int centerOverlap = 16;

            double[,] dryFrontInput = new double[frames, 2];
            for (int i = 0; i < frames; i++) {
                dryFrontInput[i, 0] = dereverb[i, 0] + (dereverb[i, 2] * fixedNearFrontKeep);
                dryFrontInput[i, 1] = dereverb[i, 1] + (dereverb[i, 3] * fixedNearFrontKeep);
            }

            double[] center = ExtractCenter(
                dryFrontInput,
                frames,
                sampleRate,
                centerWindowSize,
                centerOverlap,
                effectiveOptions.LCR7PanSharpness,
                effectiveOptions.LCR7CLCRPosition);

            double centerGain = Clamp(effectiveOptions.CenterGain, 0.0, 2.0);
            double nonCenterOutputGain = DbToLinearGain(effectiveOptions.NonCenterOutputGainDb);
            double centerOutputGain = DbToLinearGain(effectiveOptions.CenterOutputGainDb);
            double[,] output = new double[frames, 8];
            for (int i = 0; i < frames; i++) {
                double centerForResidual = center[i] * centerGain;
                double c = centerForResidual * ampFactor;
                double frontL = (dryFrontInput[i, 0] - centerForResidual) * ampFactor;
                double frontR = (dryFrontInput[i, 1] - centerForResidual) * ampFactor;
                double nearL = dereverb[i, 2] * ampFactor;
                double nearR = dereverb[i, 3] * ampFactor;
                double farL = dereverb[i, 4] * ampFactor;
                double farR = dereverb[i, 5] * ampFactor;

                output[i, 0] = frontL * nonCenterOutputGain;
                output[i, 1] = frontR * nonCenterOutputGain;
                output[i, 2] = c * centerOutputGain;
                output[i, 3] = 0.0;
                output[i, 4] = nearL * nonCenterOutputGain;
                output[i, 5] = nearR * nonCenterOutputGain;
                output[i, 6] = farL * nonCenterOutputGain;
                output[i, 7] = farR * nonCenterOutputGain;

                if (progress != null && ((i & 8191) == 0 || i + 1 == frames)) {
                    progress((double)(i + 1) / (double)Math.Max(1, frames));
                }
            }

            return output;
        }

    }
}
