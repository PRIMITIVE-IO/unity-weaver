# Primitive Trace (Unity)

Records a Unity project's play mode startup for Primitive: every call, return, exception, thread and object creation of
the project's own code, written as primitive-trace JSONL to the Primitive traces folder
(`%LOCALAPPDATA%\Primitive\traces\<project> startup\`), where the proxy picks it up and plays it over the project's
codemap.

## Install

In the project's `Packages/manifest.json`:

```json
"com.primitive.trace": "file:../../unity-weaver/Packages/com.primitive.trace"
```

(primitive-env does this, with unity-weaver cloned beside it.)

## Use

**Primitive > Trace > Record Startup** turns instrumentation on (the `PRIMITIVE_TRACE` scripting define for the active
build target), waits for the scripts to recompile and enters play mode. The trace runs from before the first scene loads
for **Length** (10, 20 or 60 seconds, or until play stops), then the file closes and the proxy shows it.

- **Trace Play Mode Startup** toggles the instrumentation; every play session is recorded while it is on. Turn it off
  before committing: it changes ProjectSettings.
- **Stop Recording** ends the current trace early. **Open Traces Folder** shows where traces go.
- Which assemblies: `Assembly-CSharp` and `Primitive.Viewer`, or those listed in `ProjectSettings/PrimitiveTrace.json`:
  `{"assemblies": ["Assembly-CSharp", "My.Game"], "exclude": ["My.Game.Noisy", "My.Game.Player.Update"]}`.
- A player built with the define traces its own startup (into `persistentDataPath/traces` on Android).

## How

`CodeGen/TracePostProcessor.cs` is an IL post-processor (`Unity.Primitive.Trace.CodeGen`): Unity runs it inside the
compilation pipeline on every compile, with the PDB, so each call keeps its source file and line. It uses the same
weaver as the .NET tracer (fody-dotnet-trace), which replaces the old unity-weaver approach of rewriting
`Library/ScriptAssemblies` after compilation. MonoBehaviour and ScriptableObject constructors are skipped (they run on
every reload), as the original did; so is generated code.

`Runtime/` is the Primitive.Trace runtime (`UnityTrace.cs` starts it when play begins); `Runtime/*` except `UnityTrace.cs`
and `CodeGen/TraceWeaver.cs` are copied from fody-dotnet-trace by its `tools/sync-unity.ps1` — edit them there.
