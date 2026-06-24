using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace FFTTools {
    internal sealed class Program {
        private const string Version = "fft_tools r133-upmix-db-gains";
        private static bool _overwriteAllowed;
        private static bool _consoleMuted;
        private static string _progressName;
        private static int _progressValue = -1;

        private enum RunMode {
            None,
            Enhance,
            Upmix,
            UpmixSpectrum,
            Dereverb
        }

        private sealed class CommandLine {
            public string InputPath;
            public string OutputPath;
            public RunMode Mode = RunMode.None;
            public int EnhanceRate;
            public double? NormalizeDb;
            public double OutputScale = 1.0;
            public int Workers;
            public readonly LCRWideOptions Upmix = new LCRWideOptions();
            public readonly DereverbOptions Dereverb = new DereverbOptions();
        }

        private sealed class ArgumentCursor {
            private readonly string[] _items;
            private int _index;

            public ArgumentCursor(string[] items) {
                _items = items ?? new string[0];
            }

            public bool HasMore { get { return _index < _items.Length; } }
            public int Consumed { get { return _index; } }

            public string Take() {
                if (_index >= _items.Length) throw new ArgumentException("Missing argument.");
                return _items[_index++];
            }
        }

        private static int Main(string[] args) {
            ApplyInvariantCulture();

            CommandLine command;
            if (!TryReadCommandLine(args, out command)) {
                PrintBanner();
                PrintHelp();
                return 1;
            }

            if (!_consoleMuted) PrintBanner();
            if (!ConfirmOutput(command.OutputPath)) return 0;
            if (command.Workers > 0) WorkerPool.ThreadCount = command.Workers;

            Stopwatch timer = Stopwatch.StartNew();
            try {
                Execute(command);
                timer.Stop();
                if (!_consoleMuted) Console.WriteLine("\rFinished in {0:0.000} seconds.", timer.Elapsed.TotalSeconds);
                return 0;
            }
            catch (Exception ex) {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 1;
            }
        }

        private static void ApplyInvariantCulture() {
            CultureInfo invariant = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = invariant;
            Thread.CurrentThread.CurrentUICulture = invariant;
        }

        private static bool TryReadCommandLine(string[] args, out CommandLine command) {
            command = null;
            try {
                ArgumentCursor cursor = new ArgumentCursor(args);
                if (!cursor.HasMore) return false;

                CommandLine parsed = new CommandLine();
                parsed.InputPath = cursor.Take();
                if (LooksLikeOption(parsed.InputPath) && !File.Exists(parsed.InputPath)) return false;
                if (IsConsolePath(parsed.InputPath)) _consoleMuted = true;

                while (cursor.HasMore) {
                    ReadSwitch(cursor, parsed);
                }

                if (parsed.Mode == RunMode.None || String.IsNullOrEmpty(parsed.OutputPath)) return false;
                command = parsed;
                return true;
            }
            catch {
                return false;
            }
        }

        private static void ReadSwitch(ArgumentCursor cursor, CommandLine command) {
            string token = cursor.Take();
            if (token == "--enhance") {
                SelectMode(command, RunMode.Enhance, cursor.Take());
            }
            else if (token == "--upmix") {
                SelectMode(command, RunMode.Upmix, cursor.Take());
            }
            else if (token == "--upmix-spectrum") {
                SelectMode(command, RunMode.UpmixSpectrum, cursor.Take());
            }
            else if (token == "--dereverb") {
                SelectMode(command, RunMode.Dereverb, cursor.Take());
            }
            else if (token == "-a") {
                command.OutputScale = ReadNumber(cursor) / 100.0;
            }
            else if (token == "--rate" || token == "-r") {
                command.EnhanceRate = ReadInt(cursor);
            }
            else if (token == "-n") {
                command.NormalizeDb = ReadNumber(cursor);
            }
            else if (token == "--nocetergain" || token == "--nocentergain" || token == "--noncentergain" || token == "--noncenter-gain" || token == "--no-center-gain") {
                command.Upmix.NonCenterOutputGainDb = ReadNumber(cursor);
            }
            else if (token == "--centergain") {
                command.Upmix.CenterOutputGainDb = ReadNumber(cursor);
            }
            else if (token == "--center-gain") {
                command.Upmix.CenterGain = ReadNumber(cursor) / 100.0;
            }
            else if (token == "--wide-gain") {
                command.Upmix.WideGain = ReadNumber(cursor) / 100.0;
            }
            else if (token == "--wide-exp") {
                command.Upmix.WideExponent = ReadNumber(cursor);
            }
            else if (token == "--wide-lowcut") {
                command.Upmix.WideLowCutHz = ReadNumber(cursor);
            }
            else if (token == "--wide-smooth") {
                command.Upmix.WideSmoothMs = ReadNumber(cursor);
            }
            else if (token == "--wide-phase") {
                command.Upmix.WidePhaseWeight = ReadNumber(cursor);
            }
            else if (token == "--pan-sharpness") {
                command.Upmix.LCR7PanSharpness = ReadNumber(cursor);
            }
            else if (token == "--clcr-pos") {
                command.Upmix.LCR7CLCRPosition = ReadNumber(cursor);
            }
            else if (token == "--dereverb-lowcut") {
                command.Dereverb.LowCutHz = ReadNumber(cursor);
            }
            else if (token == "--dereverb-early-start") {
                command.Dereverb.EarlyStartMs = ReadNumber(cursor);
            }
            else if (token == "--dereverb-early-full") {
                command.Dereverb.EarlyFullMs = ReadNumber(cursor);
            }
            else if (token == "--dereverb-late-start") {
                command.Dereverb.LateStartMs = ReadNumber(cursor);
            }
            else if (token == "--dereverb-max-ms") {
                command.Dereverb.MaxTailMs = ReadNumber(cursor);
            }
            else if (token == "--dereverb-strength") {
                command.Dereverb.Strength = ReadNumber(cursor) / 100.0;
            }
            else if (token == "--dereverb-tonal-protect") {
                command.Dereverb.TonalProtect = ReadRatio(cursor);
            }
            else if (token == "-o") {
                _overwriteAllowed = true;
            }
            else if (token == "-t") {
                command.Workers = ReadInt(cursor);
                if (command.Workers < 1 || command.Workers > 32) throw new ArgumentOutOfRangeException("-t");
            }
            else {
                throw new ArgumentException("Unknown option: " + token);
            }
        }

        private static void SelectMode(CommandLine command, RunMode mode, string outputPath) {
            if (command.Mode != RunMode.None) throw new ArgumentException("Only one mode may be selected.");
            command.Mode = mode;
            command.OutputPath = outputPath;
            if (IsConsolePath(outputPath)) _consoleMuted = true;
        }

        private static void Execute(CommandLine command) {
            switch (command.Mode) {
                case RunMode.Enhance:
                    FFTToolsHelper.RunEnhance(command.InputPath, command.OutputPath, command.EnhanceRate, command.NormalizeDb, ShowProgress);
                    break;
                case RunMode.Upmix:
                    FFTToolsHelper.RunUpmix(command.InputPath, command.OutputPath, command.OutputScale, command.Upmix, command.Dereverb, ShowProgress);
                    break;
                case RunMode.UpmixSpectrum:
                    FFTToolsHelper.RunUpmixSpectrum(command.InputPath, command.OutputPath, command.OutputScale, command.Upmix, ShowProgress);
                    break;
                case RunMode.Dereverb:
                    FFTToolsHelper.RunDereverb(command.InputPath, command.OutputPath, command.Dereverb, ShowProgress);
                    break;
                default:
                    throw new InvalidOperationException("No processing mode selected.");
            }
        }

        private static double ReadNumber(ArgumentCursor cursor) {
            string text = cursor.Take().Replace(',', '.');
            return Double.Parse(text, CultureInfo.InvariantCulture);
        }

        private static int ReadInt(ArgumentCursor cursor) {
            return Int32.Parse(cursor.Take(), CultureInfo.InvariantCulture);
        }

        private static double ReadRatio(ArgumentCursor cursor) {
            double value = ReadNumber(cursor);
            return value > 1.0 ? value / 100.0 : value;
        }

        private static bool LooksLikeOption(string value) {
            return !String.IsNullOrEmpty(value) && value[0] == '-';
        }

        private static bool IsConsolePath(string path) {
            return String.Equals(path, "stdin", StringComparison.OrdinalIgnoreCase) || String.Equals(path, "stdout", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ConfirmOutput(string outputPath) {
            if (String.IsNullOrEmpty(outputPath)) return false;
            if (IsConsolePath(outputPath)) return true;
            if (!File.Exists(outputPath)) return true;
            if (_overwriteAllowed) return true;
            if (_consoleMuted) return false;

            Console.Write("Overwrite '{0}'? [y/N] ", outputPath);
            ConsoleKeyInfo key = Console.ReadKey();
            Console.WriteLine();
            return key.KeyChar == 'y' || key.KeyChar == 'Y';
        }

        private static void PrintBanner() {
            Console.WriteLine(Version);
            Console.WriteLine("Author: chackl");
            Console.WriteLine("Independent phase-aware FFT audio tool by chackl.");
            Console.WriteLine("Historical inspiration only: the general Center Cut concept by Avery Lee.");
            Console.WriteLine("Internal WAV I/O by chackl; FFT backend retained separately.");
            Console.WriteLine();
        }

        private static void PrintHelp() {
            Console.WriteLine("Usage: fft_tools <input.wav|stdin> <mode> <output.wav|stdout> [options]");
            Console.WriteLine();
            Console.WriteLine("Modes:");
            Console.WriteLine("  --enhance <file>          FFT harmonic high-frequency enhancer.");
            Console.WriteLine("  --upmix <file>            Dereverb based AVR 7.1 upmix.");
            Console.WriteLine("  --upmix-spectrum <file>   Spectral AVR 7.1 upmix.");
            Console.WriteLine("  --dereverb <file>         Six-channel dry/near/far dereverb analysis output.");
            Console.WriteLine();
            Console.WriteLine("Common options: -o, -t <1..32>, -a <percent>");
            Console.WriteLine("Enhance: --rate|-r <rate|factor>, -n <dBFS>");
            Console.WriteLine("Upmix output: --nocetergain <dB>, --centergain <dB>");
            Console.WriteLine("Upmix tuning: --center-gain, --wide-gain, --wide-exp, --wide-lowcut, --wide-phase, --pan-sharpness, --clcr-pos");
            Console.WriteLine("Dereverb: --dereverb-lowcut, --dereverb-early-start, --dereverb-early-full, --dereverb-late-start, --dereverb-max-ms, --dereverb-strength, --dereverb-tonal-protect");
            Console.WriteLine("WAV: PCM 8/16/24/32 and IEEE float 32/64 are supported.");
        }

        private static bool ShowProgress(string phase, double fraction) {
            if (_consoleMuted) return false;
            int percent = (int)Math.Round(Limit(fraction, 0.0, 1.0) * 100.0);
            if (phase == _progressName && percent == _progressValue) return false;
            if (_progressName != null && phase != _progressName) Console.WriteLine();
            _progressName = phase;
            _progressValue = percent;
            Console.Write("\r{0}: {1}% ", phase, percent);
            if (percent >= 100) Console.WriteLine();
            return false;
        }

        private static double Limit(double value, double min, double max) {
            return value < min ? min : (value > max ? max : value);
        }
    }
}
