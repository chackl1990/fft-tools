using System;

namespace FFTTools {
    public class DereverbOptions {
        public double LowCutHz = 100.0;
        public double EarlyStartMs = 100.0;
        public double EarlyFullMs = 200.0;
        public double LateStartMs = 300.0;
        public double MaxTailMs = 500.0;
        public double Strength = 1.0;
        public double TonalProtect = 0.00;
    }

    public static class FFTToolsDereverb {
        public static double[,] Process(double[,] input, int inputFrames, int sampleRate, int windowSize, int overlapCount, DereverbOptions options, Action<double> progress) {
            if (input == null) throw new ArgumentNullException("input");
            if (inputFrames < 0 || inputFrames > input.GetLength(0)) throw new Exception("Input frame count is invalid.");
            if (input.GetLength(1) != 2) throw new Exception("Dereverb input must be stereo.");
            if (sampleRate <= 0) throw new Exception("Sample rate is invalid.");
            if (windowSize < 1024 || (windowSize & (windowSize - 1)) != 0) throw new Exception("Dereverb FFT window size must be a power of two.");
            if (overlapCount < 2 || (windowSize % overlapCount) != 0) throw new Exception("Dereverb overlap count is invalid.");
            if (options == null) options = new DereverbOptions();

            double lowCutHz = Math.Max(0.0, options.LowCutHz);
            double earlyStartMs = Math.Max(0.0, options.EarlyStartMs);
            double earlyFullMs = Math.Max(earlyStartMs + 1.0, options.EarlyFullMs);
            double lateStartMs = Math.Max(earlyFullMs + 1.0, options.LateStartMs);
            double maxTailMs = Math.Max(lateStartMs + 1.0, options.MaxTailMs);
            double strength = Math.Max(0.0, options.Strength);
            double tonalProtectAmount = Clamp(options.TonalProtect, 0.0, 1.0);

            double[,] output = new double[inputFrames, 6];
            double[] earlyL, lateL, earlyR, lateR;

            ProcessChannel(input, inputFrames, 0, sampleRate, windowSize, overlapCount, lowCutHz, earlyStartMs, earlyFullMs, lateStartMs, maxTailMs, strength, tonalProtectAmount, out earlyL, out lateL, delegate(double p) {
                if (progress != null) progress(p * 0.5);
            });
            ProcessChannel(input, inputFrames, 1, sampleRate, windowSize, overlapCount, lowCutHz, earlyStartMs, earlyFullMs, lateStartMs, maxTailMs, strength, tonalProtectAmount, out earlyR, out lateR, delegate(double p) {
                if (progress != null) progress(0.5 + (p * 0.5));
            });

            for (int i = 0; i < inputFrames; i++) {
                output[i, 0] = input[i, 0] - earlyL[i] - lateL[i];
                output[i, 1] = input[i, 1] - earlyR[i] - lateR[i];
                output[i, 2] = earlyL[i];
                output[i, 3] = earlyR[i];
                output[i, 4] = lateL[i];
                output[i, 5] = lateR[i];
            }

            return output;
        }

        private static void ProcessChannel(double[,] input, int inputFrames, int channel, int sampleRate, int windowSize, int overlapCount, double lowCutHz, double earlyStartMs, double earlyFullMs, double lateStartMs, double maxTailMs, double strength, double tonalProtectAmount, out double[] earlyOut, out double[] lateOut, Action<double> progress) {
            int hop = windowSize / overlapCount;
            int binCount = windowSize / 2;
            double hopMs = (double)hop * 1000.0 / (double)sampleRate;
            double binHz = (double)sampleRate / (double)windowSize;
            int firstStart = -windowSize + hop;
            int lastStart = inputFrames - 1;
            int totalFrames = ((lastStart - firstStart) / hop) + 1;
            int processedFrames = 0;

            earlyOut = new double[inputFrames];
            lateOut = new double[inputFrames];
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
            double[] spectrum = new double[windowSize];
            double[] earlySpectrum = new double[windowSize];
            double[] lateSpectrum = new double[windowSize];
            double[] magnitudes = new double[binCount + 1];
            double[] prevMagnitudes = new double[binCount + 1];
            double[] ageMs = new double[binCount + 1];
            double[] stableHold = new double[binCount + 1];
            double[] prevEarlyMask = new double[binCount + 1];
            double[] prevLateMask = new double[binCount + 1];
            double[] frameEarlyMask = new double[binCount + 1];
            double[] frameLateMask = new double[binCount + 1];
            double[] smoothEarlyMask = new double[binCount + 1];
            double[] smoothLateMask = new double[binCount + 1];
            bool[] forceDryFrame = new bool[binCount + 1];
            for (int bin = 0; bin <= binCount; bin++) {
                ageMs[bin] = 0.0;
            }

            double reAttackFactor = Math.Pow(10.0, 4.5 / 20.0);
            double slightRiseFactor = Math.Pow(10.0, 1.5 / 20.0);
            double stableDecay = Math.Exp(-hopMs / 450.0);
            double maskRiseCoeff = 1.0 - Math.Exp(-hopMs / 80.0);
            double maskFallCoeff = 0.65;

            for (int blockStart = firstStart; blockStart <= lastStart; blockStart += hop) {
                Array.Clear(spectrum, 0, spectrum.Length);
                int inputCopyStart = Math.Max(0, -blockStart);
                int inputCopyEnd = Math.Min(windowSize, inputFrames - blockStart);
                for (int i = inputCopyStart; i < inputCopyEnd; i++) {
                    spectrum[i] = input[blockStart + i, channel] * inWindow[i];
                }

                fft.ComputeForward(spectrum);
                BuildMagnitudes(spectrum, magnitudes);
                double framePeak = 0.0;
                for (int bin = 1; bin < binCount; bin++) {
                    if (magnitudes[bin] > framePeak) framePeak = magnitudes[bin];
                }
                double activeThreshold = Math.Max(1.0e-14, framePeak * 1.0e-6);

                Array.Clear(earlySpectrum, 0, earlySpectrum.Length);
                Array.Clear(lateSpectrum, 0, lateSpectrum.Length);
                Array.Clear(frameEarlyMask, 0, frameEarlyMask.Length);
                Array.Clear(frameLateMask, 0, frameLateMask.Length);
                Array.Clear(smoothEarlyMask, 0, smoothEarlyMask.Length);
                Array.Clear(smoothLateMask, 0, smoothLateMask.Length);
                Array.Clear(forceDryFrame, 0, forceDryFrame.Length);

                for (int bin = 1; bin < binCount; bin++) {
                    double freqHz = (double)bin * binHz;
                    double mag = magnitudes[bin];
                    double prevMag = prevMagnitudes[bin];
                    double rawEarlyMask = 0.0;
                    double rawLateMask = 0.0;
                    bool forceDryThisBin = false;

                    double frequencyWeight = LowCutCurve(freqHz, lowCutHz);

                    if (frequencyWeight > 0.0 && mag > activeThreshold) {
                        bool newPeak = (prevMag <= activeThreshold) || (mag > prevMag * reAttackFactor);
                        if (newPeak) {
                            // A new clear peak is treated as direct sound, not echo/reverb.
                            ageMs[bin] = 0.0;
                            forceDryThisBin = true;
                        }
                        else {
                            ageMs[bin] += hopMs;
                        }

                        double localPeak = LocalPeakScore(magnitudes, bin, binCount);
                        double changeDb = 0.0;
                        if (prevMag > activeThreshold && mag > activeThreshold) {
                            changeDb = Math.Abs(20.0 * Math.Log10(mag / prevMag));
                        }
                        double temporalStability = 1.0 - SmoothStep(1.5, 6.0, changeDb);
                        double stableScore = localPeak * temporalStability;
                        stableHold[bin] = Math.Max(stableHold[bin] * stableDecay, stableScore);

                        if (!newPeak) {
                            // Only decaying or almost-flat tails are reverb candidates.
                            // Strong re-growth is intentionally left dry to avoid echo extraction.
                            double decayScore = 1.0 - SmoothStep(slightRiseFactor, reAttackFactor, (prevMag > activeThreshold) ? (mag / prevMag) : 1.0);
                            double timeAmount = SmoothStep(earlyStartMs, earlyFullMs, ageMs[bin]);
                            double farAmount = SmoothStep(lateStartMs, maxTailMs, ageMs[bin]);
                            double stableProtect = Clamp(stableHold[bin] * tonalProtectAmount, 0.0, 0.98);
                            double hallMask = strength * frequencyWeight * timeAmount * decayScore * (1.0 - stableProtect);
                            hallMask = ApplyDirectFloor(hallMask, stableHold[bin]);

                            rawEarlyMask = hallMask * (1.0 - farAmount);
                            rawLateMask = hallMask * farAmount;
                        }
                    }
                    else {
                        ageMs[bin] += hopMs;
                        stableHold[bin] *= stableDecay;
                    }

                    double earlyMask;
                    double lateMask;
                    if (forceDryThisBin) {
                        earlyMask = 0.0;
                        lateMask = 0.0;
                        prevEarlyMask[bin] = 0.0;
                        prevLateMask[bin] = 0.0;
                        forceDryFrame[bin] = true;
                    }
                    else {
                        earlyMask = SmoothMask(prevEarlyMask[bin], rawEarlyMask, maskRiseCoeff, maskFallCoeff);
                        lateMask = SmoothMask(prevLateMask[bin], rawLateMask, maskRiseCoeff, maskFallCoeff);
                        prevEarlyMask[bin] = earlyMask;
                        prevLateMask[bin] = lateMask;
                    }

                    ApplyDirectFloor(ref earlyMask, ref lateMask, stableHold[bin]);
                    frameEarlyMask[bin] = earlyMask;
                    frameLateMask[bin] = lateMask;
                }

                SmoothReverbMasks(frameEarlyMask, frameLateMask, smoothEarlyMask, smoothLateMask, stableHold, forceDryFrame, binCount);

                for (int bin = 1; bin < binCount; bin++) {
                    double earlyMask = smoothEarlyMask[bin];
                    double lateMask = smoothLateMask[bin];
                    ApplyDirectFloor(ref earlyMask, ref lateMask, stableHold[bin]);

                    int si = bin * 2;
                    earlySpectrum[si] = spectrum[si] * earlyMask;
                    earlySpectrum[si + 1] = spectrum[si + 1] * earlyMask;
                    lateSpectrum[si] = spectrum[si] * lateMask;
                    lateSpectrum[si + 1] = spectrum[si + 1] * lateMask;
                }

                Array.Copy(magnitudes, prevMagnitudes, magnitudes.Length);

                fft.ComputeReverse(earlySpectrum);
                fft.ComputeReverse(lateSpectrum);

                int outputAddStart = Math.Max(0, -blockStart);
                int outputAddEnd = Math.Min(windowSize, inputFrames - blockStart);
                for (int i = outputAddStart; i < outputAddEnd; i++) {
                    int outputIndex = blockStart + i;
                    earlyOut[outputIndex] += earlySpectrum[i] * outWindow[i];
                    lateOut[outputIndex] += lateSpectrum[i] * outWindow[i];
                    norm[outputIndex] += normWindow[i];
                }

                processedFrames++;
                if (progress != null) progress((double)processedFrames / (double)Math.Max(1, totalFrames));
            }

            for (int i = 0; i < inputFrames; i++) {
                if (norm[i] > 1.0e-30) {
                    earlyOut[i] /= norm[i];
                    lateOut[i] /= norm[i];
                }
                else {
                    earlyOut[i] = 0.0;
                    lateOut[i] = 0.0;
                }
            }
        }

        private static void BuildMagnitudes(double[] spectrum, double[] magnitudes) {
            int binCount = spectrum.Length / 2;
            magnitudes[0] = Math.Abs(spectrum[0]);
            magnitudes[binCount] = Math.Abs(spectrum[1]);
            for (int bin = 1; bin < binCount; bin++) {
                int si = bin * 2;
                double re = spectrum[si];
                double im = spectrum[si + 1];
                magnitudes[bin] = Math.Sqrt((re * re) + (im * im));
            }
        }

        private static void SmoothReverbMasks(double[] earlyMask, double[] lateMask, double[] smoothEarlyMask, double[] smoothLateMask, double[] stableHold, bool[] forceDryFrame, int binCount) {
            // Reverb-only frequency smoothing: no hard frequency bands and no dry-path filtering.
            // The dry signal is still reconstructed later as original - earlyReverb - lateReverb.
            for (int bin = 1; bin < binCount; bin++) {
                if (forceDryFrame[bin]) {
                    smoothEarlyMask[bin] = 0.0;
                    smoothLateMask[bin] = 0.0;
                    continue;
                }

                double centerWeight = 0.50;
                double sideWeight = 0.25;

                double e = earlyMask[bin] * centerWeight;
                double l = lateMask[bin] * centerWeight;
                double weight = centerWeight;

                if (bin > 1) {
                    e += earlyMask[bin - 1] * sideWeight;
                    l += lateMask[bin - 1] * sideWeight;
                    weight += sideWeight;
                }
                if (bin < binCount - 1) {
                    e += earlyMask[bin + 1] * sideWeight;
                    l += lateMask[bin + 1] * sideWeight;
                    weight += sideWeight;
                }

                smoothEarlyMask[bin] = e / weight;
                smoothLateMask[bin] = l / weight;

                // Smoothing can move a little reverb energy into a stable neighbour.
                // Keep the existing signal-dependent direct floor after the smoothing pass.
                ApplyDirectFloor(ref smoothEarlyMask[bin], ref smoothLateMask[bin], stableHold[bin]);
            }
        }

        private static double LowCutCurve(double freqHz, double lowCutHz) {
            if (lowCutHz <= 0.0) return 1.0;

            // Continuous curve instead of a hard frequency step. The requested lowcut is the
            // center of the transition so no audible band boundary is introduced.
            double transitionStart = lowCutHz * 0.75;
            double transitionEnd = lowCutHz * 1.25;
            if (transitionEnd <= transitionStart) return (freqHz >= lowCutHz) ? 1.0 : 0.0;
            return SmoothStep(transitionStart, transitionEnd, freqHz);
        }

        private static double ApplyDirectFloor(double hallMask, double stableScore) {
            if (hallMask <= 0.0) return 0.0;
            double maxByDryFloor = MaxMaskByDirectFloor(stableScore);
            if (hallMask > maxByDryFloor) return maxByDryFloor;
            return hallMask;
        }

        private static void ApplyDirectFloor(ref double earlyMask, ref double lateMask, double stableScore) {
            double total = earlyMask + lateMask;
            if (total <= 0.0) return;

            double maxByDryFloor = MaxMaskByDirectFloor(stableScore);
            if (total <= maxByDryFloor) return;

            double scale = maxByDryFloor / total;
            earlyMask *= scale;
            lateMask *= scale;
        }

        private static double MaxMaskByDirectFloor(double stableScore) {
            // No fixed maximum hall mask: only keep a small signal-dependent dry floor so
            // the dereverbed bin is not completely emptied or inverted. Stable/tonal bins
            // keep a larger dry floor than clearly diffuse tails.
            double dryFloor = 0.02 + (0.18 * Clamp(stableScore, 0.0, 1.0));
            return 1.0 - dryFloor;
        }

        private static double LocalPeakScore(double[] magnitudes, int bin, int binCount) {
            double sum = 0.0;
            int count = 0;
            for (int offset = -2; offset <= 2; offset++) {
                if (offset == 0) continue;
                int other = bin + offset;
                if (other < 1 || other >= binCount) continue;
                sum += magnitudes[other];
                count++;
            }
            if (count == 0) return 0.0;
            double avg = sum / (double)count;
            double ratio = magnitudes[bin] / (avg + 1.0e-30);
            return SmoothStep(1.3, 3.0, ratio);
        }

        private static double SmoothMask(double previous, double current, double riseCoeff, double fallCoeff) {
            double coeff = (current < previous) ? fallCoeff : riseCoeff;
            return previous + ((current - previous) * coeff);
        }

        private static double SmoothStep(double edge0, double edge1, double x) {
            if (edge1 <= edge0) return (x >= edge1) ? 1.0 : 0.0;
            double t = Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
            return t * t * (3.0 - (2.0 * t));
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
