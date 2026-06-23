using System;
using System.Threading;

namespace FFTTools {
    internal static class WorkerPool {
        private const int MaxConfiguredThreads = 32;
        private static int _threadCount = Math.Min(Math.Max(1, Environment.ProcessorCount), MaxConfiguredThreads);

        public static int ThreadCount {
            get { return _threadCount; }
            set { _threadCount = Math.Min(Math.Max(1, value), MaxConfiguredThreads); }
        }

        public static void For(int startInclusive, int endExclusive, int maxWorkers, Action<int> body) {
            if (body == null) throw new ArgumentNullException("body");
            if (endExclusive <= startInclusive) return;

            int itemCount = endExclusive - startInclusive;
            int workerCount = Math.Max(1, Math.Min(Math.Min(maxWorkers, ThreadCount), itemCount));
            if (workerCount == 1) {
                for (int i = startInclusive; i < endExclusive; i++) body(i);
                return;
            }

            Exception firstError = null;
            object errorLock = new object();
            int next = startInclusive - 1;
            int remaining = workerCount;
            using (ManualResetEventSlim completed = new ManualResetEventSlim(false)) {
                for (int worker = 0; worker < workerCount; worker++) {
                    ThreadPool.QueueUserWorkItem(delegate {
                        try {
                            while (firstError == null) {
                                int index = Interlocked.Increment(ref next);
                                if (index >= endExclusive) break;
                                body(index);
                            }
                        }
                        catch (Exception ex) {
                            lock (errorLock) {
                                if (firstError == null) firstError = ex;
                            }
                        }
                        finally {
                            if (Interlocked.Decrement(ref remaining) == 0) completed.Set();
                        }
                    });
                }
                completed.Wait();
            }

            if (firstError != null) throw new Exception("Parallel worker failed.", firstError);
        }
    }
}
