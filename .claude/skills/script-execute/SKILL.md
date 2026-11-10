---
name: script-execute
description: "Compiles and runs C# in the Godot editor (Roslyn): plain statements with the last expression as the result, or a full class with a static method. See the skill body for the modes, usings/include, output and parameters."
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---

# Script / Execute

Compiles and runs C# in the Godot editor: plain statements with the last expression as the result (own usings, local classes, await allowed), or a declared class 'className' with static method 'methodName'. Returns the result and the printed output (Console; GD.Print on Godot 4.5+). Guarded: an endless loop is stopped when its time budget ('timeout', default 30 s) runs out, and runaway recursion is stopped, with an error, and the process keeps running. Threads and tasks the code starts (new Thread, Task.Run, ThreadPool) are stopped at their next loop iteration or call once the run ends (a warning counts them); timers, async void code after its first await and work blocked in a wait are not, so join or await what you start.

Argument types — give these arguments as RQL values (as in rql-mutate): {key: value, …}, [a, b], "text", 1.5, true, ref("Node3D#12") from a query result, godot.find("Name"); leave out keys you do not need:
SerializedMemberList — named values {count: 3, label: "x", target: ref("Node3D#12")}: each is passed as a parameter of the value's type (a node, string, int, float, bool)

## Modes

- **Snippet** (default): plain statements. The last expression is the result (`EditorInterface.Singleton.GetEditedSceneRoot().Name`). Own `using` lines, local classes and functions are allowed; `await` works. Usings for System, LINQ and Godot are already there; an unknown type name (also one of the game's own classes) is imported automatically when exactly one namespace has it.
- **Full code**: when `csharpCode` declares class `className` with a static method `methodName`, that method runs.
- `isMethodBody=true` forces snippet mode.

The code runs on the editor main thread; Godot APIs and the game's compiled C# types are available (build with script-build to see new game code). Compile errors point at your own lines (`snippet.cs(2,5)`). Nothing is kept between calls: use `include` for shared helpers. Edits the code makes are not in the editor's undo history; for undoable edits use `EditorInterface.Singleton.GetEditorUndoRedo()` or rql-mutate.

## Output

`result` holds the value (type name and serialized value; `Success` when nothing is returned). `logs` holds what the code printed: `Console` always; `GD.Print`, `GD.PushWarning` / `GD.PushError` when the project builds with Godot.NET.Sdk 4.5+. Both are capped (20000 characters each, 2000 per output line; the cut is noted): return a count, a summary or a slice rather than a whole data set. A lazy sequence (LINQ query, iterator) is enumerated, at most 1000 items (one that never yields an item would block the editor).

## usings / include

- `usings`: extra namespaces, e.g. `["MyGame.Gameplay"]`.
- `include`: helper `.cs` files or folders (recursive), relative to `<project>/.ai-game-dev/snippets`, compiled with the snippet. Godot does not compile that folder into the game.

## Parameters

Parameters become the snippet's variables (or the method's arguments, in order); each has `name`, `typeName` and a value.

## How to Call

Run from the project's root directory:

```powershell
[System.IO.File]::WriteAllText((Join-Path $PWD 'args.json'), @'
{
  "csharpCode": "string_value",
  "className": "string_value",
  "methodName": "string_value",
  "parameters": "string_value",
  "isMethodBody": false,
  "usings": [],
  "include": [],
  "timeout": 0
}
'@)
godot-cli.cmd run-tool script-execute --input-file args.json
```

> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `csharpCode` | `string` | Yes | C# code. Snippet: 'var root = EditorInterface.Singleton.GetEditedSceneRoot();\nroot.GetChildCount()' (System, LINQ and Godot are imported; the last expression is the result, so no ';' after it). Full: 'using Godot;\npublic class Script { public static int Main() { return 42; } }' (full code brings its own usings). |
| `className` | `string` | No | Full mode: the class whose static method runs (namespace-qualified or not). |
| `methodName` | `string` | No | Full mode: the static method to run. |
| `parameters` | `string` | No | Values for the snippet's / method's parameters by name, e.g. {node: ref("Node3D#12"), n: 6}. Leave empty if there are none. RQL: SerializedMemberList |
| `isMethodBody` | `boolean` | No | Force snippet mode even when the code declares 'className'. |
| `usings` | `array` | No | Extra namespaces for snippet mode, e.g. ["MyGame.Gameplay"]. |
| `include` | `array` | No | Helper .cs files or folders (recursive) compiled with the code, relative to <project>/.ai-game-dev/snippets. |
| `timeout` | `integer` | No | Time budget of the run in seconds (default 30, at most 240; 0 = default). When it runs out the code is stopped at its next loop iteration or call and the call fails with Timeout; give long-running work a larger value. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "csharpCode": {
      "type": "string",
      "description": "C# code. Snippet: 'var root = EditorInterface.Singleton.GetEditedSceneRoot();\\nroot.GetChildCount()' (System, LINQ and Godot are imported; the last expression is the result, so no ';' after it). Full: 'using Godot;\\npublic class Script { public static int Main() { return 42; } }' (full code brings its own usings)."
    },
    "className": {
      "type": "string",
      "description": "Full mode: the class whose static method runs (namespace-qualified or not)."
    },
    "methodName": {
      "type": "string",
      "description": "Full mode: the static method to run."
    },
    "parameters": {
      "type": "string",
      "description": "Values for the snippet's / method's parameters by name, e.g. {node: ref(\"Node3D#12\"), n: 6}. Leave empty if there are none. RQL: SerializedMemberList"
    },
    "isMethodBody": {
      "type": "boolean",
      "description": "Force snippet mode even when the code declares 'className'."
    },
    "usings": {
      "type": "array",
      "items": {
        "type": "string"
      },
      "description": "Extra namespaces for snippet mode, e.g. [\"MyGame.Gameplay\"]."
    },
    "include": {
      "type": "array",
      "items": {
        "type": "string"
      },
      "description": "Helper .cs files or folders (recursive) compiled with the code, relative to <project>/.ai-game-dev/snippets."
    },
    "timeout": {
      "type": "integer",
      "description": "Time budget of the run in seconds (default 30, at most 240; 0 = default). When it runs out the code is stopped at its next loop iteration or call and the call fails with Timeout; give long-running work a larger value."
    }
  },
  "required": [
    "csharpCode"
  ]
}
```

## Output

This tool does not return structured output.

