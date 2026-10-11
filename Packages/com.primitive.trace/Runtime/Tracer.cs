// Copied from fody-dotnet-trace/src/Primitive.Trace/Tracer.cs by tools/sync-unity.ps1; edit the original.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace Primitive.Trace
{
    /// <summary>
    /// The runtime woven code calls. Every instrumented method calls <see cref="Enter"/> on entry and <see cref="Exit"/>
    /// or <see cref="Return"/> when it returns, or <see cref="Raise"/> when an exception leaves it. The first call starts
    /// a trace (unless a host starts it itself) that ends when the process exits, after
    /// <see cref="TraceSettings.Seconds"/>, or after <see cref="TraceSettings.MaxEvents"/>.
    /// </summary>
    public static class Tracer
    {
        static readonly object startGate = new object();
        static TraceSession session;
        static TraceSettings configured;
        static bool started;

        [ThreadStatic] static bool inside;

        /// <summary>Start a trace at the first traced call (processes run by primitive-trace). Inside Unity it is off: code
        /// also runs in the editor, between play sessions, and the Unity package starts a trace when play begins.</summary>
        public static bool AutoStart =
#if UNITY_5_3_OR_NEWER
            false;
#else
            true;
#endif

        /// <summary>Settings for the next trace (otherwise they come from environment variables).</summary>
        public static void Configure(TraceSettings settings) => configured = settings;

        public static bool IsRecording => session != null && !session.Stopped;

        /// <summary>The file being (or last) written.</summary>
        public static string OutputPath { get; private set; }

        /// <summary>Raised when a trace ends, with its file.</summary>
        public static event Action<string> Finished;

        /// <summary>Begin a trace now; false when one is running already or it could not be opened.</summary>
        public static bool Start(TraceSettings settings = null)
        {
            lock (startGate)
            {
                if (session != null && !session.Stopped) return false;
                started = true;
                TraceSettings s = settings ?? configured ?? TraceSettings.FromEnvironment();
                s.Complete();
                try
                {
                    session = new TraceSession(s);
                    OutputPath = s.Output;
                    return true;
                }
                catch (Exception e)
                {
                    // never break the traced program
                    Console.Error.WriteLine("primitive-trace: cannot write " + s.Output + ": " + e.Message);
                    session = null;
                    return false;
                }
            }
        }

        /// <summary>End the trace: open frames return, an "end" event is written and the file closed.</summary>
        public static void Stop()
        {
            TraceSession s;
            lock (startGate)
            {
                s = session;
                if (s == null || s.Stopped) return;
            }
            s.Stop();
            Finished?.Invoke(s.Path);
        }

        /// <summary>Forget everything (a new play session in the Unity editor starts fresh).</summary>
        public static void Reset()
        {
            Stop();
            lock (startGate)
            {
                session = null;
                started = false;
            }
        }

        static TraceSession Session()
        {
            TraceSession s = session;
            if (s != null) return s.Stopped ? null : s;
            if (!AutoStart || started) return null;
            Start();
            return session;
        }

        // ------------------------------------------------------------------ called by woven code

        public static int Enter(string site, object self, object[] args)
        {
            if (inside) return 0;
            TraceSession s = Session();
            if (s == null) return 0;
            inside = true;
            try
            {
                return s.Enter(site, self, args);
            }
            catch (Exception)
            {
                return 0;
            }
            finally
            {
                inside = false;
            }
        }

        public static void Exit(int frame) => Leave(frame, false, null, null);

        public static void Return(int frame, object value) => Leave(frame, true, value, null);

        public static void Raise(int frame, Exception exception) => Leave(frame, false, null, exception);

        static void Leave(int frame, bool hasValue, object value, Exception exception)
        {
            if (frame == 0 || inside) return;
            TraceSession s = session;
            if (s == null) return;
            inside = true;
            try
            {
                s.Leave(frame, hasValue, value, exception);
            }
            catch (Exception)
            {
                // never break the traced program
            }
            finally
            {
                inside = false;
            }
        }

        internal static void Ended(TraceSession s)
        {
            lock (startGate)
                if (session == s)
                    OutputPath = s.Path;
        }
    }

    internal sealed class TraceSession
    {
        sealed class Entry
        {
            public int Frame;
            public bool Emitted, Suppressing;
        }

        sealed class ThreadState
        {
            public int Id;
            public readonly List<Entry> Stack = new List<Entry>();
            public int SuppressDepth;
        }

        sealed class ObjectId
        {
            public int Id;
        }

        sealed class ByReference : IEqualityComparer<string>
        {
            public bool Equals(string a, string b) => ReferenceEquals(a, b) || a == b;
            public int GetHashCode(string s) => RuntimeHelpers.GetHashCode(s);
        }

        readonly TraceSettings settings;
        readonly TraceFile file;
        readonly object gate = new object();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Dictionary<string, Site> sites = new Dictionary<string, Site>(new ByReference());
        readonly ConditionalWeakTable<object, ObjectId> ids = new ConditionalWeakTable<object, ObjectId>();
        readonly Dictionary<string, string> typeFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly List<ThreadState> threads = new List<ThreadState>();
        readonly Func<object, int> knownId;
        readonly StringBuilder line = new StringBuilder(512);
        int seq, nextFrame, nextObject, events;
        long calls;
        volatile bool stopped;

        [ThreadStatic] static ThreadState current;
        [ThreadStatic] static TraceSession currentOwner;

        public bool Stopped => stopped;
        public string Path => file.Path;

        public TraceSession(TraceSettings settings)
        {
            this.settings = settings;
            knownId = o => ids.TryGetValue(o, out ObjectId id) ? id.Id : 0;
            file = new TraceFile(settings.Output);
            WriteHeader();
            AppDomain.CurrentDomain.ProcessExit += (_, __) => Tracer.Stop();
        }

        void WriteHeader()
        {
            var sb = new StringBuilder(512);
            var process = Process.GetCurrentProcess();
            sb.Append("{\"type\":\"header\",\"format\":\"primitive-trace\",\"version\":1,\"traceId\":");
            Json.String(sb, Guid.NewGuid().ToString("N").Substring(0, 12));
            sb.Append(",\"workflow\":");
            Json.String(sb, settings.Workflow);
            sb.Append(",\"location\":{\"id\":");
            Json.String(sb, settings.LocationId);
            sb.Append(",\"name\":");
            Json.String(sb, settings.LocationName);
            sb.Append(",\"kind\":");
            Json.String(sb, settings.LocationKind);
            sb.Append(",\"host\":");
            Json.String(sb, Environment.MachineName);
            sb.Append("},\"root\":");
            Json.String(sb, settings.Root ?? "");
            sb.Append(",\"process\":{\"pid\":").Append(process.Id).Append(",\"name\":");
            Json.String(sb, process.ProcessName);
            sb.Append(",\"args\":");
            Json.String(sb, string.Join(" ", Environment.GetCommandLineArgs()), 400);
            sb.Append(",\"runtime\":");
            Json.String(sb, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            sb.Append(",\"tracer\":\"primitive-trace-dotnet/1.0\"}}");
            file.Write(sb.ToString());
        }

        string Relative(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string p = path.Replace('\\', '/');
            string root = settings.Root;
            if (!string.IsNullOrEmpty(root) && p.Length > root.Length + 1 && p.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return p.Substring(root.Length + 1);
            return p;
        }

        Site SiteOf(string text)
        {
            lock (sites)
            {
                if (sites.TryGetValue(text, out Site s)) return s;
                s = new Site(text, Relative);
                sites[text] = s;
                if (s.TypeName.Length > 0 && s.TypeFile.Length > 0 && !typeFiles.ContainsKey(s.TypeName)) typeFiles[s.TypeName] = s.TypeFile;
                return s;
            }
        }

        ThreadState Thread()
        {
            if (currentOwner == this && current != null) return current;
            var ts = new ThreadState { Id = Environment.CurrentManagedThreadId };
            current = ts;
            currentOwner = this;
            lock (gate)
            {
                threads.Add(ts);
                if (stopped) return ts;
                string name = System.Threading.Thread.CurrentThread.Name;
                if (string.IsNullOrEmpty(name)) name = ts.Id == 1 ? "main" : "thread-" + ts.Id;
                Begin("thread", ts.Id);
                line.Append(",\"name\":");
                Json.String(line, name);
                End();
            }
            return ts;
        }

        bool Recording()
        {
            if (stopped) return false;
            if (events >= settings.MaxEvents || settings.Seconds > 0 && clock.Elapsed.TotalSeconds >= settings.Seconds)
            {
                // over the limit: the trace ends here (the process goes on)
                ThreadPool.QueueUserWorkItem(_ => Tracer.Stop());
                return false;
            }
            return true;
        }

        public int Enter(string siteText, object self, object[] args)
        {
            if (stopped) return 0;
            ThreadState ts = Thread();
            Site site = SiteOf(siteText);
            var entry = new Entry { Frame = Interlocked.Increment(ref nextFrame) };
            bool emit = ts.SuppressDepth == 0 && Recording();
            if (emit && settings.HotCalls > 0)
            {
                int window = (int)(Interlocked.Increment(ref calls) / Math.Max(1, settings.HotWindow));
                lock (site)
                {
                    if (site.Window != window)
                    {
                        site.Window = window;
                        site.WindowCalls = 0;
                    }
                    if (++site.WindowCalls > settings.HotCalls)
                    {
                        emit = false;
                        entry.Suppressing = true;
                        site.Suppressed++;
                    }
                }
            }
            else if (ts.SuppressDepth > 0) site.Suppressed++;
            entry.Emitted = emit;
            ts.Stack.Add(entry);
            if (entry.Suppressing) ts.SuppressDepth++;
            if (!emit) return entry.Frame;

            int parent = 0;
            for (int i = ts.Stack.Count - 2; i >= 0; i--)
                if (ts.Stack[i].Emitted)
                {
                    parent = ts.Stack[i].Frame;
                    break;
                }

            lock (gate)
            {
                if (stopped) return entry.Frame;
                // an object the trace has not seen: born here (its constructor) or, created before or outside traced
                // code, first met as the receiver of this call
                int selfId = 0;
                if (self != null && !site.IsStatic)
                {
                    if (ids.TryGetValue(self, out ObjectId known)) selfId = known.Id;
                    else
                    {
                        selfId = ++nextObject;
                        ids.Add(self, new ObjectId { Id = selfId });
                        NewObject(ts, parent, selfId, self.GetType());
                    }
                }

                Begin("call", ts.Id);
                line.Append(",\"frame\":").Append(entry.Frame);
                if (parent != 0) line.Append(",\"parent\":").Append(parent);
                line.Append(",\"fn\":").Append(site.FnJson).Append(",\"args\":[");
                bool first = true;
                if (selfId != 0)
                {
                    line.Append("{\"name\":\"this\",\"v\":{\"ref\":").Append(selfId).Append("}}");
                    first = false;
                }
                if (args != null && settings.Values)
                    for (int i = 0; i < args.Length && i < site.ArgNames.Length; i++)
                    {
                        if (!first) line.Append(',');
                        first = false;
                        line.Append("{\"name\":");
                        Json.String(line, site.ArgNames[i]);
                        line.Append(",\"v\":");
                        Json.Value(line, args[i], knownId);
                        line.Append('}');
                    }
                line.Append(']');
                End();
            }
            return entry.Frame;
        }

        void NewObject(ThreadState ts, int frame, int id, Type type)
        {
            Begin("new", ts.Id);
            line.Append(",\"frame\":").Append(frame).Append(",\"obj\":").Append(id).Append(",\"kind\":\"object\",\"cls\":{\"name\":");
            Json.String(line, TypeNames.Plain(type));
            line.Append(",\"module\":");
            Json.String(line, type.Namespace ?? "");
            string key = type.FullName ?? type.Name;
            lock (sites)
                if (typeFiles.TryGetValue(key, out string f))
                {
                    line.Append(",\"file\":");
                    Json.String(line, f);
                }
            line.Append("},\"fields\":{}");
            End();
        }

        public void Leave(int frame, bool hasValue, object value, Exception exception)
        {
            if (currentOwner != this || current == null) return;
            ThreadState ts = current;
            // pop to the frame (frames left without a return, by an exception before their try block, go with it)
            for (int i = ts.Stack.Count - 1; i >= 0; i--)
            {
                Entry e = ts.Stack[i];
                ts.Stack.RemoveAt(i);
                if (e.Suppressing) ts.SuppressDepth--;
                bool mine = e.Frame == frame;
                if (e.Emitted && !stopped)
                    lock (gate)
                    {
                        if (stopped) return;
                        if (mine && exception != null)
                        {
                            Begin("raise", ts.Id);
                            line.Append(",\"frame\":").Append(frame).Append(",\"exc\":");
                            Json.String(line, exception.GetType().Name);
                            line.Append(",\"msg\":");
                            Json.String(line, exception.Message, Json.MaxString);
                            End();
                        }
                        Begin("return", ts.Id);
                        line.Append(",\"frame\":").Append(e.Frame);
                        if (mine && hasValue && settings.Values)
                        {
                            line.Append(",\"v\":");
                            Json.Value(line, value, knownId);
                        }
                        End();
                    }
                if (mine) return;
            }
        }

        /// <summary>Close every open frame, write "end" with what the hot-method filter left out, and close the file.</summary>
        public void Stop()
        {
            lock (gate)
            {
                if (stopped) return;
                foreach (ThreadState ts in threads)
                    for (int i = ts.Stack.Count - 1; i >= 0; i--)
                        if (ts.Stack[i].Emitted)
                        {
                            Begin("return", ts.Id);
                            line.Append(",\"frame\":").Append(ts.Stack[i].Frame);
                            End();
                        }
                Begin("end", 0);
                line.Append(",\"objects\":").Append(nextObject).Append(",\"frames\":").Append(nextFrame).Append(",\"hot\":{");
                bool first = true;
                lock (sites)
                    foreach (Site s in sites.Values)
                    {
                        if (s.Suppressed == 0) continue;
                        if (!first) line.Append(',');
                        first = false;
                        Json.String(line, s.Name);
                        line.Append(':').Append(s.Suppressed);
                    }
                line.Append('}');
                End();
                stopped = true;
            }
            file.Dispose();
            Tracer.Ended(this);
        }

        void Begin(string type, int thread)
        {
            line.Clear();
            line.Append("{\"type\":\"").Append(type).Append('"');
            if (thread != 0) line.Append(",\"th\":").Append(thread);
        }

        void End()
        {
            line.Append(",\"seq\":").Append(++seq).Append(",\"t\":").Append(clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency).Append('}');
            file.Write(line.ToString());
            events++;
        }
    }
}
