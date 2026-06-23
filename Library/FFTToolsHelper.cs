using System;
using System.IO;

namespace FFTTools {
    public static class FFTToolsHelper {
        public delegate bool Progress(string phase, double percent);

        private const int ReadBlock = 262144;
        private const int DefaultChunkSeconds = 20;

        public static void RunUpmix(string pathSrc, string pathDst, double ampFactor, LCRWideOptions options, DereverbOptions dereverbOptions, Progress progress) {
            IWaveFrameSource aSrc = WaveReadWrite.OpenInput(pathSrc);
            IWaveFrameSink aDst = null;

            try {
                if (aSrc.Channels != 2) {
                    throw new Exception("Input audio must be stereo for --upmix.");
                }
                if ((aSrc.SampleBits % 8) != 0) {
                    throw new Exception("WAV sample depth must use complete bytes.");
                }
                if (String.Equals(pathSrc, "stdin", StringComparison.OrdinalIgnoreCase)) {
                    throw new Exception("--upmix needs a seekable input for chunked dereverb processing.");
                }

                int inputBitsPerSample = aSrc.SampleBits;
                bool inputIsFloat = aSrc.FloatingPoint;
                int sampleRate = aSrc.Rate;
                long totalFrames = aSrc.TotalFrames;
                int outBitsPerSample = inputBitsPerSample;
                bool outIsFloat = inputIsFloat;

                aDst = WaveReadWrite.CreateOutput(pathDst, outBitsPerSample, 8, sampleRate, totalFrames, outIsFloat);

                int dereverbWindowSize = ResolveDereverbWindowSize(sampleRate);
                int dereverbOverlap = 16;
                int guardFrames = ResolveDereverbGuardFrames(sampleRate, dereverbWindowSize, dereverbOptions);
                int chunkFrames = ResolveChunkFrames(sampleRate, guardFrames);
                byte[] bytesDst = new byte[ReadBlock * (outBitsPerSample / 8) * 8];
                double[,] writeTemp = new double[ReadBlock, 8];

                long written = 0L;
                while (written < totalFrames) {
                    int writeCount = (int)Math.Min((long)chunkFrames, totalFrames - written);
                    long guardStart = Math.Max(0L, written - (long)guardFrames);
                    long guardEnd = Math.Min(totalFrames, written + (long)writeCount + (long)guardFrames);
                    int chunkLength = CheckedInt(guardEnd - guardStart, "Upmix chunk is too large.");
                    int middleOffset = CheckedInt(written - guardStart, "Upmix chunk offset is invalid.");

                    double[,] input = ReadAudioRange(aSrc, guardStart, chunkLength, inputBitsPerSample, 2, inputIsFloat);
                    double[,] dereverb = FFTToolsDereverb.Process(input, chunkLength, sampleRate, dereverbWindowSize, dereverbOverlap, dereverbOptions, null);
                    double[,] avr8 = FFTToolsUpmix.RenderDereverbUpmixChunk(dereverb, chunkLength, sampleRate, ampFactor, options, null);

                    WriteDoubleRange(aDst, bytesDst, writeTemp, avr8, middleOffset, writeCount, outBitsPerSample, 8, outIsFloat);
                    written += writeCount;
                    if (progress != null) progress("Upmix", (double)written / (double)Math.Max(1L, totalFrames));
                }
            }
            finally {
                if (aSrc != null) aSrc.Finish();
                if (aDst != null) try { aDst.Finish(); } catch { }
            }
        }

        public static void RunUpmixSpectrum(string pathSrc, string pathDst, double ampFactor, LCRWideOptions options, Progress progress) {
            FFTToolsUpmixSpectrum.Run(pathSrc, pathDst, ampFactor, options, progress);
        }

        public static void RunDereverb(string pathSrc, string pathDst, DereverbOptions options, Progress progress) {
            IWaveFrameSource aSrc = WaveReadWrite.OpenInput(pathSrc);
            IWaveFrameSink aDst = null;

            try {
                if (aSrc.Channels != 2) {
                    throw new Exception("Input audio must be stereo for --dereverb.");
                }
                if ((aSrc.SampleBits % 8) != 0) {
                    throw new Exception("WAV sample depth must use complete bytes.");
                }
                if (String.Equals(pathSrc, "stdin", StringComparison.OrdinalIgnoreCase)) {
                    throw new Exception("--dereverb needs a seekable input for chunked processing.");
                }

                int inputBitsPerSample = aSrc.SampleBits;
                bool inputIsFloat = aSrc.FloatingPoint;
                int sampleRate = aSrc.Rate;
                long totalFrames = aSrc.TotalFrames;
                int windowSize = ResolveDereverbWindowSize(sampleRate);
                int overlapCount = 16;
                int guardFrames = ResolveDereverbGuardFrames(sampleRate, windowSize, options);
                int chunkFrames = ResolveChunkFrames(sampleRate, guardFrames);

                int outBitsPerSample = inputBitsPerSample;
                bool outIsFloat = inputIsFloat;
                aDst = WaveReadWrite.CreateOutput(pathDst, outBitsPerSample, 6, sampleRate, totalFrames, outIsFloat);

                byte[] bytesDst = new byte[ReadBlock * (outBitsPerSample / 8) * 6];
                double[,] writeTemp = new double[ReadBlock, 6];

                long written = 0L;
                while (written < totalFrames) {
                    int writeCount = (int)Math.Min((long)chunkFrames, totalFrames - written);
                    long guardStart = Math.Max(0L, written - (long)guardFrames);
                    long guardEnd = Math.Min(totalFrames, written + (long)writeCount + (long)guardFrames);
                    int chunkLength = CheckedInt(guardEnd - guardStart, "Dereverb chunk is too large.");
                    int middleOffset = CheckedInt(written - guardStart, "Dereverb chunk offset is invalid.");

                    double[,] input = ReadAudioRange(aSrc, guardStart, chunkLength, inputBitsPerSample, 2, inputIsFloat);
                    double[,] output = FFTToolsDereverb.Process(input, chunkLength, sampleRate, windowSize, overlapCount, options, null);

                    WriteDoubleRange(aDst, bytesDst, writeTemp, output, middleOffset, writeCount, outBitsPerSample, 6, outIsFloat);
                    written += writeCount;
                    if (progress != null) progress("Dereverb", (double)written / (double)Math.Max(1L, totalFrames));
                }
            }
            finally {
                if (aSrc != null) aSrc.Finish();
                if (aDst != null) try { aDst.Finish(); } catch { }
            }
        }

        public static void RunEnhance(string pathSrc, string pathDst, int rateOption, double? normalizeDb, Progress progress) {
            IWaveFrameSource probe = WaveReadWrite.OpenInput(pathSrc);
            try {
                if ((probe.SampleBits % 8) != 0) {
                    throw new Exception("WAV sample depth must use complete bytes.");
                }
                if (String.Equals(pathSrc, "stdin", StringComparison.OrdinalIgnoreCase)) {
                    throw new Exception("--enhance needs a seekable input for chunked processing.");
                }

                int channels = probe.Channels;
                int sampleRate = probe.Rate;
                int targetRate = ResolveTargetRate(sampleRate, rateOption);
                int factor = ResolveEnhanceFactor(sampleRate, targetRate);
                int enhancedRate = sampleRate * factor;
                bool inputIsFloat = probe.FloatingPoint;
                int inputBitsPerSample = probe.SampleBits;
                long inputFrames = probe.TotalFrames;
                int outBitsPerSample = inputBitsPerSample;
                bool outIsFloat = inputIsFloat;
                probe.Finish();
                probe = null;

                bool needsResample = enhancedRate != targetRate;
                bool needsNormalize = normalizeDb.HasValue;
                if ((needsResample || needsNormalize) && pathDst == "stdout") {
                    throw new Exception("Streaming --enhance with resampling or normalization cannot write to stdout because it needs a temporary WAV file.");
                }

                string enhancedTemp = null;
                string resampledTemp = null;
                string currentTemp = null;

                try {
                    if (needsResample || needsNormalize) {
                        enhancedTemp = CreateTempPath(pathDst, "enhanced");
                        DeleteIfExists(enhancedTemp);
                        WriteEnhancedChunked(pathSrc, enhancedTemp, true, channels, sampleRate, inputBitsPerSample, inputIsFloat, factor, enhancedRate, outBitsPerSample, outIsFloat, inputFrames, progress);
                        currentTemp = enhancedTemp;
                    }
                    else {
                        WriteEnhancedChunked(pathSrc, pathDst, false, channels, sampleRate, inputBitsPerSample, inputIsFloat, factor, enhancedRate, outBitsPerSample, outIsFloat, inputFrames, progress);
                        return;
                    }

                    if (needsResample) {
                        bool resampleToTemp = needsNormalize;
                        string resampleOutput = resampleToTemp ? CreateTempPath(pathDst, "resampled") : pathDst;
                        if (resampleToTemp) {
                            resampledTemp = resampleOutput;
                            DeleteIfExists(resampledTemp);
                        }

                        ResampleWavFileChunked(currentTemp, resampleOutput, !resampleToTemp, targetRate, outBitsPerSample, outIsFloat, progress);
                        if (currentTemp != null && currentTemp != resampledTemp) {
                            DeleteIfExists(currentTemp);
                        }
                        currentTemp = resampleToTemp ? resampledTemp : null;
                    }

                    if (needsNormalize) {
                        NormalizeWavFileToOutput(currentTemp, pathDst, normalizeDb.Value, outBitsPerSample, outIsFloat, progress);
                        DeleteIfExists(currentTemp);
                        currentTemp = null;
                    }
                }
                finally {
                    if (currentTemp != null) DeleteIfExists(currentTemp);
                    if (enhancedTemp != null) DeleteIfExists(enhancedTemp);
                    if (resampledTemp != null) DeleteIfExists(resampledTemp);
                }
            }
            finally {
                if (probe != null) probe.Finish();
            }
        }

        private static void WriteEnhancedChunked(string pathSrc, string pathDst, bool tempWav, int channels, int sampleRate, int inputBitsPerSample, bool inputIsFloat, int factor, int enhancedRate, int outBitsPerSample, bool outIsFloat, long inputFrames, Progress progress) {
            IWaveFrameSource aSrc = WaveReadWrite.OpenInput(pathSrc);
            IWaveFrameSink aDst = null;
            try {
                long outputFrames = inputFrames * (long)factor;
                aDst = tempWav
                    ? (IWaveFrameSink)new WaveFileWriter(pathDst, outBitsPerSample, channels, enhancedRate, outIsFloat)
                    : WaveReadWrite.CreateOutput(pathDst, outBitsPerSample, channels, enhancedRate, outputFrames, outIsFloat);
                aDst.ExpectedFrameCount = outputFrames;

                int windowSize = ResolveEnhanceWindowSize(sampleRate);
                int guardFrames = ResolveEnhanceGuardFrames(windowSize);
                int chunkFrames = ResolveChunkFrames(sampleRate, guardFrames);
                EnhanceOptions enhanceOptions = new EnhanceOptions();
                enhanceOptions.Factor = factor;
                enhanceOptions.TargetRate = enhancedRate;

                byte[] bytesDst = new byte[ReadBlock * (outBitsPerSample / 8) * channels];
                double[,] writeTemp = new double[ReadBlock, channels];

                long writtenInput = 0L;
                while (writtenInput < inputFrames) {
                    int inputWriteCount = (int)Math.Min((long)chunkFrames, inputFrames - writtenInput);
                    long guardStart = Math.Max(0L, writtenInput - (long)guardFrames);
                    long guardEnd = Math.Min(inputFrames, writtenInput + (long)inputWriteCount + (long)guardFrames);
                    int chunkLength = CheckedInt(guardEnd - guardStart, "Enhance chunk is too large.");
                    int middleOffsetIn = CheckedInt(writtenInput - guardStart, "Enhance chunk offset is invalid.");
                    int outputOffset = middleOffsetIn * factor;
                    int outputCount = inputWriteCount * factor;

                    double[,] input = ReadAudioRange(aSrc, guardStart, chunkLength, inputBitsPerSample, channels, inputIsFloat);
                    double[,] output = FFTToolsEnhance.Process(input, chunkLength, channels, sampleRate, windowSize, enhanceOptions, null);

                    WriteDoubleRange(aDst, bytesDst, writeTemp, output, outputOffset, outputCount, outBitsPerSample, channels, outIsFloat);
                    writtenInput += inputWriteCount;
                    if (progress != null) progress("Enhance", (double)writtenInput / (double)Math.Max(1L, inputFrames));
                }
            }
            finally {
                if (aSrc != null) aSrc.Finish();
                if (aDst != null) try { aDst.Finish(); } catch { }
            }
        }

        private static void ResampleWavFileChunked(string pathSrc, string pathDst, bool finalOutput, int targetRate, int outBitsPerSample, bool outIsFloat, Progress progress) {
            WaveFileReader aSrc = new WaveFileReader(pathSrc);
            IWaveFrameSink aDst = null;
            try {
                int channels = aSrc.Channels;
                int inputRate = aSrc.Rate;
                long inputFrames = aSrc.TotalFrames;
                long outputFrames = DivCeil(inputFrames * (long)targetRate, (long)inputRate);
                aDst = finalOutput
                    ? WaveReadWrite.CreateOutput(pathDst, outBitsPerSample, channels, targetRate, outputFrames, outIsFloat)
                    : (IWaveFrameSink)new WaveFileWriter(pathDst, outBitsPerSample, channels, targetRate, outIsFloat);
                aDst.ExpectedFrameCount = outputFrames;

                int outputChunkFrames = Math.Max(ReadBlock, targetRate * 10);
                int inputGuard = HighQualityResampler.StreamHalfTaps + 4;
                byte[] bytesDst = new byte[ReadBlock * (outBitsPerSample / 8) * channels];
                double[,] writeTemp = new double[ReadBlock, channels];

                long outputPos = 0L;
                while (outputPos < outputFrames) {
                    int outCount = (int)Math.Min((long)outputChunkFrames, outputFrames - outputPos);
                    double timeStep = (double)inputRate / (double)targetRate;
                    long centerFirst = (long)Math.Floor((double)outputPos * timeStep);
                    long centerLast = (long)Math.Floor((double)(outputPos + (long)outCount - 1L) * timeStep);
                    long inputStart = Math.Max(0L, centerFirst - (long)inputGuard);
                    long inputEnd = Math.Min(inputFrames, centerLast + (long)inputGuard + 1L);
                    int inputCount = CheckedInt(inputEnd - inputStart, "Resample chunk is too large.");

                    double[,] input = ReadAudioRange(aSrc, inputStart, inputCount, aSrc.SampleBits, channels, aSrc.FloatingPoint);
                    double[,] output = HighQualityResampler.ResampleRange(input, inputStart, inputFrames, inputCount, channels, inputRate, targetRate, outputPos, outCount);
                    WriteDoubleRange(aDst, bytesDst, writeTemp, output, 0, outCount, outBitsPerSample, channels, outIsFloat);

                    outputPos += outCount;
                    if (progress != null) progress("Resampling", (double)outputPos / (double)Math.Max(1L, outputFrames));
                }
            }
            finally {
                if (aSrc != null) aSrc.Finish();
                if (aDst != null) try { aDst.Finish(); } catch { }
            }
        }

        private static void NormalizeWavFileToOutput(string pathSrc, string pathDst, double targetDb, int outBitsPerSample, bool outIsFloat, Progress progress) {
            double peak = ScanWavPeak(pathSrc, progress);
            double target = Math.Pow(10.0, targetDb / 20.0);
            double gain = (peak > 0.0) ? (target / peak) : 1.0;

            WaveFileReader aSrc = new WaveFileReader(pathSrc);
            IWaveFrameSink aDst = null;
            try {
                int channels = aSrc.Channels;
                long totalFrames = aSrc.TotalFrames;
                aDst = WaveReadWrite.CreateOutput(pathDst, outBitsPerSample, channels, aSrc.Rate, totalFrames, outIsFloat);

                byte[] bytesSrc = new byte[ReadBlock * ((aSrc.SampleBits + 7) / 8) * channels];
                byte[] bytesDst = new byte[ReadBlock * (outBitsPerSample / 8) * channels];
                double[,] temp = new double[ReadBlock, channels];
                long done = 0L;
                while (aSrc.FramesAvailable > 0) {
                    int count = (int)Math.Min((long)ReadBlock, aSrc.FramesAvailable);
                    aSrc.Read(bytesSrc, count);
                    SampleCodec.ConvertSamples(SampleTransferMode.DecodeFromBytes, bytesSrc, 0, temp, 0, count, aSrc.SampleBits, channels, aSrc.FloatingPoint);
                    for (int i = 0; i < count; i++) {
                        for (int ch = 0; ch < channels; ch++) {
                            temp[i, ch] *= gain;
                        }
                    }
                    SampleCodec.ConvertSamples(SampleTransferMode.EncodeToBytes, bytesDst, 0, temp, 0, count, outBitsPerSample, channels, outIsFloat);
                    aDst.Write(bytesDst, count);
                    done += count;
                    if (progress != null) progress("Normalizing", (double)done / (double)Math.Max(1L, totalFrames));
                }
            }
            finally {
                if (aSrc != null) aSrc.Finish();
                if (aDst != null) try { aDst.Finish(); } catch { }
            }
        }

        private static double ScanWavPeak(string pathSrc, Progress progress) {
            WaveFileReader aSrc = new WaveFileReader(pathSrc);
            try {
                int channels = aSrc.Channels;
                byte[] bytesSrc = new byte[ReadBlock * ((aSrc.SampleBits + 7) / 8) * channels];
                double[,] temp = new double[ReadBlock, channels];
                double peak = 0.0;
                long done = 0L;
                long totalFrames = aSrc.TotalFrames;
                while (aSrc.FramesAvailable > 0) {
                    int count = (int)Math.Min((long)ReadBlock, aSrc.FramesAvailable);
                    aSrc.Read(bytesSrc, count);
                    SampleCodec.ConvertSamples(SampleTransferMode.DecodeFromBytes, bytesSrc, 0, temp, 0, count, aSrc.SampleBits, channels, aSrc.FloatingPoint);
                    for (int i = 0; i < count; i++) {
                        for (int ch = 0; ch < channels; ch++) {
                            double value = Math.Abs(temp[i, ch]);
                            if (value > peak) peak = value;
                        }
                    }
                    done += count;
                    if (progress != null) progress("Peak scan", (double)done / (double)Math.Max(1L, totalFrames));
                }
                return peak;
            }
            finally {
                if (aSrc != null) aSrc.Finish();
            }
        }

        private static double[,] ReadAudioRange(IWaveFrameSource source, long startFrame, int frameCount, int bitsPerSample, int channels, bool isFloat) {
            if (frameCount < 0) throw new ArgumentOutOfRangeException("frameCount");
            source.FramePosition = startFrame;
            byte[] bytes = new byte[frameCount * ((bitsPerSample + 7) / 8) * channels];
            double[,] result = new double[frameCount, channels];
            int read = source.Read(bytes, frameCount);
            if (read != frameCount) {
                throw new Exception("Unexpected end of input while reading a processing chunk.");
            }
            SampleCodec.ConvertSamples(SampleTransferMode.DecodeFromBytes, bytes, 0, result, 0, frameCount, bitsPerSample, channels, isFloat);
            return result;
        }

        private static void WriteDoubleRange(IWaveFrameSink dest, byte[] bytesDst, double[,] temp, double[,] samples, int offset, int count, int bitsPerSample, int channels, bool isFloat) {
            int written = 0;
            while (written < count) {
                int block = Math.Min(ReadBlock, count - written);
                for (int i = 0; i < block; i++) {
                    for (int ch = 0; ch < channels; ch++) {
                        temp[i, ch] = samples[offset + written + i, ch];
                    }
                }
                SampleCodec.ConvertSamples(SampleTransferMode.EncodeToBytes, bytesDst, 0, temp, 0, block, bitsPerSample, channels, isFloat);
                dest.Write(bytesDst, block);
                written += block;
            }
        }

        private static int ResolveDereverbWindowSize(int inputRate) {
            const int baseWindowSize = 8192;
            const int baseRate = 48000;
            if (inputRate <= baseRate) return baseWindowSize;

            double scaled = (double)baseWindowSize * ((double)inputRate / (double)baseRate);
            int required = (int)Math.Ceiling(scaled);
            int windowSize = baseWindowSize;
            while (windowSize < required) {
                windowSize <<= 1;
                if (windowSize <= 0) throw new Exception("Dereverb FFT window size overflow.");
            }
            return windowSize;
        }

        private static int ResolveEnhanceWindowSize(int inputRate) {
            const int baseWindowSize = 8192;
            const int baseRate = 48000;
            if (inputRate <= baseRate) return baseWindowSize;

            double scaled = (double)baseWindowSize * ((double)inputRate / (double)baseRate);
            int required = (int)Math.Ceiling(scaled);
            int windowSize = baseWindowSize;
            while (windowSize < required) {
                windowSize <<= 1;
                if (windowSize <= 0) throw new Exception("Enhance FFT window size overflow.");
            }
            return windowSize;
        }

        private static int ResolveDereverbGuardFrames(int sampleRate, int windowSize, DereverbOptions options) {
            if (options == null) options = new DereverbOptions();
            double maxTailMs = Math.Max(1.0, options.MaxTailMs);
            int tailFrames = (int)Math.Ceiling((double)sampleRate * maxTailMs / 1000.0);
            return Math.Max(windowSize * 2, tailFrames + windowSize);
        }

        private static int ResolveEnhanceGuardFrames(int windowSize) {
            return windowSize * 3;
        }

        private static int ResolveChunkFrames(int sampleRate, int guardFrames) {
            long preferred = (long)Math.Max(1, sampleRate) * (long)DefaultChunkSeconds;
            long minimum = Math.Max(preferred, (long)guardFrames * 4L);
            if (minimum > 12000000L) minimum = 12000000L;
            if (minimum < 262144L) minimum = 262144L;
            return CheckedInt(minimum, "Processing chunk size is too large.");
        }

        private static int ResolveTargetRate(int inputRate, int rateOption) {
            if (rateOption < 0) {
                throw new Exception("--rate / -r must be 0, 1, 2, 4, 8 or an absolute sample rate in Hz.");
            }
            if (rateOption == 0 || rateOption == 1) return inputRate;
            if (rateOption == 2 || rateOption == 4 || rateOption == 8) return inputRate * rateOption;
            return rateOption;
        }

        private static int ResolveEnhanceFactor(int inputRate, int targetRate) {
            if (inputRate <= 0 || targetRate <= 0) {
                throw new Exception("Target sample rate is invalid.");
            }
            int factor = 1;
            while ((long)inputRate * (long)factor < (long)targetRate) {
                factor <<= 1;
                if (factor > 8) {
                    throw new Exception("Enhance supports an internal FFT rate factor up to 8 only.");
                }
            }
            return factor;
        }

        private static string CreateTempPath(string pathDst, string label) {
            string directory = Path.GetDirectoryName(Path.GetFullPath(pathDst));
            string fileName = Path.GetFileName(pathDst);
            return Path.Combine(directory, fileName + "." + label + ".wav.tmp");
        }

        private static void DeleteIfExists(string path) {
            if (String.IsNullOrEmpty(path)) return;
            try {
                if (File.Exists(path)) File.Delete(path);
            }
            catch {
            }
        }

        private static double Clamp(double value, double min, double max) {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static int CheckedInt(long value, string message) {
            if (value < 0L || value > (long)Int32.MaxValue) {
                throw new Exception(message);
            }
            return (int)value;
        }

        private static long DivCeil(long numerator, long denominator) {
            if (denominator <= 0L) throw new ArgumentOutOfRangeException("denominator");
            if (numerator <= 0L) return 0L;
            return ((numerator - 1L) / denominator) + 1L;
        }
    }
}
