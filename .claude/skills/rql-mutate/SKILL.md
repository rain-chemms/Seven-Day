---
name: rql-mutate
description: "Change the live Godot editor with RQL set/call: bulk edits in one all-or-nothing, undoable call; add, duplicate, reparent and remove nodes; connect signals, groups; project settings, input map, autoloads, layer names; TileSets, Themes, shader parameters, MeshLibraries; create, move and delete resources; open and save scenes. dry_run previews."
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---

# RQL / Mutate

RQL (Reflection Query Language, reference: https://ai-game.dev/docs/rql). Change the live Godot editor with an RQL query whose last stage is set or call. All-or-nothing: every target and value is checked before anything is written; a failure changes nothing. Undoable in the editor (Ctrl+Z). Scene edits mark the scene unsaved (save with editor | call SaveScene()); edited resource files (res://….tres) are saved to disk automatically. dry_run=true returns the receipt ($ref, member, old, new) without changing anything. The receipt leaves out assignments that found the value already set; they are counted ("N unchanged assignment(s) not listed"). Do exactly the change requested; a read-only member fails with readonly-member instead of being approximated. Destructive cleanup over what a query finds (Free, DeleteResource, DeleteFolder on matches, not on paths the user named) takes two turns: list what matches with rql-query, show the list to the user and end your turn; delete only after the user confirms in a later message. When you map a misspelt or approximate name to an existing node, resource or member, tell the user which one you used.
  set path = expr, ...   assign members (paths may go through structs: set Position.Y = Position.Y + 1); values see each target: $ and $index (its position in the stream entering set / call)
  call Method(args)   invoke a method the tools expose
Examples:
  nodes | where name == "Player" | set Position = "(0, 1, 0)"   move a node
  nodes | where class == "OmniLight3D" | set props["light_energy"] = props["light_energy"] * 2, props["light_color"] = "#ffcc88"   Godot properties by their snake_case name
  nodes | where name starts-with "Enemy" | set Visible = false   hide every enemy
  nodes | where name starts-with "Enemy" | sort name | set Position.X = $index * 2   spread the matches along X
  godot.resource("res://materials/red.tres") | set props["albedo_color"] = "(1, 0, 0, 1)"   edit a resource file (saved to disk)
  root | call AddChild("Node3D", "Props")   new child node of a class; the receipt's result column holds its $ref
  range(0, 100) | call editor.CreateNode("MeshInstance3D", {name: "Cube" + $, parent: "Props", position: [($ % 10) * 2, 0, floor($ / 10) * 2], mesh: {class: "BoxMesh"}})   bulk create: a 10 x 10 grid of cubes in one call and one undo step (one receipt row per node with its $ref; a long receipt lists the first 50 and counts the rest)
  [["CoinA", 0], ["CoinB", 3]] | call editor.InstantiateScene("res://scenes/enemy.tscn", {name: $[0], parent: "Props", position: [$[1], 1, 0]})   one scene instance per list entry
  nodes | where name starts-with "Cube" | set Scale = Scale * 1.5   vectors and colors compute component-wise
  nodes | where name starts-with "Cube" | set Position = [Position.X, 0, Position.Z] * 2   numeric lists compute like vectors ([x, 0, z] * k); vector functions: length, normalize, distance, dot, lerp
  nodes | where name starts-with "Cube" | sort name | set Position = [10 * cos(2 * pi() * $index / $count), 0, 10 * sin(2 * pi() * $index / $count)]   evenly on a circle of radius 10 ($count = the matches; sin cos atan2 pi deg rad)
  nodes | where name == "Props" | call InstantiateScene("res://scenes/enemy.tscn")   instance a scene as a child
  nodes | where name == "Crate" | call Duplicate()   copy next to it (returns the copy)
  nodes | where name == "Floor" | set Mesh = {$type: "PlaneMesh", Size: "(20, 20)"}   a new embedded resource (mesh, shape, material, environment …) of a concrete class, with its members; a script class_name works too
  nodes | where name == "Crate" | call Reparent(godot.find("Props"))   reparent (keeps the global transform)
  nodes | where name == "Crate" | call MoveChild(0)   reorder among its siblings (-1 = last)
  nodes | where name == "Old" | call Free()   remove a node (undoable, like Delete in the Scene dock)
  editor | set selection = [godot.find("Player")]   change the editor selection
  editor | call SaveScene()   write the edited scene to its .tscn
  editor | call CreateResource("res://materials/blue.tres", "StandardMaterial3D")   new resource file (a Godot class or a script class_name)
  editor | call MoveResource("res://materials/blue.tres", "res://materials/old/blue.tres")   move / rename a file (its .import / .uid sidecars move with it)
  editor | call DeleteResource("res://materials/old/blue.tres")   delete a file and its sidecars
  editor | call NewScene("res://scenes/new.tscn", "Node2D", "Main")   create, save and open a scene (path, root class, root name)
  editor | call OpenScene("res://scenes/level.tscn")   open a scene file
  editor | call Reimport(["res://materials/red.tres"])   re-import files (no argument: full filesystem scan)
  editor | call ClearConsole()   clear the captured editor log
  nodes | where name starts-with "Enemy" | call AddToGroup("enemies")   add nodes to a group (saved with the scene; set groups = [...] replaces them)
  nodes | where name == "Enemy1" | call Connect("tree_exited", godot.find("Player"), "_on_enemy_died")   connect a signal to a method of the target's script (saved with the scene)
  nodes | where name == "Enemy1" | get connections | where method == "_on_enemy_died" | call Disconnect()   remove a connection
  project | set settings["display/window/size/viewport_width"] = 1280   any project setting by its path (saved to project.godot)
  project | call AddInputAction("jump", ["Key:Space", "JoypadButton:A"])   new input action with its events
  inputActions | where name == "jump" | call AddEvent("Key:W")   add an event to an input action (RemoveEvent, set events = [...], set deadzone)
  project | call AddAutoload("Game", "res://scenes/game.gd")   register an autoload singleton
  godot.resource("res://tiles/terrain.tres") | call AddAtlasSource("res://tiles/terrain.png")   add a texture to a TileSet (returns the source id)
  godot.resource("res://tiles/terrain.tres") | get sources | get atlas | call CreateTilesFromTexture()   create tiles in the non-transparent grid cells
  godot.resource("res://ui/theme.tres") | set colors["Button/font_color"] = "#ffcc00", constants["HBoxContainer/separation"] = 8   edit Theme items
  layers | where kind == "3d_physics" and index == 2 | set name = "enemies"   name a layer (saved to project.godot)
  nodes | where name == "Wall" | set layerNames["collision_mask"] = [1, "enemies"]   a layer mask by layer names (numbers for unnamed layers)
  godot.resource("res://materials/water.tres") | set params["speed"] = 2, params["tint"] = "#3388ff"   shader uniforms of a ShaderMaterial (null or call ClearParam(name) reverts one)
  godot.resource("res://meshes/tiles.tres") | call CreateItem("Floor", "res://meshes/floor.tres")   add a MeshLibrary item (set its shapes, navigationMesh … on the returned row)
  nodes | where class == "OmniLight3D" | set name = name.replace("Lamp", "Light")   bulk rename (replace, replace-regex, substring, pad …)
  files | where path == "res://tiles/terrain.png" | set importSettings["mipmaps/generate"] = true   change an import option (re-imports the file; a .wav file loops with the import option edit/loop_mode = 2; on an AudioStreamWAV resource set LoopMode = "Forward", its loop_mode 2 is PingPong)
Editor state: editor | set isPlaying = true / false runs / stops the main scene; editor | call PlayScene("res://…") runs one scene; what the game prints and its errors: rql-query gameLog. editor | call Undo() / Redo() steps the editor's undo history like Ctrl+Z (returns the action; rql-query editor | select undoHistory lists the scene's and the global history); editor | call ReloadScene() reloads the edited scene from its file; NewScene(path, "res://base.tscn") makes an inherited scene.
Baking: a VoxelGI node bakes global illumination with call CallMethod("bake") (set its Size around the level first); LightmapGI has no scriptable bake in Godot (only the editor toolbar's Bake Lightmaps), so say so instead of reporting a bake.
Bulk create: range(start, end) or a list literal streams items, and call editor.CreateNode(class, {…}) / editor.InstantiateScene(path, {…}) runs once per item ($ and $index are the item's); the options are name, parent (a node, its path or ref("Node3D#…"); the scene root by default) and Godot properties of the new node (position, rotation_degrees, scale, mesh, visible …; local, not global_*). Every item is checked before the first node is made; all of them are one undo step. By name, the options stay one object: call editor.CreateNode({className: "Node3D", options: {name: "Pivot"}}).
Object literals build values and resources (meshes, shapes, materials …, script and GDExtension Resource classes too), never nodes (AddChild / CreateNode create those; pass an existing node by godot.find("Name"), path or ref): set Position = {x: 1, y: 2, z: 3}; set Mesh = {$type: "BoxMesh", Size: [1, 2, 1]} builds a new resource of a concrete class. Map entries streamed by get write through their map: … | get colors | where Key starts-with "Button/" | set Value = "#ffffff". Up to 200 targets per call; for an intended bulk change of more objects pass max_targets = the match count (at most 1000): still one all-or-nothing, undoable call. Nodes inside a scene instance take property changes (saved as overrides; the instance gets Editable Children) but are removed, renamed or moved in their own scene.

Query part: the RQL of rql-query (full guide and the sources in its description); godot.resources("Texture2D"), godot.resource(path), godot.find("Name"), ref("Node3D#1234"). Stages: where, select, sort, first N, get <path>, recurse children. Operators: == != < > and or not, contains, starts-with, $ is Node3D, cond ? a : b; conditions are booleans (where Mesh != null, has(Mesh)). Paths a.b.c, list[0], props["key"]. Vectors/colors as "(1, 2, 3)" or "#ff8800"; enums as strings.

RQL (Reflection Query Language, reference: https://ai-game.dev/docs/rql). Change the live Godot editor with an RQL query whose last stage is set or call. All-or-nothing: every target and value is checked before anything is written; a failure changes nothing. Undoable in the editor (Ctrl+Z). Scene edits mark the scene unsaved (save with editor | call SaveScene()); edited resource files (res://….tres) are saved to disk automatically. dry_run=true returns the receipt ($ref, member, old, new) without changing anything. The receipt leaves out assignments that found the value already set; they are counted ("N unchanged assignment(s) not listed"). Do exactly the change requested; a read-only member fails with readonly-member instead of being approximated. Destructive cleanup over what a query finds (Free, DeleteResource, DeleteFolder on matches, not on paths the user named) takes two turns: list what matches with rql-query, show the list to the user and end your turn; delete only after the user confirms in a later message. When you map a misspelt or approximate name to an existing node, resource or member, tell the user which one you used.
  set path = expr, ...   assign members (paths may go through structs: set Position.Y = Position.Y + 1); values see each target: $ and $index (its position in the stream entering set / call)
  call Method(args)   invoke a method the tools expose
Examples:
  nodes | where name == "Player" | set Position = "(0, 1, 0)"   move a node
  nodes | where class == "OmniLight3D" | set props["light_energy"] = props["light_energy"] * 2, props["light_color"] = "#ffcc88"   Godot properties by their snake_case name
  nodes | where name starts-with "Enemy" | set Visible = false   hide every enemy
  nodes | where name starts-with "Enemy" | sort name | set Position.X = $index * 2   spread the matches along X
  godot.resource("res://materials/red.tres") | set props["albedo_color"] = "(1, 0, 0, 1)"   edit a resource file (saved to disk)
  root | call AddChild("Node3D", "Props")   new child node of a class; the receipt's result column holds its $ref
  range(0, 100) | call editor.CreateNode("MeshInstance3D", {name: "Cube" + $, parent: "Props", position: [($ % 10) * 2, 0, floor($ / 10) * 2], mesh: {class: "BoxMesh"}})   bulk create: a 10 x 10 grid of cubes in one call and one undo step (one receipt row per node with its $ref; a long receipt lists the first 50 and counts the rest)
  [["CoinA", 0], ["CoinB", 3]] | call editor.InstantiateScene("res://scenes/enemy.tscn", {name: $[0], parent: "Props", position: [$[1], 1, 0]})   one scene instance per list entry
  nodes | where name starts-with "Cube" | set Scale = Scale * 1.5   vectors and colors compute component-wise
  nodes | where name starts-with "Cube" | set Position = [Position.X, 0, Position.Z] * 2   numeric lists compute like vectors ([x, 0, z] * k); vector functions: length, normalize, distance, dot, lerp
  nodes | where name starts-with "Cube" | sort name | set Position = [10 * cos(2 * pi() * $index / $count), 0, 10 * sin(2 * pi() * $index / $count)]   evenly on a circle of radius 10 ($count = the matches; sin cos atan2 pi deg rad)
  nodes | where name == "Props" | call InstantiateScene("res://scenes/enemy.tscn")   instance a scene as a child
  nodes | where name == "Crate" | call Duplicate()   copy next to it (returns the copy)
  nodes | where name == "Floor" | set Mesh = {$type: "PlaneMesh", Size: "(20, 20)"}   a new embedded resource (mesh, shape, material, environment …) of a concrete class, with its members; a script class_name works too
  nodes | where name == "Crate" | call Reparent(godot.find("Props"))   reparent (keeps the global transform)
  nodes | where name == "Crate" | call MoveChild(0)   reorder among its siblings (-1 = last)
  nodes | where name == "Old" | call Free()   remove a node (undoable, like Delete in the Scene dock)
  editor | set selection = [godot.find("Player")]   change the editor selection
  editor | call SaveScene()   write the edited scene to its .tscn
  editor | call CreateResource("res://materials/blue.tres", "StandardMaterial3D")   new resource file (a Godot class or a script class_name)
  editor | call MoveResource("res://materials/blue.tres", "res://materials/old/blue.tres")   move / rename a file (its .import / .uid sidecars move with it)
  editor | call DeleteResource("res://materials/old/blue.tres")   delete a file and its sidecars
  editor | call NewScene("res://scenes/new.tscn", "Node2D", "Main")   create, save and open a scene (path, root class, root name)
  editor | call OpenScene("res://scenes/level.tscn")   open a scene file
  editor | call Reimport(["res://materials/red.tres"])   re-import files (no argument: full filesystem scan)
  editor | call ClearConsole()   clear the captured editor log
  nodes | where name starts-with "Enemy" | call AddToGroup("enemies")   add nodes to a group (saved with the scene; set groups = [...] replaces them)
  nodes | where name == "Enemy1" | call Connect("tree_exited", godot.find("Player"), "_on_enemy_died")   connect a signal to a method of the target's script (saved with the scene)
  nodes | where name == "Enemy1" | get connections | where method == "_on_enemy_died" | call Disconnect()   remove a connection
  project | set settings["display/window/size/viewport_width"] = 1280   any project setting by its path (saved to project.godot)
  project | call AddInputAction("jump", ["Key:Space", "JoypadButton:A"])   new input action with its events
  inputActions | where name == "jump" | call AddEvent("Key:W")   add an event to an input action (RemoveEvent, set events = [...], set deadzone)
  project | call AddAutoload("Game", "res://scenes/game.gd")   register an autoload singleton
  godot.resource("res://tiles/terrain.tres") | call AddAtlasSource("res://tiles/terrain.png")   add a texture to a TileSet (returns the source id)
  godot.resource("res://tiles/terrain.tres") | get sources | get atlas | call CreateTilesFromTexture()   create tiles in the non-transparent grid cells
  godot.resource("res://ui/theme.tres") | set colors["Button/font_color"] = "#ffcc00", constants["HBoxContainer/separation"] = 8   edit Theme items
  layers | where kind == "3d_physics" and index == 2 | set name = "enemies"   name a layer (saved to project.godot)
  nodes | where name == "Wall" | set layerNames["collision_mask"] = [1, "enemies"]   a layer mask by layer names (numbers for unnamed layers)
  godot.resource("res://materials/water.tres") | set params["speed"] = 2, params["tint"] = "#3388ff"   shader uniforms of a ShaderMaterial (null or call ClearParam(name) reverts one)
  godot.resource("res://meshes/tiles.tres") | call CreateItem("Floor", "res://meshes/floor.tres")   add a MeshLibrary item (set its shapes, navigationMesh … on the returned row)
  nodes | where class == "OmniLight3D" | set name = name.replace("Lamp", "Light")   bulk rename (replace, replace-regex, substring, pad …)
  files | where path == "res://tiles/terrain.png" | set importSettings["mipmaps/generate"] = true   change an import option (re-imports the file; a .wav file loops with the import option edit/loop_mode = 2; on an AudioStreamWAV resource set LoopMode = "Forward", its loop_mode 2 is PingPong)
Editor state: editor | set isPlaying = true / false runs / stops the main scene; editor | call PlayScene("res://…") runs one scene; what the game prints and its errors: rql-query gameLog. editor | call Undo() / Redo() steps the editor's undo history like Ctrl+Z (returns the action; rql-query editor | select undoHistory lists the scene's and the global history); editor | call ReloadScene() reloads the edited scene from its file; NewScene(path, "res://base.tscn") makes an inherited scene.
Baking: a VoxelGI node bakes global illumination with call CallMethod("bake") (set its Size around the level first); LightmapGI has no scriptable bake in Godot (only the editor toolbar's Bake Lightmaps), so say so instead of reporting a bake.
Bulk create: range(start, end) or a list literal streams items, and call editor.CreateNode(class, {…}) / editor.InstantiateScene(path, {…}) runs once per item ($ and $index are the item's); the options are name, parent (a node, its path or ref("Node3D#…"); the scene root by default) and Godot properties of the new node (position, rotation_degrees, scale, mesh, visible …; local, not global_*). Every item is checked before the first node is made; all of them are one undo step. By name, the options stay one object: call editor.CreateNode({className: "Node3D", options: {name: "Pivot"}}).
Object literals build values and resources (meshes, shapes, materials …, script and GDExtension Resource classes too), never nodes (AddChild / CreateNode create those; pass an existing node by godot.find("Name"), path or ref): set Position = {x: 1, y: 2, z: 3}; set Mesh = {$type: "BoxMesh", Size: [1, 2, 1]} builds a new resource of a concrete class. Map entries streamed by get write through their map: … | get colors | where Key starts-with "Button/" | set Value = "#ffffff". Up to 200 targets per call; for an intended bulk change of more objects pass max_targets = the match count (at most 1000): still one all-or-nothing, undoable call. Nodes inside a scene instance take property changes (saved as overrides; the instance gets Editable Children) but are removed, renamed or moved in their own scene.

Query part: the RQL of rql-query (full guide and the sources in its description); godot.resources("Texture2D"), godot.resource(path), godot.find("Name"), ref("Node3D#1234"). Stages: where, select, sort, first N, get <path>, recurse children. Operators: == != < > and or not, contains, starts-with, $ is Node3D, cond ? a : b; conditions are booleans (where Mesh != null, has(Mesh)). Paths a.b.c, list[0], props["key"]. Vectors/colors as "(1, 2, 3)" or "#ff8800"; enums as strings.

## How to Call

Run from the project's root directory:

```powershell
[System.IO.File]::WriteAllText((Join-Path $PWD 'args.json'), @'
{
  "query": "string_value",
  "dry_run": false,
  "max_targets": 0
}
'@)
godot-cli.cmd run-tool rql-mutate --input-file args.json
```

> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `query` | `string` | Yes | RQL query ending with a set or call stage, e.g. nodes | where name == "Player" | set Visible = false |
| `dry_run` | `boolean` | No | true = preview the receipt without changing anything |
| `max_targets` | `integer` | No | Most objects the set / call stage may change (default 200, at most 1000). Raise it to the match count for an intended bulk change of more objects: the call stays all-or-nothing and one undo step. |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "query": {
      "type": "string",
      "description": "RQL query ending with a set or call stage, e.g. nodes | where name == \"Player\" | set Visible = false"
    },
    "dry_run": {
      "type": "boolean",
      "description": "true = preview the receipt without changing anything"
    },
    "max_targets": {
      "type": "integer",
      "description": "Most objects the set / call stage may change (default 200, at most 1000). Raise it to the match count for an intended bulk change of more objects: the call stays all-or-nothing and one undo step."
    }
  },
  "required": [
    "query"
  ]
}
```

## Output

This tool does not return structured output.

