---
name: godot-skill-create
description: Create a new skill (MCP tool) for the Godot Editor by writing a C# (.cs) file into the project, which Godot compiles into the project assembly. After a rebuild the new tool becomes callable through MCP. The file must declare a partial class decorated with [AiToolType]; each tool method must be decorated with [AiTool] plus [Description]; every Godot API call must run through com.IvanMurzak.ReflectorNet.Utils.MainThread.Instance.Run(); editor-only code must be wrapped in #if TOOLS; and the method should return a structured data model (for parseable output) or void (for side-effect-only operations). See the body of this skill for a full sample and best-practice notes.
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---

# Skill (Tool) / Create

Create a new skill (MCP tool) using C# code. The code is written to a '.cs' file under res:// and compiled into the project assembly by Godot; the tool becomes callable after the project is REBUILT (Godot builds C# out-of-band — there is no automatic reload like Unity's). An existing file at the path is overwritten.

The file must declare a partial class decorated with [AiToolType]; each tool method must carry [AiTool("<tool-name>", ...)] and [Description]. All Godot API calls must be marshalled onto the editor main thread via com.IvanMurzak.ReflectorNet.Utils.MainThread.Instance.Run(), and editor-only code (EditorInterface / EditorPlugin / EditorFileSystem) must be wrapped in #if TOOLS so it is stripped from an exported game build. Return a data model for structured output, or void for side-effect-only operations.

The CONSUMER project's .csproj must already declare the addon's NuGet package references (com.IvanMurzak.ReflectorNet, com.IvanMurzak.McpPlugin) — without them the whole project assembly fails to compile.

Create a new skill (MCP tool) for the Godot Editor by writing a C# (`.cs`) file into the project. Godot compiles every `.cs` under the project into ONE assembly, so after the project is rebuilt the new tool is discovered by the addon's assembly scanner and becomes callable through MCP.

## Inputs

- `path` — `res://` path of the C# file to write, e.g. `res://Skills/Tool_Sample.cs`. Must be a `res://` FILE path ending in `.cs`, with no `..` segment. GDScript (`.gd`) is rejected: only C# can carry the `[AiToolType]`/`[AiTool]` attributes the scanner discovers.
- `code` — the full C# source for the tool file.

## Behavior

Missing parent directories are created, an existing file at `path` is OVERWRITTEN (the re-emit-after-a-fix loop), the file is reimported through the editor filesystem, and the tool bounded-waits for the scan to settle before returning a structured `ScriptInfo`.

**Rebuild required.** Godot builds C# out-of-band (on editor focus, or an explicit *Build*), so the new tool is NOT callable the instant this returns — unlike Unity, there is no automatic domain reload. Trigger a build, then re-list the tools.

## Requirements for the file to compile

The CONSUMER project's `.csproj` must already declare the same NuGet `PackageReference`s the addon depends on (`com.IvanMurzak.ReflectorNet`, `com.IvanMurzak.McpPlugin`) — the addon ships as source, so its own csproj does not carry into the project. If they are missing, the whole project assembly fails to compile, not just the new file.

## Full sample

```csharp
// This sample drives EditorInterface, so the WHOLE file is guarded: `TOOLS` is defined only
// in the editor build, and Godot compiles every .cs in the project into ONE assembly — so
// without this guard an exported game build fails to compile (see 'Guard editor-only code'
// below). A tool that touches no editor API needs no guard.
#if TOOLS
#nullable enable
using System;
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using Godot;

namespace com.IvanMurzak.Godot.MCP.Tools
{
    [AiToolType]
    public partial class Tool_Sample
    {
        public const string SampleRenameToolId = "sample-rename";

        [AiTool(SampleRenameToolId, Title = "Sample / Rename", ReadOnlyHint = false, DestructiveHint = false,
            IdempotentHint = true, OpenWorldHint = false)]
        [Description("Renames a node in the currently edited scene.")]
        public string Rename
        (
            [Description("Node path of the node to rename, e.g. '/root/Main/Player'.")]
            string nodePath,
            [Description("New name to assign.")]
            string newName
        )
        {
            if (string.IsNullOrEmpty(newName))
                throw new ArgumentException("New name cannot be null or empty.", nameof(newName));

            return MainThread.Instance.Run(() =>
            {
                var root = EditorInterface.Singleton.GetEditedSceneRoot()
                    ?? throw new InvalidOperationException("No scene is currently open in the editor.");

                var node = root.GetNodeOrNull(nodePath)
                    ?? throw new ArgumentException($"Node '{nodePath}' not found.", nameof(nodePath));

                node.Name = newName;
                return $"Renamed to '{newName}'.";
            });
        }
    }
}
#endif
```

## Suggestions

### Always marshal Godot API calls onto the main thread
Tool handlers run on a background SignalR thread. EVERY touch of a `Node`, `Resource`, `SceneTree`, or `EditorInterface` must go through `MainThread.Instance.Run(() => { ... })` (`com.IvanMurzak.ReflectorNet.Utils.MainThread`) — off-thread access crashes the editor rather than throwing.

### Guard editor-only code with `#if TOOLS`
A tool that touches `EditorInterface`/`EditorPlugin`/`EditorFileSystem` must be wrapped in `#if TOOLS ... #endif` so it is stripped from an exported game build (where those APIs do not exist). A tool that only needs pure-managed state (like `ping`) should stay unguarded.

### Mark game-only tools `ToolTarget.Runtime`
A tool for the RUNNING game (registered by `GodotMcpRuntime`, reading the live game) is marked `[AiToolType(Target = ToolTarget.Runtime)]` (`using com.IvanMurzak.McpPlugin.Common;`): the editor then does not register it, and the agent reaches the game even while the editor is connected. Editor tools keep the default.

### Annotate every tool
Give every `[AiTool]` a `Title` and set `ReadOnlyHint`, `DestructiveHint`, `IdempotentHint` and `OpenWorldHint` explicitly: pure reads are read-only; deleting or overwriting anything, or running arbitrary code, is destructive. MCP clients use these hints to decide which calls need the user's confirmation.

### Return structured data, not formatted strings
Prefer returning a data model (ReflectorNet serializes it for the client) over hand-formatted text, so the AI can read individual fields. Return `void` for side-effect-only operations.

### Validate inputs first and throw clearly
Validate required parameters at the top of the method, before any Godot API call, and throw `ArgumentException`/`InvalidOperationException` with a message that tells the AI how to self-correct.

### Follow the addon's file conventions
One `[AiToolType] partial class Tool_<Family>` per family, one tool method per partial-class file (`Tool_<Family>.<Method>.cs`), a `public const string <Name>ToolId` per tool, the Apache-2.0 header at the top of every file, `#nullable enable`, Allman braces, and 4-space indentation.

## How to Call

Run from the project's root directory:

```powershell
[System.IO.File]::WriteAllText((Join-Path $PWD 'args.json'), @'
{
  "path": "string_value",
  "code": "string_value"
}
'@)
godot-cli.cmd run-system-tool godot-skill-create --input-file args.json
```

> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `path` | `string` | Yes | res:// path for the C# (.cs) file to create, e.g. 'res://Skills/Tool_Sample.cs'. Must be a res:// file path ending in '.cs' with no '..' segment. GDScript ('.gd') is not accepted — only C# can declare MCP tools. |
| `code` | `string` | Yes | C# source code for the skill tool file. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string",
      "description": "res:// path for the C# (.cs) file to create, e.g. 'res://Skills/Tool_Sample.cs'. Must be a res:// file path ending in '.cs' with no '..' segment. GDScript ('.gd') is not accepted — only C# can declare MCP tools."
    },
    "code": {
      "type": "string",
      "description": "C# source code for the skill tool file."
    }
  },
  "required": [
    "path",
    "code"
  ]
}
```

## Output

### Output JSON Schema

```json
{
  "type": "object",
  "properties": {
    "result": {
      "$ref": "#/$defs/com.IvanMurzak.Godot.MCP.Data.ScriptInfo",
      "description": "Identity + (for reads) content of a script file: its res:// path, language, optional content, and a status note about any triggered compile/reload."
    }
  },
  "$defs": {
    "com.IvanMurzak.Godot.MCP.Data.ScriptInfo": {
      "type": "object",
      "properties": {
        "resourcePath": {
          "type": "string",
          "description": "res:// path of the script file, e.g. 'res://scripts/player.gd' or 'res://scripts/Enemy.cs'."
        },
        "language": {
          "type": "string",
          "description": "Script language: 'CSharp' for a .cs file, 'GDScript' for a .gd file."
        },
        "content": {
          "type": "string",
          "description": "The script's text content. Populated by 'script-read'; null on the write/delete/attach confirmation payloads (which echo identity only)."
        },
        "lineCount": {
          "type": "integer",
          "description": "Number of lines in 'content' when present (the read slice's line count), else 0."
        },
        "status": {
          "type": "string",
          "description": "Short human-readable status note, e.g. 'Script created; build settled.' or 'Script read.'. For C# writes/deletes this records the bounded compile/reload settle outcome; null when no status applies."
        }
      },
      "required": [
        "lineCount"
      ],
      "description": "Identity + (for reads) content of a script file: its res:// path, language, optional content, and a status note about any triggered compile/reload."
    }
  },
  "required": [
    "result"
  ]
}
```

