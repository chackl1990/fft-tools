using System;
using System.Threading;

namespace FFTTools {
    public static class HighQualityResampler {
        private const double SoxVeryHighBandwidth = 0.95;
        private const double SoxVeryHighRejectionDb = 175.0;
        private const int SteepLinearPhaseTaps = 1024;
        public const int StreamHalfTaps = SteepLinearPhaseTaps / 2;
        private const int FractionalPhaseCount = 4096;

        public static double[,] Resample(double[,] input, int inputFrames, int channels, int inputRate, int outputRate) {
            return Resample(input, inputFrames, channels, inputRate, outputRate, null);
        }

        public static double[,] Resample(double[,] input, int inputFrames, int channels, int inputRate, int outputRate, Action<double> progress) {
            if (input == null) throw new ArgumentNullException("input");
            if (inputFrames < 0) throw new ArgumentOutOfRangeException("inputFrames");
            if (channels <= 0) throw new ArgumentOutOfRangeException("channels");
            if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException("sampleRate");
            if (inputRate == outputRate) {
                if (progress != null) progress(1.0);
                return input;
            }
            if (inputFrames == 0) {
                if (progress != null) progress(1.0);
                return new double[0, channels];
            }

            long outputFramesLong = ((long)inputFrames * (long)outputRate + (long)inputRate - 1L) / (long)inputRate;
            if (outputFramesLong > Int32.MaxValue) {
                throw new Exception("Resampled output is too large to fit into memory.");
            }
            int outputFrames = (int)outputFramesLong;
            double[,] output = new double[outputFrames, channels];

            int taps = SteepLinearPhaseTaps;
            int halfTaps = taps / 2;
            double ratio = (double)outputRate / (double)inputRate;
            double cutoff = 0.5 * SoxVeryHighBandwidth * Math.Min(1.0, ratio);
            double timeStep = (double)inputRate / (double)outputRate;

            ResampleKernelTable table = ResampleKernelTable.Create(taps, FractionalPhaseCount, cutoff);
            int maxDegreeOfParallelism = Math.Max(1, Math.Min(channels, WorkerPool.ThreadCount));

            if (channels == 1 || maxDegreeOfParallelism == 1) {
                ResampleChannel(input, output, inputFrames, outputFrames, 0, timeStep, halfTaps, taps, table, progress);
            }
            else {
                long completedWork = 0L;
                long totalWork = (long)outputFrames * (long)channels;
                int progressStep = Math.Max(1, outputFrames / 1000);
                int[] lastProgressPermille = new int[] { -1 };
                object progressLock = new object();

                WorkerPool.For(0, channels, maxDegreeOfParallelism, delegate(int ch) {
                    int localPending = 0;
                    for (int outIndex = 0; outIndex < outputFrames; outIndex++) {
                        output[outIndex, ch] = ResampleOne(input, inputFrames, ch, outIndex, timeStep, halfTaps, taps, table);
                        localPending++;
                        if (localPending >= progressStep || outIndex == outputFrames - 1) {
                            long done = Interlocked.Add(ref completedWork, localPending);
                            localPending = 0;
                            ReportProgress(progress, done, totalWork, lastProgressPermille, progressLock);
                        }
                    }
                });
                if (progress != null) progress(1.0);
            }

            return output;
        }


        private static void ResampleChannel(double[,] input, double[,] output, int inputFrames, int outputFrames, int channel, double timeStep, int halfTaps, int taps, ResampleKernelTable table, Action<double> progress) {
            int progressStep = Math.Max(1, outputFrames / 1000);
            for (int outIndex = 0; outIndex < outputFrames; outIndex++) {
                output[outIndex, channel] = ResampleOne(input, inputFrames, channel, outIndex, timeStep, halfTaps, taps, table);
                if (progress != null && ((outIndex % progressStep) == 0 || outIndex == outputFrames - 1)) {
                    progress((double)(outIndex + 1) / (double)outputFrames);
                }
            }
        }

        private static double ResampleOne(double[,] input, int inputFrames, int channel, int outIndex, double timeStep, int halfTaps, int taps, ResampleKernelTable table) {
            double sourcePosition = (double)outIndex * timeStep;
            int center = (int)Math.Floor(sourcePosition);
            double frac = sourcePosition - (double)center;
            int phaseIndex = (int)Math.Round(frac * (double)FractionalPhaseCount);
            if (phaseIndex >= FractionalPhaseCount) {
                phaseIndex = 0;
                center++;
            }

            double[] weights = table.Weights[phaseIndex];
            int firstTap = center - halfTaps + 1;
            int firstValidTap = 0;
            int lastValidTap = taps - 1;

            if (firstTap < 0) firstValidTap = -firstTap;
            int overEnd = firstTap + lastValidTap - (inputFrames - 1);
            if (overEnd > 0) lastValidTap -= overEnd;
            if (firstValidTap > lastValidTap) return 0.0;

            double weightSum = table.WeightSums[phaseIndex];
            if (firstValidTap != 0 || lastValidTap != taps - 1) {
                weightSum = 0.0;
                for (int tap = firstValidTap; tap <= lastValidTap; tap++) {
                    weightSum += weights[tap];
                }
            }
            if (Math.Abs(weightSum) < 1.0e-300) weightSum = 1.0;

            double sum = 0.0;
            for (int tap = firstValidTap; tap <= lastValidTap; tap++) {
                int sourceIndex = firstTap + tap;
                sum += input[sourceIndex, channel] * weights[tap];
            }

            return sum / weightSum;
        }


        public static double[,] ResampleRange(double[,] input, long inputStartFrame, long totalInputFrames, int inputFrames, int channels, int inputRate, int outputRate, long outputStartFrame, int outputFrames) {
            if (input == null) throw new ArgumentNullException("input");
            if (inputFrames < 0 || inputFrames > input.GetLength(0)) throw new ArgumentOutOfRangeException("inputFrames");
            if (channels <= 0 || channels > input.GetLength(1)) throw new ArgumentOutOfRangeException("channels");
            if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException("sampleRate");
            if (outputFrames < 0) throw new ArgumentOutOfRangeException("outputFrames");

            double[,] output = new double[outputFrames, channels];
            if (outputFrames == 0) return output;

            int taps = SteepLinearPhaseTaps;
            int halfTaps = taps / 2;
            double ratio = (double)outputRate / (double)inputRate;
            double cutoff = 0.5 * SoxVeryHighBandwidth * Math.Min(1.0, ratio);
            double timeStep = (double)inputRate / (double)outputRate;
            ResampleKernelTable table = ResampleKernelTable.Create(taps, FractionalPhaseCount, cutoff);

            int maxDegreeOfParallelism = Math.Max(1, Math.Min(channels, WorkerPool.ThreadCount));
            if (channels == 1 || maxDegreeOfParallelism == 1) {
                for (int ch = 0; ch < channels; ch++) {
                    for (int i = 0; i < outputFrames; i++) {
                        output[i, ch] = ResampleOneRange(input, inputStartFrame, totalInputFrames, inputFrames, ch, outputStartFrame + (long)i, timeStep, halfTaps, taps, table);
                    }
                }
            }
            else {
                WorkerPool.For(0, channels, maxDegreeOfParallelism, delegate(int ch) {
                    for (int i = 0; i < outputFrames; i++) {
                        output[i, ch] = ResampleOneRange(input, inputStartFrame, totalInputFrames, inputFrames, ch, outputStartFrame + (long)i, timeStep, halfTaps, taps, table);
                    }
                });
            }

            return output;
        }

        private static double ResampleOneRange(double[,] input, long inputStartFrame, long totalInputFrames, int inputFrames, int channel, long outIndexGlobal, double timeStep, int halfTaps, int taps, ResampleKernelTable table) {
            double sourcePosition = (double)outIndexGlobal * timeStep;
            long center = (long)Math.Floor(sourcePosition);
            double frac = sourcePosition - (double)center;
            int phaseIndex = (int)Math.Round(frac * (double)FractionalPhaseCount);
            if (phaseIndex >= FractionalPhaseCount) {
                phaseIndex = 0;
                center++;
            }

            double[] weights = table.Weights[phaseIndex];
            long firstTap = center - (long)halfTaps + 1L;
            double weightSum = 0.0;
            double sum = 0.0;

            for (int tap = 0; tap < taps; tap++) {
                long sourceIndexGlobal = firstTap + (long)tap;
                if (sourceIndexGlobal < 0L || sourceIndexGlobal >= totalInputFrames) continue;
                long localIndexLong = sourceIndexGlobal - inputStartFrame;
                if (localIndexLong < 0L || localIndexLong >= (long)inputFrames) continue;

                double weight = weights[tap];
                sum += input[(int)localIndexLong, channel] * weight;
                weightSum += weight;
            }

            if (Math.Abs(weightSum) < 1.0e-300) return 0.0;
            return sum / weightSum;
        }

        private static void ReportProgress(Action<double> progress, long completed, long total, int[] lastProgressPermille, object progressLock) {
            if (progress == null || total <= 0L) return;
            int permille = (int)Math.Round((double)completed * 1000.0 / (double)total);
            if (permille > 1000) permille = 1000;
            lock (progressLock) {
                if (permille != lastProgressPermille[0]) {
                    lastProgressPermille[0] = permille;
                    progress((double)permille / 1000.0);
                }
            }
        }
        private sealed class ResampleKernelTable {
            public double[][] Weights;
            public double[] WeightSums;

            public static ResampleKernelTable Create(int taps, int phaseCount, double cutoff) {
                ResampleKernelTable table = new ResampleKernelTable();
                table.Weights = new double[phaseCount][];
                table.WeightSums = new double[phaseCount];

                double beta = KaiserBeta(SoxVeryHighRejectionDb);
                double i0Beta = BesselI0(beta);
                double halfTaps = (double)taps / 2.0;

                for (int phase = 0; phase < phaseCount; phase++) {
                    double frac = (double)phase / (double)phaseCount;
                    double[] weights = new double[taps];
                    double sum = 0.0;

                    for (int tap = 0; tap < taps; tap++) {
                        double distance = ((double)tap - halfTaps + 1.0) - frac;
                        double windowPosition = ((double)tap + 0.5) / (double)taps;
                        double window = KaiserWindow(windowPosition, beta, i0Beta);
                        double weight = 2.0 * cutoff * Sinc(2.0 * cutoff * distance) * window;
                        weights[tap] = weight;
                        sum += weight;
                    }

                    table.Weights[phase] = weights;
                    table.WeightSums[phase] = sum;
                }

                return table;
            }
        }

        private static double Sinc(double x) {
            if (Math.Abs(x) < 1.0e-12) return 1.0;
            double pix = Math.PI * x;
            return Math.Sin(pix) / pix;
        }

        private static double KaiserWindow(double position, double beta, double i0Beta) {
            double x = (2.0 * position) - 1.0;
            double arg = 1.0 - (x * x);
            if (arg < 0.0) arg = 0.0;
            return BesselI0(beta * Math.Sqrt(arg)) / i0Beta;
        }

        private static double KaiserBeta(double attenuationDb) {
            if (attenuationDb > 50.0) return 0.1102 * (attenuationDb - 8.7);
            if (attenuationDb >= 21.0) return (0.5842 * Math.Pow(attenuationDb - 21.0, 0.4)) + (0.07886 * (attenuationDb - 21.0));
            return 0.0;
        }

        private static double BesselI0(double x) {
            double ax = Math.Abs(x);
            if (ax < 3.75) {
                double y = x / 3.75;
                y *= y;
                return 1.0 + y * (3.5156229 + y * (3.0899424 + y * (1.2067492 + y * (0.2659732 + y * (0.0360768 + y * 0.0045813)))));
            }
            else {
                double y = 3.75 / ax;
                return (Math.Exp(ax) / Math.Sqrt(ax)) * (0.39894228 + y * (0.01328592 + y * (0.00225319 + y * (-0.00157565 + y * (0.00916281 + y * (-0.02057706 + y * (0.02635537 + y * (-0.01647633 + y * 0.00392377))))))));
            }
        }
    }
}
