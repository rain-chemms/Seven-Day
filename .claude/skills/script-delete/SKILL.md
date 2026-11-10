---
name: script-delete
description: "Delete a Godot script file (C# '.cs' or GDScript '.gd') from the res:// filesystem. Fails if no file exists at the path. The script's identity (path, language) is captured BEFORE deletion and returned for confirmation. The file is removed with DirAccess, along with its Godot '.uid' sidecar when present, then the editor filesystem is rescanned and (for a '.cs' file) BOUNDED-settles. WARNING: nodes/resources referencing the deleted script will break — verify with 'script-read' first. A loaded copy is detached from the path, so an open scene still using it embeds it on its next save. Returns a structured ScriptInfo."
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---

# Script / Delete

Delete a Godot script file (C# '.cs' or GDScript '.gd') from the res:// filesystem. Fails if no file exists at the path. The script's identity (path, language) is captured BEFORE deletion and returned for confirmation. The file is removed with DirAccess, along with its Godot '.uid' sidecar when present, then the editor filesystem is rescanned and (for a '.cs' file) BOUNDED-settles. WARNING: nodes/resources referencing the deleted script will break — verify with 'script-read' first. A loaded copy is detached from the path, so an open scene still using it embeds it on its next save. Returns a structured ScriptInfo.

## How to Call

Run from the project's root directory:

```powershell
[System.IO.File]::WriteAllText((Join-Path $PWD 'args.json'), @'
{
  "scriptPath": "string_value"
}
'@)
godot-cli.cmd run-tool script-delete --input-file args.json
```

> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `scriptPath` | `string` | Yes | res:// path of the script file to delete, ending in '.cs' or '.gd'. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "scriptPath": {
      "type": "string",
      "description": "res:// path of the script file to delete, ending in '.cs' or '.gd'."
    }
  },
  "required": [
    "scriptPath"
  ]
}
```

## Output

This tool does not return structured output.

