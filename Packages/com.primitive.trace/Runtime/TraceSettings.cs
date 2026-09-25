// Copied from fody-dotnet-trace/src/Primitive.Trace/TraceSettings.cs by tools/sync-unity.ps1; edit the original.
using System;
using System.Diagnostics;
using System.IO;

namespace Primitive.Trace
{
    /// <summary>
    /// How a trace is recorded. Read from environment variables (set by <c>primitive-trace run</c>) unless a host
    /// (the Unity package) configures them first with <see cref="Tracer.Configure"/>.
    /// </summary>
    public sealed class TraceSettings
    {
        /// <summary>JSONL file to write; default &lt;traces folder&gt;/&lt;workflow&gt;/&lt;location&gt;-&lt;time&gt;.jsonl.</summary>
        public string Output;

        /// <summary>Traces of one workflow are shown together; default the entry assembly's name.</summary>
        public string Workflow;

        /// <summary>The traced project's root: file paths in the trace are relative to it (the proxy links by them).</summary>
        public string Root;

        /// <summary>Where the process runs; the proxy's own machine is "pc-workstation" by default.</summary>
        public string LocationId = "pc-workstation", LocationName = Environment.MachineName, LocationKind = "pc";

        /// <summary>Stop recording after this many events (the process keeps running).</summary>
        public int MaxEvents = 1_000_000;

        /// <summary>Stop recording after this many seconds (0: until the process ends).</summary>
        public double Seconds;

        /// <summary>
        /// The original ActiveRepetitions: a method called more than <see cref="HotCalls"/> times within the last
        /// <see cref="HotWindow"/> calls stops being recorded, together with everything it calls, for that window.
        /// </summary>
        public int HotCalls = 50, HotWindow = 10_000;

        /// <summary>Record argument and return values.</summary>
        public bool Values = true;

        /// <summary>The folder traces go to when no <see cref="Output"/> is given; the proxy watches it.</summary>
        public static string DefaultFolder
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("PRIMITIVE_TRACE_DIR");
                if (!string.IsNullOrEmpty(env)) return env;
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local)) local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                return Path.Combine(local, "Primitive", "traces");
            }
        }

        public static TraceSettings FromEnvironment()
        {
            var s = new TraceSettings
            {
                Output = Env("PRIMITIVE_TRACE_OUT"),
                Workflow = Env("PRIMITIVE_TRACE_WORKFLOW"),
                Root = Env("PRIMITIVE_TRACE_ROOT"),
            };
            s.LocationId = Env("PRIMITIVE_TRACE_LOCATION_ID") ?? s.LocationId;
            s.LocationName = Env("PRIMITIVE_TRACE_LOCATION_NAME") ?? s.LocationName;
            s.LocationKind = Env("PRIMITIVE_TRACE_LOCATION_KIND") ?? s.LocationKind;
            if (int.TryParse(Env("PRIMITIVE_TRACE_MAX_EVENTS"), out int max)) s.MaxEvents = max;
            if (double.TryParse(Env("PRIMITIVE_TRACE_SECONDS"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double seconds)) s.Seconds = seconds;
            if (int.TryParse(Env("PRIMITIVE_TRACE_HOT"), out int hot)) s.HotCalls = hot;
            if (Env("PRIMITIVE_TRACE_VALUES") == "0") s.Values = false;
            return s;
        }

        static string Env(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(v) ? null : v;
        }

        internal void Complete()
        {
            if (string.IsNullOrEmpty(Workflow))
            {
                string entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
                Workflow = entry ?? Process.GetCurrentProcess().ProcessName;
            }
            if (string.IsNullOrEmpty(Output))
                Output = Path.Combine(DefaultFolder, Safe(Workflow), $"{Safe(LocationId)}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
            if (!string.IsNullOrEmpty(Root)) Root = Path.GetFullPath(Root).Replace('\\', '/').TrimEnd('/');
        }

        internal static string Safe(string name)
        {
            char[] c = name.ToCharArray();
            for (int i = 0; i < c.Length; i++)
                if (!char.IsLetterOrDigit(c[i]) && c[i] != '-' && c[i] != '_' && c[i] != '.')
                    c[i] = '-';
            return new string(c);
        }
    }
}
