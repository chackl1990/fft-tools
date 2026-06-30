using System;

namespace FFTTools {
    public static class FFTToolsUpmixSpectrum {
        private const int ReadBlock = 262144;
        private const int WindowSize = 8192;
        private const int OverlapCount = 16;
        private const int DefaultChunkSeconds = 20;
        private const double NonCenterGain = 0.7071067811865476;

        public static void Run(string pathSrc, string pathDst, double ampFactor, LCRWideOptions options, FFTToolsHelper.Progress progress) {
            IWaveFrameSource aSrc = WaveReadWrite.OpenInput(pathSrc);
            IWaveFrameSink aDst = null;

            try {
                if (aSrc.Channels != 2) throw new Exception("Input audio must be stereo for --upmix-spectrum.");
                if ((aSrc.SampleBits % 8) != 0) throw new Exception("Input audio's bit depth must be a multiple of 8.");
                if (pathSrc == "stdin") throw new Exception("--upmix-spectrum needs a seekable input in this cleaned implementation.");

                int inputBitsPerSample = aSrc.SampleBits;
                bool inputIsFloat = aSrc.FloatingPoint;
                int sampleRate = aSrc.Rate;
                long totalFrames = aSrc.TotalFrames;
                int outBitsPerSample = inputBitsPerSample;
                bool outIsFloat = inputIsFloat;

                aDst = WaveReadWrite.CreateOutput(pathDst, outBitsPerSample, 8, sampleRate, totalFrames, outIsFloat);

                int guardFrames = WindowSize * 2;
                int chunkFrames = ResolveChunkFrames(sampleRate, guardFrames);
                byte[] bytesDst = new byte[ReadBlock * (outBitsPerSample / 8) * 8];
                double[,] writeTemp = new double[ReadBlock, 8];

                long written = 0L;
                while (written < totalFrames) {
                    int writeCount = (int)Math.Min((long)chunkFrames, totalFrames - written);
                    long guardStart = Math.Max(0L, written - (long)guardFrames);
                    long guardEnd = Math.Min(totalFrames, written + (long)writeCount + (long)guardFrames);
                    int chunkLength = CheckedInt(guardEnd - guardStart, "Upmix-spectrum chunk is too large.");
                    int middleOffset = CheckedInt(written - guardStart, "Upmix-spectrum chunk offset is invalid.");

                    double[,] input = ReadAudioRange(aSrc, guardStart, chunkLength, inputBitsPerSample, 2, inputIsFloat);
                    double[,] output = ProcessChunk(input, chunkLength, sampleRate, ampFactor, options ?? new LCRWideOptions());
                    WriteDoubleRange(aDst, bytesDst, writeTemp, output, middleOffset, writeCount, outBitsPerSample, 8, outIsFloat);

                    written += writeCount;
                    if (progress != null) progress("Upmix-spectrum", (double)written / (double)Math.Max(1L, totalFrames));
                }
            }
            finally {
                if (aSrc != null) aSrc.Finish();
                if (aDst != null) try { aDst.Finish(); } catch { }
            }
        }

        private static double[,] ProcessChunk(double[,] input, int frames, int sampleRate, double ampFactor, LCRWideOptions options) {
            int hop = WindowSize / OverlapCount;
            int binCount = WindowSize / 2;
            double binHz = (double)sampleRate / (double)WindowSize;
            int firstStart = -WindowSize + hop;
            int lastStart = frames - 1;

            double[,] output = new double[frames, 8];
            double[] norm = new double[frames];
            double[] inWindow = CreateRaisedCosineWindow(WindowSize, 1.0);
            double[] outWindow = CreateRaisedCosineWindow(WindowSize, 2.0);
            double[] outNormWindow = CreateRaisedCosineWindow(WindowSize, 1.0);
            double[] normWindow = new double[WindowSize];
            double inverseFftScale = (double)WindowSize / 2.0;
            for (int i = 0; i < WindowSize; i++) normWindow[i] = inverseFftScale * outNormWindow[i] * outWindow[i];

            RealFFT fft = new RealFFT(WindowSize);
            double[] left = new double[WindowSize];
            double[] right = new double[WindowSize];
            double[] frontL = new double[WindowSize];
            double[] frontR = new double[WindowSize];
            double[] center = new double[WindowSize];
            double[] sideL = new double[WindowSize];
            double[] sideR = new double[WindowSize];
            double[] backL = new double[WindowSize];
            double[] backR = new double[WindowSize];

            double centerGain = Clamp(options.CenterGain, 0.0, 2.0);
            double wideGain = Math.Max(0.0, options.WideGain);
            double wideExponent = Math.Max(0.05, options.WideExponent);
            double wideLowCutHz = Math.Max(0.0, options.WideLowCutHz);
            double widePhaseWeight = Math.Max(0.0, options.WidePhaseWeight);
            double panSharpness = Math.Max(0.1, options.LCR7PanSharpness);
            double centerPosition = Clamp(options.LCR7CLCRPosition, 0.05, 0.95);

            for (int blockStart = firstStart; blockStart <= lastStart; blockStart += hop) {
                Array.Clear(left, 0, left.Length);
                Array.Clear(right, 0, right.Length);
                Array.Clear(frontL, 0, frontL.Length);
                Array.Clear(frontR, 0, frontR.Length);
                Array.Clear(center, 0, center.Length);
                Array.Clear(sideL, 0, sideL.Length);
                Array.Clear(sideR, 0, sideR.Length);
                Array.Clear(backL, 0, backL.Length);
                Array.Clear(backR, 0, backR.Length);

                int copyStart = Math.Max(0, -blockStart);
                int copyEnd = Math.Min(WindowSize, frames - blockStart);
                for (int i = copyStart; i < copyEnd; i++) {
                    int sourceIndex = blockStart + i;
                    double w = inWindow[i];
                    left[i] = input[sourceIndex, 0] * w;
                    right[i] = input[sourceIndex, 1] * w;
                }

                fft.ComputeForward(left);
                fft.ComputeForward(right);

                // Use the same CenterExtract sum/difference common-signal estimate
                // as the dereverb based upmix mode. No additional pan gate,
                // phase-delay gate, signed-amplitude fold, or fragment prune is
                // applied in the Center stage.
                for (int bin = 1; bin < binCount; bin++) {
                    int si = bin * 2;
                    double cR;
                    double cI;
                    FFTToolsUpmix.BuildCenterExtractBin(
                        left[si],
                        left[si + 1],
                        right[si],
                        right[si + 1],
                        centerGain,
                        out cR,
                        out cI);
                    center[si] = cR;
                    center[si + 1] = cI;
                }

                for (int bin = 1; bin < binCount; bin++) {
                    int si = bin * 2;
                    double lR = left[si];
                    double lI = left[si + 1];
                    double rR = right[si];
                    double rI = right[si + 1];
                    double lMag = Math.Sqrt((lR * lR) + (lI * lI));
                    double rMag = Math.Sqrt((rR * rR) + (rI * rI));
                    double panPos = Clamp((rMag - lMag) / (lMag + rMag + 1.0e-20), -1.0, 1.0);

                    double cR = center[si];
                    double cI = center[si + 1];
                    double lResR = lR - cR;
                    double lResI = lI - cI;
                    double rResR = rR - cR;
                    double rResI = rI - cI;

                    double sideEnergyR = (lResR - rResR) * 0.5;
                    double sideEnergyI = (lResI - rResI) * 0.5;
                    double midEnergyR = (lResR + rResR) * 0.5;
                    double midEnergyI = (lResI + rResI) * 0.5;
                    double sideMag = Math.Sqrt((sideEnergyR * sideEnergyR) + (sideEnergyI * sideEnergyI));
                    double midMag = Math.Sqrt((midEnergyR * midEnergyR) + (midEnergyI * midEnergyI));
                    double sideRatio = sideMag / (sideMag + midMag + 1.0e-20);
                    double cosPhase = FFTToolsUpmix.CosPhase(lR, lI, rR, rI);
                    double phaseWide = Math.Sqrt(Clamp((1.0 - cosPhase) * 0.5, 0.0, 1.0));
                    double freqHz = (double)bin * binHz;
                    double lowCutMask = (wideLowCutHz > 0.0) ? SmoothLowCut(freqHz, wideLowCutHz) : 1.0;
                    double wideMask = sideRatio * ((widePhaseWeight <= 0.0) ? 1.0 : Math.Pow(phaseWide, widePhaseWeight));
                    wideMask = Math.Pow(Clamp(wideMask, 0.0, 1.0), wideExponent) * lowCutMask * wideGain;

                    double frontShare;
                    double surroundShare;
                    FFTToolsUpmix.ComputeFrontSidePanSplit(panPos, out frontShare, out surroundShare);

                    frontL[si] = lResR * frontShare;
                    frontL[si + 1] = lResI * frontShare;
                    frontR[si] = rResR * frontShare;
                    frontR[si + 1] = rResI * frontShare;
                    sideL[si] = lResR * surroundShare;
                    sideL[si + 1] = lResI * surroundShare;
                    sideR[si] = rResR * surroundShare;
                    sideR[si + 1] = rResI * surroundShare;
                    backL[si] = sideEnergyR * wideMask;
                    backL[si + 1] = sideEnergyI * wideMask;
                    backR[si] = -sideEnergyR * wideMask;
                    backR[si + 1] = -sideEnergyI * wideMask;
                }

                fft.ComputeReverse(frontL);
                fft.ComputeReverse(frontR);
                fft.ComputeReverse(center);
                fft.ComputeReverse(sideL);
                fft.ComputeReverse(sideR);
                fft.ComputeReverse(backL);
                fft.ComputeReverse(backR);

                int outStart = Math.Max(0, -blockStart);
                int outEnd = Math.Min(WindowSize, frames - blockStart);
                for (int i = outStart; i < outEnd; i++) {
                    int oi = blockStart + i;
                    double w = outWindow[i];
                    output[oi, 0] += frontL[i] * w;
                    output[oi, 1] += frontR[i] * w;
                    output[oi, 2] += center[i] * w;
                    output[oi, 4] += sideL[i] * w;
                    output[oi, 5] += sideR[i] * w;
                    output[oi, 6] += backL[i] * w;
                    output[oi, 7] += backR[i] * w;
                    norm[oi] += normWindow[i];
                }
            }

            for (int i = 0; i < frames; i++) {
                double scale = (norm[i] > 1.0e-30) ? (ampFactor / norm[i]) : 0.0;
                output[i, 0] *= scale * NonCenterGain;
                output[i, 1] *= scale * NonCenterGain;
                output[i, 2] *= scale;
                output[i, 3] = 0.0;
                output[i, 4] *= scale * NonCenterGain;
                output[i, 5] *= scale * NonCenterGain;
                output[i, 6] *= scale * NonCenterGain;
                output[i, 7] *= scale * NonCenterGain;
            }

            return output;
        }

        private static double SmoothLowCut(double freqHz, double lowCutHz) {
            double mask = freqHz / (freqHz + lowCutHz + 1.0e-20);
            return mask * mask;
        }

        private static double[,] ReadAudioRange(IWaveFrameSource source, long startFrame, int frameCount, int bitsPerSample, int channels, bool isFloat) {
            source.FramePosition = startFrame;
            byte[] bytes = new byte[frameCount * ((bitsPerSample + 7) / 8) * channels];
            double[,] result = new double[frameCount, channels];
            int read = source.Read(bytes, frameCount);
            if (read != frameCount) throw new Exception("Unexpected end of input while reading a processing chunk.");
            SampleCodec.ConvertSamples(SampleTransferMode.DecodeFromBytes, bytes, 0, result, 0, frameCount, bitsPerSample, channels, isFloat);
            return result;
        }

        private static void WriteDoubleRange(IWaveFrameSink dest, byte[] bytesDst, double[,] temp, double[,] samples, int offset, int count, int bitsPerSample, int channels, bool isFloat) {
            int written = 0;
            while (written < count) {
                int block = Math.Min(ReadBlock, count - written);
                for (int i = 0; i < block; i++) {
                    for (int ch = 0; ch < channels; ch++) temp[i, ch] = samples[offset + written + i, ch];
                }
                SampleCodec.ConvertSamples(SampleTransferMode.EncodeToBytes, bytesDst, 0, temp, 0, block, bitsPerSample, channels, isFloat);
                dest.Write(bytesDst, block);
                written += block;
            }
        }

        private static int ResolveChunkFrames(int sampleRate, int guardFrames) {
            long preferred = (long)Math.Max(1, sampleRate) * (long)DefaultChunkSeconds;
            long minimum = Math.Max(preferred, (long)guardFrames * 4L);
            if (minimum > 12000000L) minimum = 12000000L;
            if (minimum < 262144L) minimum = 262144L;
            return CheckedInt(minimum, "Processing chunk size is too large.");
        }

        private static int CheckedInt(long value, string message) {
            if (value < 0L || value > (long)Int32.MaxValue) throw new Exception(message);
            return (int)value;
        }

        private static double[] CreateRaisedCosineWindow(int n, double power) {
            return WindowMath.BuildPowerSineWindow(n, power);
        }

        private static double Clamp(double value, double min, double max) {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
