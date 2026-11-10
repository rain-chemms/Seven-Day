---
name: session-close
description: "Save and quit an editor started by godot-cli (open scenes with a path and the resources they changed). Only for that CLI session: 'nonce' must be the one in <project>/.ai-game-dev/session.json."
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---

# Session / Close

Save and quit an editor started by godot-cli (open scenes with a path and the resources they changed). Only for that CLI session: 'nonce' must be the one in <project>/.ai-game-dev/session.json.

## How to Call

Run from the project's root directory:

```powershell
[System.IO.File]::WriteAllText((Join-Path $PWD 'args.json'), @'
{
  "nonce": "string_value"
}
'@)
godot-cli.cmd run-system-tool session-close --input-file args.json
```

> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `nonce` | `string` | Yes | The session nonce from <project>/.ai-game-dev/session.json. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "nonce": {
      "type": "string",
      "description": "The session nonce from <project>/.ai-game-dev/session.json."
    }
  },
  "required": [
    "nonce"
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
      "type": "string"
    }
  },
  "required": [
    "result"
  ]
}
```

