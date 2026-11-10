---
name: script-validate
description: |-
  Validate GDScript ('.gd') files and return STRUCTURED parse/compile diagnostics — the on-demand 'is the project error-free?' query. Closes the gap where the agent gets NO feedback on GDScript parse errors and assumes the game runs fine.
  Inputs:
    - 'scriptPath' (optional): a single res:// '.gd' path to validate. Omit to scan every '.gd' under res:// (capped at FullScanFileCap).
  Result (ScriptDiagnosticsResult): 'ok' (true when no errors), 'diagnostics' [{ path, line, message, severity }], counts, a 'truncated' flag (true when a full scan hit the file cap, so 'ok' is NOT a whole-project all-clear), and a 'fidelity' note:
    - Godot 4.5+: 'Precise' — exact line + message captured via the engine Logger hook.
    - Godot < 4.5: 'Coarse' — per-file pass/fail with the engine error code (no line/message text).
  GDScript warnings (unused variables, shadowed names, … as enabled in Project Settings → Debug → GDScript) come back with severity Warning on Godot 4.5+ for scripts without errors (res://addons/ skipped like the…
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---

# Script / Validate

Validate GDScript ('.gd') files and return STRUCTURED parse/compile diagnostics — the on-demand 'is the project error-free?' query. Closes the gap where the agent gets NO feedback on GDScript parse errors and assumes the game runs fine.
Inputs:
  - 'scriptPath' (optional): a single res:// '.gd' path to validate. Omit to scan every '.gd' under res:// (capped at FullScanFileCap).
Result (ScriptDiagnosticsResult): 'ok' (true when no errors), 'diagnostics' [{ path, line, message, severity }], counts, a 'truncated' flag (true when a full scan hit the file cap, so 'ok' is NOT a whole-project all-clear), and a 'fidelity' note:
  - Godot 4.5+: 'Precise' — exact line + message captured via the engine Logger hook.
  - Godot < 4.5: 'Coarse' — per-file pass/fail with the engine error code (no line/message text).
GDScript warnings (unused variables, shadowed names, … as enabled in Project Settings → Debug → GDScript) come back with severity Warning on Godot 4.5+ for scripts without errors (res://addons/ skipped like the editor does); they do not make 'ok' false.
C# ('.cs') files are NOT validated here (Godot has no in-editor C# compiler to reach cheaply); their compile errors surface through the project build. Pair with the RQL 'console' source (rql-query 'console | where logType == "Error"'), which on Godot 4.5+ also captures engine GDScript parse errors passively.

## How to Call

Run from the project's root directory:

```powershell
[System.IO.File]::WriteAllText((Join-Path $PWD 'args.json'), @'
{
  "scriptPath": "string_value"
}
'@)
godot-cli.cmd run-tool script-validate --input-file args.json
```

> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `scriptPath` | `string` | No | Optional single res:// '.gd' script path to validate, e.g. 'res://scripts/player.gd'. Omit (null/empty) to scan every '.gd' file under res://. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "scriptPath": {
      "type": "string",
      "description": "Optional single res:// '.gd' script path to validate, e.g. 'res://scripts/player.gd'. Omit (null/empty) to scan every '.gd' file under res://."
    }
  }
}
```

## Output

This tool does not return structured output.

