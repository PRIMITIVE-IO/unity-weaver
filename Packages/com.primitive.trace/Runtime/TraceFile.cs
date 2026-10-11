// Copied from fody-dotnet-trace/src/Primitive.Trace/TraceFile.cs by tools/sync-unity.ps1; edit the original.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace Primitive.Trace
{
    /// <summary>
    /// Appends lines to the trace file from a background thread, flushed at least every 100 ms, so a viewer tailing
    /// the file (the proxy re-reads it as it grows) sees the process run live.
    /// </summary>
    internal sealed class TraceFile : IDisposable
    {
        readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly StreamWriter writer;
        readonly Thread thread;
        volatile bool closing;

        public string Path { get; }

        public TraceFile(string path)
        {
            Path = path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            // shared for reading: the proxy tails it while it is written
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16);
            thread = new Thread(Run) { IsBackground = true, Name = "primitive-trace writer" };
            thread.Start();
        }

        public void Write(string line)
        {
            queue.Enqueue(line);
            if (queue.Count > 4096) wake.Set();
        }

        void Run()
        {
            while (!closing)
            {
                wake.WaitOne(100);
                Drain();
            }
        }

        void Drain()
        {
            lock (writer)
            {
                bool any = false;
                while (queue.TryDequeue(out string line))
                {
                    writer.Write(line);
                    writer.Write('\n');
                    any = true;
                }
                if (any) writer.Flush();
            }
        }

        public void Dispose()
        {
            closing = true;
            wake.Set();
            if (thread.IsAlive && Thread.CurrentThread != thread) thread.Join(2000);
            Drain();
            lock (writer) writer.Dispose();
        }
    }
}
