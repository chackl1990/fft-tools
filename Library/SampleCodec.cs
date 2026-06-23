using System;

namespace FFTTools {
    public enum SampleTransferMode {
        DecodeFromBytes,
        EncodeToBytes
    }

    public static class SampleCodec {
        public static void ConvertSamples(SampleTransferMode type, byte[] sampleBytes, int byteFrameOffset,
            double[,] sampleValues, int valueFrameOffset, int frameCount, int bitsPerSample, int channelCount)
        {
            ConvertSamples(type, sampleBytes, byteFrameOffset, sampleValues, valueFrameOffset, frameCount, bitsPerSample, channelCount, false);
        }

        public static unsafe void ConvertSamples(SampleTransferMode type, byte[] sampleBytes, int byteFrameOffset,
            double[,] sampleValues, int valueFrameOffset, int frameCount, int bitsPerSample, int channelCount, bool isFloat)
        {
            SampleFormat format = SampleFormat.Create(bitsPerSample, channelCount, isFloat);
            ValidateArguments(sampleBytes, sampleValues, byteFrameOffset, valueFrameOffset, frameCount, format);

            int valueCount = checked(frameCount * channelCount);
            int byteOffset = checked(byteFrameOffset * format.FrameBytes);
            int valueOffset = checked(valueFrameOffset * channelCount);

            fixed (byte* byteBase = sampleBytes)
            fixed (double* valueBase = sampleValues) {
                byte* bytes = byteBase + byteOffset;
                double* values = valueBase + valueOffset;

                if (type == SampleTransferMode.DecodeFromBytes) {
                    Decode(bytes, values, valueCount, format);
                }
                else {
                    Encode(bytes, values, valueCount, format);
                }
            }
        }

        public static void MonoToStereoInPlace(byte[] buffer, int frameCount) {
            MonoToStereoInPlace(buffer, frameCount, 2);
        }

        public static void MonoToStereoInPlace(byte[] buffer, int frameCount, int bytesPerSample) {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (frameCount < 0) throw new ArgumentOutOfRangeException("frameCount");
            if (bytesPerSample <= 0) throw new ArgumentOutOfRangeException("bytesPerSample");

            for (int frame = frameCount - 1; frame >= 0; frame--) {
                int source = frame * bytesPerSample;
                int target = source * 2;
                Buffer.BlockCopy(buffer, source, buffer, target, bytesPerSample);
                Buffer.BlockCopy(buffer, source, buffer, target + bytesPerSample, bytesPerSample);
            }
        }

        private static void ValidateArguments(byte[] sampleBytes, double[,] sampleValues, int byteFrameOffset,
            int valueFrameOffset, int frameCount, SampleFormat format)
        {
            if (sampleBytes == null) throw new ArgumentNullException("sampleBytes");
            if (sampleValues == null) throw new ArgumentNullException("sampleValues");
            if (byteFrameOffset < 0) throw new ArgumentOutOfRangeException("byteFrameOffset");
            if (valueFrameOffset < 0) throw new ArgumentOutOfRangeException("valueFrameOffset");
            if (frameCount < 0) throw new ArgumentOutOfRangeException("frameCount");

            int byteOffset = checked(byteFrameOffset * format.FrameBytes);
            int bytesNeeded = checked(frameCount * format.FrameBytes);
            if (byteOffset + bytesNeeded > sampleBytes.Length) throw new ArgumentException("Byte buffer is too small for the requested sample range.", "sampleBytes");

            int valuesNeeded = checked((valueFrameOffset + frameCount) * format.Tracks);
            if (valuesNeeded > sampleValues.Length) throw new ArgumentException("Double buffer is too small for the requested sample range.", "sampleValues");
        }

        private static unsafe void Decode(byte* source, double* target, int valueCount, SampleFormat format) {
            if (format.UsesFloatingPoint) {
                if (format.BitDepth == 64) {
                    double* d = (double*)source;
                    for (int i = 0; i < valueCount; i++) target[i] = d[i];
                }
                else {
                    float* f = (float*)source;
                    for (int i = 0; i < valueCount; i++) target[i] = f[i];
                }
                return;
            }

            switch (format.BitDepth) {
                case 8:
                    for (int i = 0; i < valueCount; i++) target[i] = ((int)source[i] - 128) * (1.0 / 128.0);
                    return;
                case 16:
                    short* s16 = (short*)source;
                    for (int i = 0; i < valueCount; i++) target[i] = s16[i] * (1.0 / 32768.0);
                    return;
                case 24:
                    for (int i = 0; i < valueCount; i++) {
                        int p = i * 3;
                        int sample = source[p] | (source[p + 1] << 8) | (source[p + 2] << 16);
                        if ((sample & 0x00800000) != 0) sample |= unchecked((int)0xFF000000);
                        target[i] = sample * (1.0 / 8388608.0);
                    }
                    return;
                case 32:
                    int* s32 = (int*)source;
                    for (int i = 0; i < valueCount; i++) target[i] = s32[i] * (1.0 / 2147483648.0);
                    return;
            }

            throw new Exception("Unsupported PCM bit depth.");
        }

        private static unsafe void Encode(byte* target, double* source, int valueCount, SampleFormat format) {
            if (format.UsesFloatingPoint) {
                if (format.BitDepth == 64) {
                    double* d = (double*)target;
                    for (int i = 0; i < valueCount; i++) d[i] = Clean(source[i]);
                }
                else {
                    float* f = (float*)target;
                    for (int i = 0; i < valueCount; i++) f[i] = (float)Clean(source[i]);
                }
                return;
            }

            switch (format.BitDepth) {
                case 8:
                    for (int i = 0; i < valueCount; i++) target[i] = (byte)(ToInt(Clean(source[i]) * 128.0, -128, 127) + 128);
                    return;
                case 16:
                    short* s16 = (short*)target;
                    for (int i = 0; i < valueCount; i++) s16[i] = (short)ToInt(Clean(source[i]) * 32768.0, -32768, 32767);
                    return;
                case 24:
                    for (int i = 0; i < valueCount; i++) {
                        int sample = ToInt(Clean(source[i]) * 8388608.0, -8388608, 8388607);
                        int p = i * 3;
                        target[p] = (byte)(sample & 0xFF);
                        target[p + 1] = (byte)((sample >> 8) & 0xFF);
                        target[p + 2] = (byte)((sample >> 16) & 0xFF);
                    }
                    return;
                case 32:
                    int* s32 = (int*)target;
                    for (int i = 0; i < valueCount; i++) s32[i] = ToInt(Clean(source[i]) * 2147483648.0, Int32.MinValue, Int32.MaxValue);
                    return;
            }

            throw new Exception("Unsupported PCM bit depth.");
        }

        private static double Clean(double value) {
            return Double.IsNaN(value) ? 0.0 : value;
        }

        private static int ToInt(double value, int min, int max) {
            if (value > max) return max;
            if (value < min) return min;
            return (int)(value >= 0.0 ? value + 0.5 : value - 0.5);
        }

        private struct SampleFormat {
            public readonly int BitDepth;
            public readonly int Tracks;
            public readonly bool UsesFloatingPoint;
            public readonly int BytesPerValue;
            public readonly int FrameBytes;

            private SampleFormat(int bitsPerSample, int channelCount, bool isFloat) {
                BitDepth = bitsPerSample;
                Tracks = channelCount;
                UsesFloatingPoint = isFloat;
                BytesPerValue = bitsPerSample / 8;
                FrameBytes = BytesPerValue * channelCount;
            }

            public static SampleFormat Create(int bitsPerSample, int channelCount, bool isFloat) {
                if (channelCount <= 0) throw new ArgumentOutOfRangeException("channelCount");
                if ((bitsPerSample & 7) != 0) throw new ArgumentOutOfRangeException("bitsPerSample");

                if (isFloat) {
                    if (bitsPerSample != 32 && bitsPerSample != 64) throw new Exception("Only 32-bit and 64-bit IEEE float WAV samples are supported.");
                }
                else {
                    if (bitsPerSample != 8 && bitsPerSample != 16 && bitsPerSample != 24 && bitsPerSample != 32) throw new Exception("Only 8-, 16-, 24- and 32-bit PCM WAV samples are supported.");
                }

                return new SampleFormat(bitsPerSample, channelCount, isFloat);
            }
        }
    }
}
