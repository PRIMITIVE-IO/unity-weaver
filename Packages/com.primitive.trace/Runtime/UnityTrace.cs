using System.IO;
using UnityEngine;

namespace Primitive.Trace
{
    /// <summary>
    /// Records play mode startup (and a player's startup) when the project was compiled with PRIMITIVE_TRACE: the
    /// trace starts before the first scene loads and ends after the configured number of seconds, or when play
    /// stops. It is written to the Primitive traces folder, where the proxy shows it over the project's codemap as
    /// the workflow "&lt;project&gt; startup".
    /// </summary>
    public static class UnityTrace
    {
        /// <summary>EditorPrefs key for the startup trace's length in seconds (0: until play stops).</summary>
        public const string SecondsKey = "Primitive.Trace.Seconds";

        public const float DefaultSeconds = 20f;

#if PRIMITIVE_TRACE
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Begin()
        {
            Tracer.Reset();
            TraceSettings s = TraceSettings.FromEnvironment();
            string project = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            if (string.IsNullOrEmpty(s.Workflow)) s.Workflow = Path.GetFileName(project) + " startup";
            if (Application.isEditor)
            {
                // the editor compiles with paths relative to the project folder
                if (string.IsNullOrEmpty(s.Root)) s.Root = project;
            }
            else
            {
                s.LocationKind = Application.platform == RuntimePlatform.Android ? "headset" : s.LocationKind;
                if (string.IsNullOrEmpty(s.Output) && Application.platform == RuntimePlatform.Android)
                    s.Output = Path.Combine(Application.persistentDataPath, "traces", TraceSettings.Safe(s.Workflow) + ".jsonl");
            }
#if UNITY_EDITOR
            if (s.Seconds <= 0) s.Seconds = UnityEditor.EditorPrefs.GetFloat(SecondsKey, DefaultSeconds);
#else
            if (s.Seconds <= 0) s.Seconds = DefaultSeconds;
#endif
            if (Tracer.Start(s)) Debug.Log($"Primitive Trace: recording {s.Workflow} to {Tracer.OutputPath}" + (s.Seconds > 0 ? $" for {s.Seconds:0.#} s" : ""));
            Application.quitting -= Tracer.Stop;
            Application.quitting += Tracer.Stop;
        }
#endif
    }
}
