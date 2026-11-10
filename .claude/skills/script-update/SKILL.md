---
name: script-update
description: "Overwrite an EXISTING Godot script file (C# '.cs' or GDScript '.gd') under res:// with the provided source. Fails if no file exists at the path — use 'script-create' to create one. GDScript content is syntax-validated before write (invalid '.gd' is rejected and the existing file is left untouched); C# is accepted as-is (no in-editor compiler) and the post-write build settle surfaces compile errors. For a '.cs' file the editor filesystem reimports and BOUNDED-settles before returning (then 'script-build' loads the changed type); a '.gd' file only needs a filesystem update so the editor re-parses it. Use 'script-read' to inspect the current content first. Returns a structured ScriptInfo."
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---

# Script / Update

Overwrite an EXISTING Godot script file (C# '.cs' or GDScript '.gd') under res:// with the provided source. Fails if no file exists at the path — use 'script-create' to create one. GDScript content is syntax-validated before write (invalid '.gd' is rejected and the existing file is left untouched); C# is accepted as-is (no in-editor compiler) and the post-write build settle surfaces compile errors. For a '.cs' file the editor filesystem reimports and BOUNDED-settles before returning (then 'script-build' loads the changed type); a '.gd' file only needs a filesystem update so the editor re-parses it. Use 'script-read' to inspect the current content first. Returns a structured ScriptInfo.

## How to Call

Run from the project's root directory:

```powershell
[System.IO.File]::WriteAllText((Join-Path $PWD 'args.json'), @'
{
  "scriptPath": "string_value",
  "content": "string_value"
}
'@)
godot-cli.cmd run-tool script-update --input-file args.json
```

> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `scriptPath` | `string` | Yes | res:// path of the existing script to overwrite, ending in '.cs' or '.gd'. |
| `content` | `string` | Yes | New source code to write (replaces the file's entire content). |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "scriptPath": {
      "type": "string",
      "description": "res:// path of the existing script to overwrite, ending in '.cs' or '.gd'."
    },
    "content": {
      "type": "string",
      "description": "New source code to write (replaces the file's entire content)."
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

