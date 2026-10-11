using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Primitive.Trace.Editor
{
    /// <summary>
    /// Primitive > Trace: turn instrumentation on or off (the PRIMITIVE_TRACE define, for the active build target),
    /// record the next play mode startup in one step, choose how long a startup trace lasts, and open the traces
    /// folder the Primitive proxy watches.
    /// </summary>
    [InitializeOnLoad]
    static class TraceMenu
    {
        const string Root = "Primitive/Trace/";
        const string Define = "PRIMITIVE_TRACE";
        const string PlayAfterReload = "Primitive.Trace.PlayAfterReload";

        static TraceMenu()
        {
            // code running in edit mode never starts a trace; play mode starts one (UnityTrace)
            Tracer.AutoStart = false;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode) Tracer.Stop();
            };
            Tracer.Finished += path => Debug.Log($"Primitive Trace: wrote {path}; the Primitive proxy shows it over this project's codemap");
            // "Record Startup" asked for play once the instrumented scripts are compiled
            if (SessionState.GetBool(PlayAfterReload, false) && Enabled && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SessionState.EraseBool(PlayAfterReload);
                EditorApplication.delayCall += EditorApplication.EnterPlaymode;
            }
        }

        static NamedBuildTarget Target => NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup);

        static bool Enabled => PlayerSettings.GetScriptingDefineSymbols(Target).Split(';').Contains(Define);

        static void SetEnabled(bool on)
        {
            var defines = PlayerSettings.GetScriptingDefineSymbols(Target).Split(';').Where(d => d.Length > 0 && d != Define).ToList();
            if (on) defines.Add(Define);
            PlayerSettings.SetScriptingDefineSymbols(Target, string.Join(";", defines));
            Debug.Log(on
                ? "Primitive Trace: instrumenting Assembly-CSharp and Primitive.Viewer; play mode startup will be recorded"
                : "Primitive Trace: instrumentation removed");
        }

        [MenuItem(Root + "Record Startup", priority = 0)]
        static void RecordStartup()
        {
            if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
            if (Enabled)
            {
                EditorApplication.EnterPlaymode();
                return;
            }
            // play once the scripts are recompiled with the instrumentation
            SessionState.SetBool(PlayAfterReload, true);
            SetEnabled(true);
        }

        [MenuItem(Root + "Trace Play Mode Startup", priority = 1)]
        static void Toggle() => SetEnabled(!Enabled);

        [MenuItem(Root + "Trace Play Mode Startup", true)]
        static bool ToggleCheck()
        {
            Menu.SetChecked(Root + "Trace Play Mode Startup", Enabled);
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        [MenuItem(Root + "Stop Recording", priority = 2)]
        static void Stop() => Tracer.Stop();

        [MenuItem(Root + "Stop Recording", true)]
        static bool StopCheck() => Tracer.IsRecording;

        static void Length(float seconds) => EditorPrefs.SetFloat(UnityTrace.SecondsKey, seconds);

        static bool LengthCheck(string item, float seconds)
        {
            Menu.SetChecked(Root + "Length/" + item, Mathf.Approximately(EditorPrefs.GetFloat(UnityTrace.SecondsKey, UnityTrace.DefaultSeconds), seconds));
            return true;
        }

        [MenuItem(Root + "Length/10 s", priority = 20)] static void L10() => Length(10);
        [MenuItem(Root + "Length/10 s", true)] static bool L10Check() => LengthCheck("10 s", 10);
        [MenuItem(Root + "Length/20 s", priority = 21)] static void L20() => Length(20);
        [MenuItem(Root + "Length/20 s", true)] static bool L20Check() => LengthCheck("20 s", 20);
        [MenuItem(Root + "Length/60 s", priority = 22)] static void L60() => Length(60);
        [MenuItem(Root + "Length/60 s", true)] static bool L60Check() => LengthCheck("60 s", 60);
        [MenuItem(Root + "Length/Until Play Stops", priority = 23)] static void LAll() => Length(0);
        [MenuItem(Root + "Length/Until Play Stops", true)] static bool LAllCheck() => LengthCheck("Until Play Stops", 0);

        [MenuItem(Root + "Open Traces Folder", priority = 40)]
        static void Open()
        {
            System.IO.Directory.CreateDirectory(TraceSettings.DefaultFolder);
            EditorUtility.RevealInFinder(TraceSettings.DefaultFolder);
        }
    }
}
