using System;
using System.IO;

namespace FFTTools {
    public interface IWaveFrameSource {
        int Read(byte[] buffer, int sampleCount);
        long TotalFrames { get; }
        long FramePosition { get; set; }
        long FramesAvailable { get; }
        void Finish();
        int SampleBits { get; }
        int Channels { get; }
        int Rate { get; }
        bool FloatingPoint { get; }
    }

    public interface IWaveFrameSink {
        void Write(byte[] buffer, int sampleCount);
        void Finish();
        long ExpectedFrameCount { set; }
    }

    public static class WaveReadWrite {

        public static IWaveFrameSource OpenInput(string path) {
            if (String.Equals(path, "stdin", StringComparison.OrdinalIgnoreCase)) {
                return new WaveFileReader(Console.OpenStandardInput());
            }
            if (!String.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)) {
                throw new Exception("Only WAV input is supported in this build.");
            }
            return new WaveFileReader(path);
        }

        public static IWaveFrameSink CreateOutput(string path, int bitsPerSample, int channelCount, int sampleRate, long finalSampleCount) {
            return CreateOutput(path, bitsPerSample, channelCount, sampleRate, finalSampleCount, false);
        }

        public static IWaveFrameSink CreateOutput(string path, int bitsPerSample, int channelCount, int sampleRate, long finalSampleCount, bool isFloat) {
            WaveFileWriter writer;
            if (String.Equals(path, "stdout", StringComparison.OrdinalIgnoreCase)) {
                writer = new WaveFileWriter(Console.OpenStandardOutput(), bitsPerSample, channelCount, sampleRate, isFloat);
            }
            else {
                if (!String.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)) {
                    throw new Exception("Only WAV output is supported in this build.");
                }
                writer = new WaveFileWriter(path, bitsPerSample, channelCount, sampleRate, isFloat);
            }
            writer.ExpectedFrameCount = finalSampleCount;
            return writer;
        }
    }

    public sealed class WaveFileReader : IWaveFrameSource {
        private const ushort WaveFormatPcm = 0x0001;
        private const ushort WaveFormatIeeeFloat = 0x0003;
        private const ushort WaveFormatExtensible = 0xFFFE;

        private readonly BinaryReader _reader;
        private readonly bool _seekable;
        private readonly long _streamLength;
        private long _audioPayloadOffset;
        private long _audioPayloadBytes;
        private long _cursorFrames;
        private long _frameTotal;
        private int _bits;
        private int _channels;
        private int _rate;
        private int _frameBytes;
        private bool _isFloat;
        private bool _closed;

        public WaveFileReader(string path)
            : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
        }

        public WaveFileReader(Stream stream) {
            if (stream == null) throw new ArgumentNullException("stream");
            _reader = new BinaryReader(stream);
            _seekable = stream.CanSeek;
            _streamLength = _seekable ? stream.Length : -1L;
            LoadWaveHeader();
        }

        public int SampleBits { get { return _bits; } }
        public int Channels { get { return _channels; } }
        public int Rate { get { return _rate; } }
        public bool FloatingPoint { get { return _isFloat; } }
        public long TotalFrames { get { return _frameTotal; } }
        public long FramesAvailable { get { return _frameTotal - _cursorFrames; } }

        public long FramePosition {
            get { return _cursorFrames; }
            set {
                if (!_seekable) throw new Exception("Input stream does not support seeking.");
                if (value < 0) value = 0;
                if (value > _frameTotal) value = _frameTotal;
                _cursorFrames = value;
                _reader.BaseStream.Seek(_audioPayloadOffset + (_cursorFrames * _frameBytes), SeekOrigin.Begin);
            }
        }

        public void Finish() {
            if (_closed) return;
            _closed = true;
            _reader.Close();
        }

        public int Read(byte[] buffer, int sampleCount) {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (sampleCount <= 0) return 0;
            if (sampleCount > FramesAvailable) sampleCount = (int)FramesAvailable;

            int bytesRequested = checked(sampleCount * _frameBytes);
            if (bytesRequested > buffer.Length) throw new ArgumentException("Destination buffer is too small.", "buffer");

            int bytesRead = ReadExactly(buffer, 0, bytesRequested);
            int completeSamples = bytesRead / _frameBytes;
            _cursorFrames += completeSamples;
            return completeSamples;
        }

        private void LoadWaveHeader() {
            string containerId = ReadFourCC();
            bool isRf64 = false;
            if (containerId == "RF64") {
                isRf64 = true;
            }
            else if (containerId != "RIFF") {
                throw new Exception("Not a RIFF/RF64 file.");
            }

            uint riffSize = ReadUInt32LE();
            if (ReadFourCC() != "WAVE") throw new Exception("Not a WAVE file.");

            bool haveFormat = false;
            bool haveData = false;
            bool haveDs64 = false;
            ulong ds64DataBytes = 0UL;
            long riffEnd = (!isRf64 && _seekable && riffSize != UInt32.MaxValue) ? Math.Min(_streamLength, 8L + riffSize) : Int64.MaxValue;

            while (!haveData) {
                if (_seekable && _reader.BaseStream.Position >= riffEnd) break;

                string chunkId = ReadFourCC();
                uint chunkSize32 = ReadUInt32LE();
                long chunkStart = _reader.BaseStream.Position;
                long chunkSize = chunkSize32;

                if (chunkId == "ds64") {
                    ulong ignoredRiffSize;
                    ulong ignoredSampleCount;
                    ReadDs64Chunk(chunkSize, out ignoredRiffSize, out ds64DataBytes, out ignoredSampleCount);
                    haveDs64 = true;
                }
                else if (chunkId == "fmt ") {
                    ReadFormatChunk(chunkSize);
                    haveFormat = true;
                }
                else if (chunkId == "data") {
                    if (!haveFormat) throw new Exception("WAVE data chunk appears before format chunk.");
                    _audioPayloadOffset = _reader.BaseStream.Position;
                    if (isRf64 && chunkSize32 == UInt32.MaxValue) {
                        if (!haveDs64) throw new Exception("RF64 data chunk requires a preceding ds64 chunk.");
                        if (ds64DataBytes > Int64.MaxValue) throw new Exception("RF64 data chunk is too large for this build.");
                        _audioPayloadBytes = (long)ds64DataBytes;
                    }
                    else {
                        _audioPayloadBytes = ResolvePayloadBytes(chunkSize);
                    }
                    _frameTotal = _audioPayloadBytes / _frameBytes;
                    haveData = true;
                }

                if (!haveData) {
                    AdvancePastChunk(chunkStart, chunkSize);
                }
            }

            if (!haveFormat) throw new Exception("WAVE format chunk is missing.");
            if (!haveData) throw new Exception("WAVE data chunk is missing.");
            ValidateWaveFormat();
            _cursorFrames = 0;
            if (_seekable) {
                _reader.BaseStream.Seek(_audioPayloadOffset, SeekOrigin.Begin);
            }
        }

        private void ReadDs64Chunk(long chunkSize, out ulong riffSize64, out ulong dataSize64, out ulong sampleCount64) {
            if (chunkSize < 28) throw new Exception("RF64 ds64 chunk is too short.");
            riffSize64 = ReadUInt64LE();
            dataSize64 = ReadUInt64LE();
            sampleCount64 = ReadUInt64LE();
            ReadUInt32LE(); // table length, ignored; AdvancePastChunk skips optional table entries.
        }

        private void ReadFormatChunk(long chunkSize) {
            if (chunkSize < 16) throw new Exception("WAVE format chunk is too short.");

            ushort formatTag = ReadUInt16LE();
            _channels = ReadUInt16LE();
            _rate = (int)ReadUInt32LE();
            ReadUInt32LE();
            _frameBytes = ReadUInt16LE();
            _bits = ReadUInt16LE();

            if (formatTag == WaveFormatExtensible) {
                if (chunkSize < 40) throw new Exception("WAVE extensible format chunk is too short.");
                ushort extraSize = ReadUInt16LE();
                if (extraSize < 22) throw new Exception("WAVE extensible extra format data is too short.");
                ReadUInt16LE(); // valid bits per sample
                ReadUInt32LE(); // channel mask
                ushort subFormat = ReadUInt16LE();
                ushort guidA = ReadUInt16LE();
                uint guidB = ReadUInt32LE();
                uint guidC = ReadUInt32LE();
                uint guidD = ReadUInt32LE();
                if (guidA != 0x0000 || guidB != 0x00100000 || guidC != 0xAA000080 || guidD != 0x719B3800) {
                    throw new Exception("Unsupported WAVE extensible sub-format GUID.");
                }
                formatTag = subFormat;
            }

            if (formatTag == WaveFormatPcm) {
                _isFloat = false;
            }
            else if (formatTag == WaveFormatIeeeFloat) {
                _isFloat = true;
            }
            else {
                throw new Exception("Only PCM and IEEE float WAV input are supported.");
            }
        }

        private long ResolvePayloadBytes(long chunkSize) {
            if (!_seekable) return chunkSize;
            long remaining = _streamLength - _reader.BaseStream.Position;
            if (chunkSize == UInt32.MaxValue || chunkSize > remaining) return remaining;
            return chunkSize;
        }

        private void ValidateWaveFormat() {
            if (_channels <= 0) throw new Exception("WAV channel count is invalid.");
            if (_rate <= 0) throw new Exception("WAV sample rate is invalid.");
            if (_bits <= 0 || (_bits & 7) != 0) throw new Exception("WAV bits per sample is invalid.");
            if (_frameBytes != _channels * (_bits / 8)) throw new Exception("WAV block alignment is invalid.");

            if (_isFloat) {
                if (_bits != 32 && _bits != 64) {
                    throw new Exception("Only 32-bit and 64-bit IEEE float WAV input are supported.");
                }
            }
            else if (_bits != 8 && _bits != 16 && _bits != 24 && _bits != 32) {
                throw new Exception("Only 8-, 16-, 24- and 32-bit PCM WAV input are supported.");
            }
        }

        private void AdvancePastChunk(long chunkStart, long chunkSize) {
            long paddedSize = chunkSize + (chunkSize & 1L);
            long target = chunkStart + paddedSize;
            long skip = target - _reader.BaseStream.Position;
            if (skip < 0) throw new Exception("Invalid WAV chunk layout.");
            DiscardBytes(skip);
        }

        private int ReadExactly(byte[] buffer, int offset, int count) {
            int done = 0;
            while (done < count) {
                int n = _reader.Read(buffer, offset + done, count - done);
                if (n <= 0) break;
                done += n;
            }
            return done;
        }

        private void DiscardBytes(long count) {
            if (count <= 0) return;
            if (_seekable) {
                _reader.BaseStream.Seek(count, SeekOrigin.Current);
                return;
            }

            byte[] temp = new byte[Math.Min(65536, (int)Math.Min(Int32.MaxValue, count))];
            while (count > 0) {
                int chunk = (int)Math.Min(temp.Length, count);
                int n = _reader.Read(temp, 0, chunk);
                if (n <= 0) throw new EndOfStreamException();
                count -= n;
            }
        }

        private string ReadFourCC() {
            byte[] id = _reader.ReadBytes(4);
            if (id.Length != 4) throw new EndOfStreamException();
            return new string(new char[] { (char)id[0], (char)id[1], (char)id[2], (char)id[3] });
        }

        private ushort ReadUInt16LE() {
            byte[] b = _reader.ReadBytes(2);
            if (b.Length != 2) throw new EndOfStreamException();
            return (ushort)(b[0] | (b[1] << 8));
        }

        private uint ReadUInt32LE() {
            byte[] b = _reader.ReadBytes(4);
            if (b.Length != 4) throw new EndOfStreamException();
            return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        }

        private ulong ReadUInt64LE() {
            byte[] b = _reader.ReadBytes(8);
            if (b.Length != 8) throw new EndOfStreamException();
            uint lo = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
            uint hi = (uint)(b[4] | (b[5] << 8) | (b[6] << 16) | (b[7] << 24));
            return ((ulong)hi << 32) | lo;
        }
    }

    public sealed class WaveFileWriter : IWaveFrameSink {
        private const ushort WaveFormatPcm = 0x0001;
        private const ushort WaveFormatIeeeFloat = 0x0003;
        private const int Rf64HeaderBytes = 80;
        private const int Ds64RiffSizeOffset = 20;
        private const int Ds64DataSizeOffset = 28;
        private const int Ds64SampleCountOffset = 36;

        private readonly BinaryWriter _writer;
        private readonly bool _seekable;
        private readonly int _bits;
        private readonly int _channels;
        private readonly int _rate;
        private readonly int _frameBytes;
        private readonly bool _isFloat;
        private bool _headerWritten;
        private bool _closed;
        private long _plannedFrames;
        private long _framesWritten;

        public WaveFileWriter(string path, int bitsPerSample, int channelCount, int sampleRate)
            : this(path, bitsPerSample, channelCount, sampleRate, false)
        {
        }

        public WaveFileWriter(string path, int bitsPerSample, int channelCount, int sampleRate, bool isFloat)
            : this(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), bitsPerSample, channelCount, sampleRate, isFloat)
        {
        }

        public WaveFileWriter(Stream stream, int bitsPerSample, int channelCount, int sampleRate)
            : this(stream, bitsPerSample, channelCount, sampleRate, false)
        {
        }

        public WaveFileWriter(Stream stream, int bitsPerSample, int channelCount, int sampleRate, bool isFloat) {
            if (stream == null) throw new ArgumentNullException("stream");
            if (channelCount <= 0) throw new ArgumentOutOfRangeException("channelCount");
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException("sampleRate");

            if (isFloat) {
                if (bitsPerSample != 32 && bitsPerSample != 64) bitsPerSample = 32;
            }
            else if (bitsPerSample != 8 && bitsPerSample != 16 && bitsPerSample != 24 && bitsPerSample != 32) {
                throw new Exception("Only 8-, 16-, 24- and 32-bit PCM WAV output are supported.");
            }

            _writer = new BinaryWriter(stream);
            _seekable = stream.CanSeek;
            _bits = bitsPerSample;
            _channels = channelCount;
            _rate = sampleRate;
            _isFloat = isFloat;
            _frameBytes = checked(channelCount * (bitsPerSample / 8));
        }

        public long ExpectedFrameCount {
            set { _plannedFrames = Math.Max(0L, value); }
        }

        public void Write(byte[] buffer, int sampleCount) {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (sampleCount <= 0) return;
            if (!_headerWritten) EmitHeader(_plannedFrames);

            int bytes = checked(sampleCount * _frameBytes);
            if (bytes > buffer.Length) throw new ArgumentException("Source buffer is too small.", "buffer");
            _writer.Write(buffer, 0, bytes);
            _framesWritten += sampleCount;
        }

        public void Finish() {
            if (_closed) return;
            _closed = true;

            try {
                if (!_headerWritten) EmitHeader(_framesWritten);

                long dataBytes = checked(_framesWritten * (long)_frameBytes);
                if ((dataBytes & 1L) != 0) _writer.Write((byte)0);

                if (_seekable) {
                    PatchSizes(_framesWritten);
                }
                else if (_plannedFrames != 0 && _plannedFrames != _framesWritten) {
                    throw new Exception("Samples written differs from the expected sample count.");
                }
            }
            finally {
                _writer.Close();
            }
        }

        private void EmitHeader(long sampleCount) {
            _headerWritten = true;
            ulong dataBytes = DataByteCount64(sampleCount);
            ulong riffSize = RiffSize64(dataBytes);
            ushort format = (ushort)(_isFloat ? WaveFormatIeeeFloat : WaveFormatPcm);

            WriteTextToken("RF64");
            WriteUInt32LE(UInt32.MaxValue);
            WriteTextToken("WAVE");

            WriteTextToken("ds64");
            WriteUInt32LE(28);
            WriteUInt64LE(riffSize);
            WriteUInt64LE(dataBytes);
            WriteUInt64LE((ulong)Math.Max(0L, sampleCount));
            WriteUInt32LE(0); // no additional 64-bit chunk-size table entries

            WriteTextToken("fmt ");
            WriteUInt32LE(16);
            WriteUInt16LE(format);
            WriteUInt16LE((ushort)_channels);
            WriteUInt32LE((uint)_rate);
            WriteUInt32LE((uint)(_rate * _frameBytes));
            WriteUInt16LE((ushort)_frameBytes);
            WriteUInt16LE((ushort)_bits);

            WriteTextToken("data");
            WriteUInt32LE(UInt32.MaxValue);
        }

        private void PatchSizes(long sampleCount) {
            ulong dataBytes = DataByteCount64(sampleCount);
            ulong riffSize = RiffSize64(dataBytes);
            long current = _writer.BaseStream.Position;
            _writer.BaseStream.Seek(Ds64RiffSizeOffset, SeekOrigin.Begin);
            WriteUInt64LE(riffSize);
            _writer.BaseStream.Seek(Ds64DataSizeOffset, SeekOrigin.Begin);
            WriteUInt64LE(dataBytes);
            _writer.BaseStream.Seek(Ds64SampleCountOffset, SeekOrigin.Begin);
            WriteUInt64LE((ulong)Math.Max(0L, sampleCount));
            _writer.BaseStream.Seek(current, SeekOrigin.Begin);
        }

        private ulong DataByteCount64(long sampleCount) {
            long frames = Math.Max(0L, sampleCount);
            return (ulong)checked(frames * (long)_frameBytes);
        }

        private ulong RiffSize64(ulong dataBytes) {
            return (ulong)(Rf64HeaderBytes - 8) + dataBytes + (dataBytes & 1UL);
        }

        private void WriteTextToken(string value) {
            for (int i = 0; i < value.Length; i++) _writer.Write((byte)value[i]);
        }

        private void WriteUInt16LE(ushort value) {
            _writer.Write((byte)(value & 0xFF));
            _writer.Write((byte)((value >> 8) & 0xFF));
        }

        private void WriteUInt32LE(uint value) {
            _writer.Write((byte)(value & 0xFF));
            _writer.Write((byte)((value >> 8) & 0xFF));
            _writer.Write((byte)((value >> 16) & 0xFF));
            _writer.Write((byte)((value >> 24) & 0xFF));
        }

        private void WriteUInt64LE(ulong value) {
            _writer.Write((byte)(value & 0xFF));
            _writer.Write((byte)((value >> 8) & 0xFF));
            _writer.Write((byte)((value >> 16) & 0xFF));
            _writer.Write((byte)((value >> 24) & 0xFF));
            _writer.Write((byte)((value >> 32) & 0xFF));
            _writer.Write((byte)((value >> 40) & 0xFF));
            _writer.Write((byte)((value >> 48) & 0xFF));
            _writer.Write((byte)((value >> 56) & 0xFF));
        }
    }
}
