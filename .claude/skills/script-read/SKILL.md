---
name: script-read
description: "Read a Godot script file (C# '.cs' or GDScript '.gd') under res:// and return its content + metadata (res:// path, language, line count). Supports an optional 1-based 'lineFrom'/'lineTo' slice for partial reads (indices are clamped — out-of-range values read at-most the whole file). Pair with 'script-update'/'script-create' to write back. The file is read with Godot's FileAccess so it works on any res:// path (imported or not). Returns a structured ScriptInfo."
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---

# Script / Read

Read a Godot script file (C# '.cs' or GDScript '.gd') under res:// and return its content + metadata (res:// path, language, line count). Supports an optional 1-based 'lineFrom'/'lineTo' slice for partial reads (indices are clamped — out-of-range values read at-most the whole file). Pair with 'script-update'/'script-create' to write back. The file is read with Godot's FileAccess so it works on any res:// path (imported or not). Returns a structured ScriptInfo.

## How to Call

Run from the project's root directory:

```powershell
[System.IO.File]::WriteAllText((Join-Path $PWD 'args.json'), @'
{
  "scriptPath": "string_value",
  "lineFrom": 0,
  "lineTo": 0
}
'@)
godot-cli.cmd run-tool script-read --input-file args.json
```

> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `scriptPath` | `string` | Yes | res:// path of the script to read, e.g. 'res://scripts/player.gd' or 'res://scripts/Enemy.cs'. Must end in '.cs' or '.gd'. |
| `lineFrom` | `integer` | No | 1-based start line of the slice (default 1). Clamped into range. |
| `lineTo` | `integer` | No | 1-based inclusive end line of the slice (default -1 = end of file). Clamped into range. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "scriptPath": {
      "type": "string",
      "description": "res:// path of the script to read, e.g. 'res://scripts/player.gd' or 'res://scripts/Enemy.cs'. Must end in '.cs' or '.gd'."
    },
    "lineFrom": {
      "type": "integer",
      "description": "1-based start line of the slice (default 1). Clamped into range."
    },
    "lineTo": {
      "type": "integer",
      "description": "1-based inclusive end line of the slice (default -1 = end of file). Clamped into range."
    }
  },
  "required": [
    "scriptPath"
  ]
}
```

## Output

This tool does not return structured output.

