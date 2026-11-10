---
name: script-attach-to-node
description: |-
  Attach a script (C# '.cs' or GDScript '.gd') to a Node in the currently edited Godot scene — the Godot analog of adding a MonoBehaviour to a GameObject. Identify the target with 'nodeRef' (instanceId preferred, else scene-tree path) and the script with 'scriptPath' (a res:// script file that must exist). Pass an empty/whitespace/null 'scriptPath' to DETACH (clear) the node's script. The script resource is loaded and set via Node.SetScript; the scene is marked unsaved (persist with rql-mutate 'editor | call SaveScene()'). Returns the script's structured ScriptInfo on attach, or an identity record with a 'detached' status on clear.
  
  Argument types — give these arguments as RQL values (as in rql-mutate): {key: value, …}, [a, b], "text", 1.5, true, ref("Node3D#12") from a query result, godot.find("Name"); leave out keys you do not need:
  NodeRef — a node: ref("Node3D#12") from a query, godot.find("Name"), an instance id, or its path below the scene root as text ("Level/Player")
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---

# Script / Attach To Node

Attach a script (C# '.cs' or GDScript '.gd') to a Node in the currently edited Godot scene — the Godot analog of adding a MonoBehaviour to a GameObject. Identify the target with 'nodeRef' (instanceId preferred, else scene-tree path) and the script with 'scriptPath' (a res:// script file that must exist). Pass an empty/whitespace/null 'scriptPath' to DETACH (clear) the node's script. The script resource is loaded and set via Node.SetScript; the scene is marked unsaved (persist with rql-mutate 'editor | call SaveScene()'). Returns the script's structured ScriptInfo on attach, or an identity record with a 'detached' status on clear.

Argument types — give these arguments as RQL values (as in rql-mutate): {key: value, …}, [a, b], "text", 1.5, true, ref("Node3D#12") from a query result, godot.find("Name"); leave out keys you do not need:
NodeRef — a node: ref("Node3D#12") from a query, godot.find("Name"), an instance id, or its path below the scene root as text ("Level/Player")

## How to Call

Run from the project's root directory:

```powershell
[System.IO.File]::WriteAllText((Join-Path $PWD 'args.json'), @'
{
  "nodeRef": "string_value",
  "scriptPath": "string_value"
}
'@)
godot-cli.cmd run-tool script-attach-to-node --input-file args.json
```

> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `nodeRef` | `string` | Yes | Reference to the target Node (instanceId preferred, else scene-tree path). RQL: NodeRef |
| `scriptPath` | `string` | No | res:// path of the script to attach, ending in '.cs' or '.gd'. Pass empty/whitespace/null to DETACH the node's current script. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "nodeRef": {
      "type": "string",
      "description": "Reference to the target Node (instanceId preferred, else scene-tree path). RQL: NodeRef"
    },
    "scriptPath": {
      "type": "string",
      "description": "res:// path of the script to attach, ending in '.cs' or '.gd'. Pass empty/whitespace/null to DETACH the node's current script."
    }
  },
  "required": [
    "nodeRef"
  ]
}
```

## Output

This tool does not return structured output.

