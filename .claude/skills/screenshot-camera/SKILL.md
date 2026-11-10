---
name: screenshot-camera
description: |-
  Capture a screenshot from a specific Camera3D (or Camera2D) in the edited scene and return it as a PNG image for direct LLM inspection. Identify the camera with 'nodeRef' (instanceId preferred, else a scene-tree path). The camera's own world is rendered into a temporary off-screen SubViewport at the requested 'width'x'height', so the editor's live selection/camera is left untouched. Output edges are capped to keep the response transportable. NOTE: rendering needs a real GPU — a Godot launched with '--headless' yields an empty image, surfaced here as a structured error rather than a blank PNG.
  
  Argument types — give these arguments as RQL values (as in rql-mutate): {key: value, …}, [a, b], "text", 1.5, true, ref("Node3D#12") from a query result, godot.find("Name"); leave out keys you do not need:
  NodeRef — a node: ref("Node3D#12") from a query, godot.find("Name"), an instance id, or its path below the scene root as text ("Level/Player")
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---

# Screenshot / Camera

Capture a screenshot from a specific Camera3D (or Camera2D) in the edited scene and return it as a PNG image for direct LLM inspection. Identify the camera with 'nodeRef' (instanceId preferred, else a scene-tree path). The camera's own world is rendered into a temporary off-screen SubViewport at the requested 'width'x'height', so the editor's live selection/camera is left untouched. Output edges are capped to keep the response transportable. NOTE: rendering needs a real GPU — a Godot launched with '--headless' yields an empty image, surfaced here as a structured error rather than a blank PNG.

Argument types — give these arguments as RQL values (as in rql-mutate): {key: value, …}, [a, b], "text", 1.5, true, ref("Node3D#12") from a query result, godot.find("Name"); leave out keys you do not need:
NodeRef — a node: ref("Node3D#12") from a query, godot.find("Name"), an instance id, or its path below the scene root as text ("Level/Player")

## How to Call

Run from the project's root directory:

```powershell
[System.IO.File]::WriteAllText((Join-Path $PWD 'args.json'), @'
{
  "nodeRef": "string_value",
  "width": 0,
  "height": 0
}
'@)
godot-cli.cmd run-tool screenshot-camera --input-file args.json
```

> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `nodeRef` | `string` | Yes | Reference to the camera Node (Camera3D or Camera2D) to capture from (instanceId preferred, else scene-tree path). RQL: NodeRef |
| `width` | `integer` | No | Output width in pixels. Default 1920. Must be > 0 and within the dimension cap. |
| `height` | `integer` | No | Output height in pixels. Default 1080. Must be > 0 and within the dimension cap. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "nodeRef": {
      "type": "string",
      "description": "Reference to the camera Node (Camera3D or Camera2D) to capture from (instanceId preferred, else scene-tree path). RQL: NodeRef"
    },
    "width": {
      "type": "integer",
      "description": "Output width in pixels. Default 1920. Must be > 0 and within the dimension cap."
    },
    "height": {
      "type": "integer",
      "description": "Output height in pixels. Default 1080. Must be > 0 and within the dimension cap."
    }
  },
  "required": [
    "nodeRef"
  ]
}
```

## Output

This tool does not return structured output.

