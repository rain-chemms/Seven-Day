---
name: script-build
description: "Build the project's C# (dotnet build, Debug configuration like the editor's builds) so C# scripts written with 'script-create' / 'script-update' load: new [GlobalClass] types, changed node scripts. The build runs in the background; when it succeeds Godot reloads the C# assembly and the plugin reconnects, and the result (the compiler warnings, or the compiler errors) is delivered after that reload through the request's requestId. Tool calls made while the reload is pending are refused with a retryable error: retry them a few seconds later."
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---

# Script / Build

Build the project's C# (dotnet build, Debug configuration like the editor's builds) so C# scripts written with 'script-create' / 'script-update' load: new [GlobalClass] types, changed node scripts. The build runs in the background; when it succeeds Godot reloads the C# assembly and the plugin reconnects, and the result (the compiler warnings, or the compiler errors) is delivered after that reload through the request's requestId. Tool calls made while the reload is pending are refused with a retryable error: retry them a few seconds later.

## How to Call

Run from the project's root directory:

```powershell
[System.IO.File]::WriteAllText((Join-Path $PWD 'args.json'), @'
{}
'@)
godot-cli.cmd run-tool script-build --input-file args.json
```

> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

## Input

This tool takes no input parameters.

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {}
}
```

## Output

This tool does not return structured output.

