---
name: script-create
description: "Create a NEW Godot script file (C# '.cs' or GDScript '.gd') under res:// with the provided source. Fails if a file already exists at the path — use 'script-update' to overwrite. GDScript content is syntax-validated before write (invalid '.gd' is rejected and nothing is written); C# is accepted as-is (no in-editor compiler) and the post-write build settle surfaces compile errors. Missing parent directories are created. For a '.cs' file the editor filesystem reimports and BOUNDED-settles before returning (then 'script-build' builds the project and loads the new type); a '.gd' file only needs a filesystem update. Returns a structured ScriptInfo."
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---

# Script / Create

Create a NEW Godot script file (C# '.cs' or GDScript '.gd') under res:// with the provided source. Fails if a file already exists at the path — use 'script-update' to overwrite. GDScript content is syntax-validated before write (invalid '.gd' is rejected and nothing is written); C# is accepted as-is (no in-editor compiler) and the post-write build settle surfaces compile errors. Missing parent directories are created. For a '.cs' file the editor filesystem reimports and BOUNDED-settles before returning (then 'script-build' builds the project and loads the new type); a '.gd' file only needs a filesystem update. Returns a structured ScriptInfo.

## How to Call

Run from the project's root directory:

```powershell
[System.IO.File]::WriteAllText((Join-Path $PWD 'args.json'), @'
{
  "scriptPath": "string_value",
  "content": "string_value"
}
'@)
godot-cli.cmd run-tool script-create --input-file args.json
```

> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `scriptPath` | `string` | Yes | res:// path for the new script, ending in '.cs' (C#) or '.gd' (GDScript), e.g. 'res://scripts/Enemy.cs' or 'res://scripts/player.gd'. |
| `content` | `string` | Yes | Source code for the new script file. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "scriptPath": {
      "type": "string",
      "description": "res:// path for the new script, ending in '.cs' (C#) or '.gd' (GDScript), e.g. 'res://scripts/Enemy.cs' or 'res://scripts/player.gd'."
    },
    "content": {
      "type": "string",
      "description": "Source code for the new script file."
    }
  },
  "required": [
    "scriptPath",
    "content"
  ]
}
```

## Output

This tool does not return structured output.

