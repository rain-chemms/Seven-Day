---
name: ping
description: Lightweight readiness probe for the Godot-MCP connection. Returns the input 'message' echoed back, or 'pong' when omitted. Useful for verifying SignalR connectivity end-to-end after the editor plugin connects to the MCP server.
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---

# Ping

Lightweight readiness probe for the Godot-MCP connection. Returns the input 'message' echoed back, or 'pong' when omitted. Useful for verifying SignalR connectivity end-to-end after the editor plugin connects to the MCP server.

## How to Call

Run from the project's root directory:

```powershell
[System.IO.File]::WriteAllText((Join-Path $PWD 'args.json'), @'
{
  "message": "string_value"
}
'@)
godot-cli.cmd run-system-tool ping --input-file args.json
```

> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `message` | `string` | No | Optional message to echo back. When null or empty, the tool returns 'pong'. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "message": {
      "type": "string",
      "description": "Optional message to echo back. When null or empty, the tool returns 'pong'."
    }
  }
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

