using System;

namespace FFTTools {
    public static class FFTToolsUpmix {
        public const double CenterPhaseFullDelayMs = 1.0;
        public const double CenterPhaseZeroDelayMs = 2.0;

        // Dry residual front/side split tuning. This split is intentionally enabled in normal --upmix.
        // sidepan defines where the smooth pan handoff starts. 0.8 means
        // only the outermost 20% of the pan range can move into Side.
        // front_side_mix defines the Front share at full side pan:
        // 0.0 = all Side, 1.0 = all Front, 0.5 = half Front / half Side.
        private const double sidepan = 0.8;
        private const double front_side_mix = 0.5;


        public static double[] ExtractCenter(double[,] input, int inputFrames, int sampleRate, int windowSize, int overlapCount, double panSharpness, double centerPosition) {
            if (input == null) throw new ArgumentNullException("input");
            if (inputFrames < 0 || inputFrames > input.GetLength(0)) throw new Exception("Input frame count is invalid.");
            if (input.GetLength(1) != 2) throw new Exception("Center extraction input must be stereo.");
            if (sampleRate <= 0) throw new Exception("Sample rate is invalid.");
            if (windowSize < 1024 || (windowSize & (windowSize - 1)) != 0) throw new Exception("Center extraction FFT window size must be a power of two.");
            if (overlapCount < 2 || (windowSize % overlapCount) != 0) throw new Exception("Center extraction overlap count is invalid.");

            int hop = windowSize / overlapCount;
            int binCount = windowSize / 2;
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

            panSharpness = Math.Max(0.1, panSharpness);
            centerPosition = Clamp(centerPosition, 0.05, 0.95);

            for (int blockStart = firstStart; blockStart <= lastStart; blockStart += hop) {
                Array.Clear(spectrumL, 0, spectrumL.Length);
                Array.Clear(spectrumR, 0, spectrumR.Length);
                Array.Clear(spectrumC, 0, spectrumC.Length);

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

                // CenterExtract common signal estimate. For each complex FFT bin,
                // the common Center component is derived from sum/difference energy:
                //   sum  = L + R
                //   diff = L - R
                //   alpha = 0.5 - 0.5 * sqrt(|diff|^2 / |sum|^2)
                //   C = sum * alpha
                // No additional pan gate, phase-delay gate, signed-amplitude fold,
                // or fragment prune is applied in this Center stage.
                for (int bin = 1; bin < binCount; bin++) {
                    int si = bin * 2;
                    double cR;
                    double cI;
                    BuildCenterExtractBin(
                        spectrumL[si],
                        spectrumL[si + 1],
                        spectrumR[si],
                        spectrumR[si + 1],
                        1.0,
                        out cR,
                        out cI);
                    spectrumC[si] = cR;
                    spectrumC[si + 1] = cI;
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

        public static void SplitDryResidualFrontSide(double[,] residual, int frames, int sampleRate, int windowSize, int overlapCount, out double[,] front, out double[,] side) {
            if (residual == null) throw new ArgumentNullException("residual");
            if (frames < 0 || frames > residual.GetLength(0)) throw new Exception("Dry residual frame count is invalid.");
            if (residual.GetLength(1) != 2) throw new Exception("Dry residual split input must be stereo.");
            if (sampleRate <= 0) throw new Exception("Sample rate is invalid.");
            if (windowSize < 1024 || (windowSize & (windowSize - 1)) != 0) throw new Exception("Dry residual split FFT window size must be a power of two.");
            if (overlapCount < 2 || (windowSize % overlapCount) != 0) throw new Exception("Dry residual split overlap count is invalid.");

            front = new double[frames, 2];
            side = new double[frames, 2];
            if (frames <= 0) return;

            int hop = windowSize / overlapCount;
            int binCount = windowSize / 2;
            int firstStart = -windowSize + hop;
            int lastStart = frames - 1;

            double[] norm = new double[frames];
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
            double[] frontL = new double[windowSize];
            double[] frontR = new double[windowSize];
            double[] sideL = new double[windowSize];
            double[] sideR = new double[windowSize];

            for (int blockStart = firstStart; blockStart <= lastStart; blockStart += hop) {
                Array.Clear(spectrumL, 0, spectrumL.Length);
                Array.Clear(spectrumR, 0, spectrumR.Length);
                Array.Clear(frontL, 0, frontL.Length);
                Array.Clear(frontR, 0, frontR.Length);
                Array.Clear(sideL, 0, sideL.Length);
                Array.Clear(sideR, 0, sideR.Length);

                int inputCopyStart = Math.Max(0, -blockStart);
                int inputCopyEnd = Math.Min(windowSize, frames - blockStart);
                for (int i = inputCopyStart; i < inputCopyEnd; i++) {
                    int sourceIndex = blockStart + i;
                    double window = inWindow[i];
                    spectrumL[i] = residual[sourceIndex, 0] * window;
                    spectrumR[i] = residual[sourceIndex, 1] * window;
                }

                fft.ComputeForward(spectrumL);
                fft.ComputeForward(spectrumR);

                // Preserve DC / Nyquist in Front. The side pan split is meant for
                // normal spectral bins and must remain reconstructable.
                frontL[0] = spectrumL[0];
                frontL[1] = spectrumL[1];
                frontR[0] = spectrumR[0];
                frontR[1] = spectrumR[1];

                for (int bin = 1; bin < binCount; bin++) {
                    int si = bin * 2;
                    double lR = spectrumL[si];
                    double lI = spectrumL[si + 1];
                    double rR = spectrumR[si];
                    double rI = spectrumR[si + 1];
                    double lMag = Math.Sqrt((lR * lR) + (lI * lI));
                    double rMag = Math.Sqrt((rR * rR) + (rI * rI));
                    double pan = (rMag - lMag) / (lMag + rMag + 1.0e-20);
                    double frontShare;
                    double sideShare;
                    ComputeFrontSidePanSplit(pan, out frontShare, out sideShare);

                    frontL[si] = lR * frontShare;
                    frontL[si + 1] = lI * frontShare;
                    frontR[si] = rR * frontShare;
                    frontR[si + 1] = rI * frontShare;
                    sideL[si] = lR * sideShare;
                    sideL[si + 1] = lI * sideShare;
                    sideR[si] = rR * sideShare;
                    sideR[si + 1] = rI * sideShare;
                }

                fft.ComputeReverse(frontL);
                fft.ComputeReverse(frontR);
                fft.ComputeReverse(sideL);
                fft.ComputeReverse(sideR);

                int outputAddStart = Math.Max(0, -blockStart);
                int outputAddEnd = Math.Min(windowSize, frames - blockStart);
                for (int i = outputAddStart; i < outputAddEnd; i++) {
                    int outputIndex = blockStart + i;
                    double w = outWindow[i];
                    front[outputIndex, 0] += frontL[i] * w;
                    front[outputIndex, 1] += frontR[i] * w;
                    side[outputIndex, 0] += sideL[i] * w;
                    side[outputIndex, 1] += sideR[i] * w;
                    norm[outputIndex] += normWindow[i];
                }
            }

            for (int i = 0; i < frames; i++) {
                double scale = (norm[i] > 1.0e-30) ? (1.0 / norm[i]) : 0.0;
                front[i, 0] *= scale;
                front[i, 1] *= scale;
                side[i, 0] *= scale;
                side[i, 1] *= scale;
            }
        }

        public static void ComputeFrontSidePanSplit(double pan, out double frontShare, out double sideShare) {
            double start = Clamp(sidepan, 0.0, 1.0);
            double fullFront = Clamp(front_side_mix, 0.0, 1.0);
            double absPan = Math.Abs(Clamp(pan, -1.0, 1.0));
            double t = 0.0;
            if (start < 1.0 && absPan > start) {
                t = SmoothStep((absPan - start) / (1.0 - start));
            }

            sideShare = (1.0 - fullFront) * t;
            frontShare = 1.0 - sideShare;
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
            return 1.0 - SmoothStep(t);
        }

        private static double CenterPhaseDelayMsToDegrees(double frequencyHz, double delayMs) {
            if (frequencyHz <= 0.0 || delayMs <= 0.0) return 0.0;
            return frequencyHz * delayMs * 0.36;
        }

        // CenterExtract common spectral component. The formula operates directly
        // on complex L/R bins and estimates the shared part from the ratio of
        // side energy to sum energy. When L and R are equal, C becomes that
        // shared bin. When L and R are opposite, C tends to zero.
        public static void BuildCenterExtractBin(double lR, double lI, double rR, double rI, double gain, out double cR, out double cI) {
            cR = 0.0;
            cI = 0.0;
            if (gain <= 0.0) return;

            double sumR = lR + rR;
            double sumI = lI + rI;
            double diffR = lR - rR;
            double diffI = lI - rI;
            double sumSq = (sumR * sumR) + (sumI * sumI);
            if (sumSq <= 1.0e-30) return;

            double diffSq = (diffR * diffR) + (diffI * diffI);
            double alpha = 0.5 - (0.5 * Math.Sqrt(diffSq / sumSq));
            if (Double.IsNaN(alpha) || Double.IsInfinity(alpha)) return;

            double scale = alpha * gain;
            cR = sumR * scale;
            cI = sumI * scale;
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
            int centerWindowSize = 8192;
            int centerOverlap = 16;

            double[,] dryFrontInput = new double[frames, 2];
            for (int i = 0; i < frames; i++) {
                dryFrontInput[i, 0] = dereverb[i, 0];
                dryFrontInput[i, 1] = dereverb[i, 1];
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

            double[,] dryResidual = new double[frames, 2];
            for (int i = 0; i < frames; i++) {
                double centerForResidual = center[i] * centerGain;
                dryResidual[i, 0] = dryFrontInput[i, 0] - centerForResidual;
                dryResidual[i, 1] = dryFrontInput[i, 1] - centerForResidual;
            }

            double[,] dryFrontResidual;
            double[,] drySideResidual;
            SplitDryResidualFrontSide(
                dryResidual,
                frames,
                sampleRate,
                centerWindowSize,
                centerOverlap,
                out dryFrontResidual,
                out drySideResidual);

            double nonCenterOutputGain = DbToLinearGain(effectiveOptions.NonCenterOutputGainDb);
            double centerOutputGain = DbToLinearGain(effectiveOptions.CenterOutputGainDb);
            double[,] output = new double[frames, 8];
            for (int i = 0; i < frames; i++) {
                double centerForResidual = center[i] * centerGain;
                double c = centerForResidual * ampFactor;

                // Front remains reconstructable together with Center and the
                // optional dry Side split:
                //   FL + SL(dry split) + C = dry L
                //   FR + SR(dry split) + C = dry R
                // when amp/output gains are unity and CenterGain is 100%.
                double frontL = dryFrontResidual[i, 0] * ampFactor;
                double frontR = dryFrontResidual[i, 1] * ampFactor;

                // Reverb components remain separated: Side = near, Rear = far.
                // Clear far-panned dry residual can additionally be split from
                // Front to Side while preserving the technical mixdown sum.
                double nearL = (dereverb[i, 2] + drySideResidual[i, 0]) * ampFactor;
                double nearR = (dereverb[i, 3] + drySideResidual[i, 1]) * ampFactor;
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
