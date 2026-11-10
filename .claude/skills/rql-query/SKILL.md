---
name: rql-query
description: "Query the live Godot editor (scene nodes, resources, files, scenes, classes, console, project settings, input map) with RQL in ONE call: filter, project, sort, count, walk the scene tree. Far fewer calls/tokens than reading nodes one by one."
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---

# RQL / Query

RQL (Reflection Query Language, reference: https://ai-game.dev/docs/rql). Run a read-only RQL query against the live Godot editor and return compact rows. One precise query beats many exploratory calls. Changes need rql-mutate.

RQL: source | stage | stage ...
Sources: nodes (every node of the edited scene, depth-first, root included), root (the edited scene root), selection, scenes (open scenes), files (every res:// file the editor knows; on request size (bytes), importedSize, deps, missingDeps, usedBy (scenes / resources that load it) and importSettings["option"]), folders (path fileCount folderCount empty), classes (Godot classes, ClassDB), console (captured editor log entries), gameLog (what the game run from the editor printed: Sequence LogType Message StackTrace), editor (editor state, undoHistory / redoHistory, and operations), project (project settings: settings["section/key"], name, mainScene; input map, autoload and global group calls), settings (every project setting: name section value type changed), inputActions (name events deadzone builtIn), autoloads (name path singleton order), groups (name nodes count global), layers (kind index name: the project's layer names), tools (every MCP tool of this editor, hidden ones included: name title visible description; a hidden tool is not offered to this client: to use one, ask the user to show it in the AI Game Developer dock). Resources: godot.resources("StandardMaterial3D") or godot.resources("Texture2D", "res://art"), godot.resource("res://materials/red.tres"). ref("Node3D#1234") resolves a $ref; godot.find("Player") or godot.find("Level/Player") a node by unique name or path below the scene root; godot.group("enemies") the nodes of a group; godot.callable("Player", "_on_hit") a Callable.
Stages:
  where <predicate>   filter (a boolean condition: where Mesh != null, where has(Mesh), where children.count() > 0)
  select a b.c alias=(expr)   project columns (wrap computed expressions in parentheses)
  sort <expr> [desc], ...   stable sort
  first N | skip N | count
  get <path>   replace each item by a member (lists are flattened)
  recurse children [max-depth=N] [include-self=false]   walk the scene tree (input items included by default)
  distinct [expr] | sum expr | min expr | max expr | avg expr
  group-by expr   rows key|count; group-by a, b (or kind=class) gives one column per key, then count; group-by class | select key, count, avgY = items.avg(Position.Y)
Expressions: == != < <= > >= and or not + - * / %; contains, starts-with, ends-with (case-insensitive); matches "regex"; x in [a, b]; x is null; x is not null; $ is Node3D (includes subclasses); has(member). Conditional: cond ? a : b, e.g. select name kind=($ is MeshInstance3D ? "mesh" : "other"). Text: replace(s, from, to) (case-insensitive), replace-regex(s, pattern, repl) ($1 = group 1), substring(s, start[, length]), split(s, sep), trim(s), pad(v, width[, fill]) — also as methods: name.replace("Old", "New"), name.substring(0, 4). Literals: numbers, "strings", true/false/null, lists [a, b], objects {x: 1, y: 2}; groups == ["enemies"] compares a list.
Paths: a.b.c, list[0], map["key"]; .member on a list maps over its elements. List methods: .any(pred) .all(pred) .count([pred]) .sum(e) .min(e) .max(e) .first([pred]); inside them bare names refer to the element, falling back to the outer item.
Meta: $ (current item), $ref (stable id such as Node3D#1234), $type, $path, $index, $depth. Enums compare to strings; == on strings is case-sensitive.
Godot: every node has name, path (below the scene root, "." = root), class (the Godot class, e.g. "OmniLight3D"), parent, owner, children (list), childCount, index, script (res:// path), groups (list) and id; signals, connections (persistent: signal target method flags), incoming and overrides (how a scene instance differs from its scene file: node property value template) are read on request (select connections, get overrides). Members are the C# properties (Position, Rotation, Scale, Visible, Text …), matched case-insensitively, so position works too; Rotation is in radians, RotationDegrees in degrees. Every Godot property, including GDScript script variables (in the editor: @export ones, every one of a @tool script) and GDExtension properties, is in props["snake_name"] (e.g. props["light_energy"], props["speed"] on a scripted node); a Godot array lists the values it holds (get props["items"] | where props["price"] > 10). Vectors / colors print as (x, y, z); write "(1, 2, 3)", [1, 2, 3] or {x: 1, y: 2, z: 3}; colors also "#ff8800". Vector2/3/4, their integer variants (whole results only) and Color compute component-wise (set Scale = Scale * 1.5, Position + [0, 1, 0]); Quaternion, Basis and transforms do not. Lists of numbers compute too ([Position.X, 0, Position.Z] * 2), and length(v), normalize(v), distance(a, b), dot(a, b), lerp(a, b, t) take vectors or lists (distance(Position, godot.find("Player").Position) < 5). Units: meters in 3D, pixels in 2D. A resource path "res://…" works wherever a resource is expected, a node path or name wherever a node is expected. Freed nodes read as null. Input events are text: "Key:Space", "Key:Ctrl+S", "PhysicalKey:W", "Mouse:Left", "JoypadButton:A", "JoypadMotion:LeftX-". A TileSet has sources (id kind atlas texture tileCount), physicsLayers, terrainSets, customDataLayers; an atlas source has tiles (coords size data). A Theme has maps keyed "ControlType/item": colors, constants, fonts, fontSizes, icons, styleboxes. A ShaderMaterial has uniforms (name type default value overridden) and params["uniform"] (typed by the shader; null reverts to the default); a MeshLibrary has items (id name mesh meshTransform shapes navigationMesh preview). Layer masks by name: layerNames["collision_mask"] on any object with layer properties is a list of layer names (a number for an unnamed layer). Callables are "Player::_on_hit" (target::method); RIDs are read-only "RID(…)"; a Projection is 16 numbers. Navigate with paths or get; select only projects output columns.
Results come back as compact rows (header line, then col|col). Long results are truncated — refine with where/first or use count. Unknown members produce warnings with suggestions (in rql-mutate a misspelt member that leaves no target is an error); rql-schema(type) lists a type's members and Godot properties.

Language rules:
- Conditions are booleans. where, and / or / not, ?: and the predicates of .any() .all() .count() .first() take a comparison or has(), never a bare value: where material != null (or has(material)), where count > 0, where name != "", where len(tags) > 0. A number, string, object or list there fails with type-mismatch; null counts as false.
- cond ? a : b (the same as if(cond, a, b)) picks a value and evaluates only the taken branch: set y = (y < 0 ? 0 : y). Inside select, wrap computed values in parentheses: select kind = (intensity > 2 ? "bright" : "dim").
- Lists compare by value, in order: tags == ["a", "b"], x in [[1, 2], [3, 4]]; distinct and group-by merge equal lists.
- group-by gives rows with key (or one column per key), count and the group's items: group-by kind | select key, count, avg = items.avg(weight); also items.sum(e), items.min(e), items.max(e), items.count(pred), items.first().name. Group by the item's type with $type (group-by $type); a bare type is a member that most items do not have.
- Receipts list only real changes: a set row whose value stayed the same is left out and counted as unchanged, so no rows with N unchanged means the values already matched.
- call Method(args) runs the method of each item (the items must be objects). call source.Method(args) runs the method of that one source object once per stream item, with $, $index, $path and $depth in the arguments being the item's; every call counts against the target limit, and every argument is checked before the first call (a dry run checks without calling).
- Bulk create: range(start, end[, step]) streams numbers (end excluded), and a list literal streams its elements, so one call creates many objects: range(0, 150) | call editor.CreateNode("Node3D", {name: "Tile" + $, position: [($ % 15) * 2, 0, floor($ / 15) * 2]}) builds a 15-wide grid, [["Lamp", [0, 2, 0]], ["Sun", [6, 4, 0]]] | call editor.CreateNode("OmniLight3D", {name: $[0], position: $[1]}) one object per entry. The receipt has one row per call with the $ref of the created object.
- Vectors and colors the host marks as component-wise compute component by component: set Scale = Scale * 1.5, Position + [0, 1, 0], Position - Offset, -Velocity, v / 2. Adding a number to a vector is a type-mismatch (add [x, y, z] instead); quaternions and other types without that mark stay type-mismatch. Lists of numbers are vectors too: [x, 0, z] * k, [1, 2] + [3, 4]. Vector functions work on both: length(v) (or v.length(), magnitude), normalize(v), distance(a, b), dot(a, b), lerp(a, b, t); len(x) stays the size of a string or list.
- Trigonometry is in radians: sin, cos, tan, asin, acos, atan, atan2(y, x), pi(), deg(x), rad(x); trigonometric results are rounded (sin(pi()) is 0). In a set value or call argument $count is the number of items entering the stage ($index runs 0 to $count - 1), so set position = [5 * cos(2 * pi() * $index / $count), 0, 5 * sin(2 * pi() * $index / $count)] lays the items out on a circle of radius 5.
- A missing map entry reads as null: props["k"] == null and props["k"] is null are true without the entry, and has(props["k"]) tests whether it exists. A misspelt member is not null: it stays unknown and is reported.
- Arguments by name: call Method({name: value, ...}) with one object literal that no overload takes as a whole binds each key to the parameter of that name; parameters left out keep their defaults, and an unknown name fails listing the parameters: range(0, 10) | call editor.CreateNode({className: "Node3D", options: {name: "Tile" + $, position: [$ * 2, 0, 0]}}). Engine objects (scene objects, nodes, actors) are never built from an object literal: pass an existing one, e.g. ref("Type#id"). Value-like assets and owned subobjects that the host marks are built from a typed literal: set mesh = {$type: "TypeName", size: 2}.


Examples:
  nodes | where name contains "Enemy" | select $ref name class path
  nodes | where $ is Node3D and Position.Y > 0 | select name Position
  nodes | where class == "OmniLight3D" | select name props["light_energy"] props["light_color"]
  root | recurse children | where childCount == 0 | select path class
  godot.resources("StandardMaterial3D") | select path props["albedo_color"]
  files | where ext == "tscn" | select path
  scenes | select name path active
  classes | where parent == "Light3D" | select name
  console | where logType == "Error" | sort sequence desc | first 5 | select message
  editor | select isPlaying editedScene selection
  godot.group("enemies") | select name path
  nodes | where name == "Player" | get incoming | select source signal method
  settings | where section == "display" and changed | select name value
  inputActions | where builtIn == false | select name events
  autoloads | select name path singleton
  godot.resources("TileSet") | get sources | select id kind texture tileCount
  godot.resource("res://materials/water.tres") | get uniforms | select name type default value overridden
  nodes | where class == "StaticBody3D" | select name layerNames["collision_layer"] layerNames["collision_mask"]
  layers | where kind == "3d_physics" and name != "" | select index name
  godot.resources("MeshLibrary") | get items | select id name mesh
  nodes | group-by class, scripted=(script is not null)
  nodes | where $ is Node3D | group-by class | select key, count, avgY = items.avg(Position.Y)
  nodes | where $ is Node3D | sort distance(Position, [0, 0, 0]) | first 3 | select name dist = (distance(Position, [0, 0, 0]))
  nodes | where $ is Node3D | select name kind=($ is MeshInstance3D ? "mesh" : $ is Light3D ? "light" : "other")
  files | where type contains "Material" and usedBy.count() == 0 | select path
  files | where missingDeps.count() > 0 | select path missingDeps
  files | where not (path starts-with "res://addons/") | sort size desc | first 5 | select path size importedSize
  nodes | where SceneFilePath != "" and overrides.count() > 0 | select name overrides
  editor | select undoHistory redoHistory

RQL (Reflection Query Language, reference: https://ai-game.dev/docs/rql). Run a read-only RQL query against the live Godot editor and return compact rows. One precise query beats many exploratory calls. Changes need rql-mutate.

RQL: source | stage | stage ...
Sources: nodes (every node of the edited scene, depth-first, root included), root (the edited scene root), selection, scenes (open scenes), files (every res:// file the editor knows; on request size (bytes), importedSize, deps, missingDeps, usedBy (scenes / resources that load it) and importSettings["option"]), folders (path fileCount folderCount empty), classes (Godot classes, ClassDB), console (captured editor log entries), gameLog (what the game run from the editor printed: Sequence LogType Message StackTrace), editor (editor state, undoHistory / redoHistory, and operations), project (project settings: settings["section/key"], name, mainScene; input map, autoload and global group calls), settings (every project setting: name section value type changed), inputActions (name events deadzone builtIn), autoloads (name path singleton order), groups (name nodes count global), layers (kind index name: the project's layer names), tools (every MCP tool of this editor, hidden ones included: name title visible description; a hidden tool is not offered to this client: to use one, ask the user to show it in the AI Game Developer dock). Resources: godot.resources("StandardMaterial3D") or godot.resources("Texture2D", "res://art"), godot.resource("res://materials/red.tres"). ref("Node3D#1234") resolves a $ref; godot.find("Player") or godot.find("Level/Player") a node by unique name or path below the scene root; godot.group("enemies") the nodes of a group; godot.callable("Player", "_on_hit") a Callable.
Stages:
  where <predicate>   filter (a boolean condition: where Mesh != null, where has(Mesh), where children.count() > 0)
  select a b.c alias=(expr)   project columns (wrap computed expressions in parentheses)
  sort <expr> [desc], ...   stable sort
  first N | skip N | count
  get <path>   replace each item by a member (lists are flattened)
  recurse children [max-depth=N] [include-self=false]   walk the scene tree (input items included by default)
  distinct [expr] | sum expr | min expr | max expr | avg expr
  group-by expr   rows key|count; group-by a, b (or kind=class) gives one column per key, then count; group-by class | select key, count, avgY = items.avg(Position.Y)
Expressions: == != < <= > >= and or not + - * / %; contains, starts-with, ends-with (case-insensitive); matches "regex"; x in [a, b]; x is null; x is not null; $ is Node3D (includes subclasses); has(member). Conditional: cond ? a : b, e.g. select name kind=($ is MeshInstance3D ? "mesh" : "other"). Text: replace(s, from, to) (case-insensitive), replace-regex(s, pattern, repl) ($1 = group 1), substring(s, start[, length]), split(s, sep), trim(s), pad(v, width[, fill]) — also as methods: name.replace("Old", "New"), name.substring(0, 4). Literals: numbers, "strings", true/false/null, lists [a, b], objects {x: 1, y: 2}; groups == ["enemies"] compares a list.
Paths: a.b.c, list[0], map["key"]; .member on a list maps over its elements. List methods: .any(pred) .all(pred) .count([pred]) .sum(e) .min(e) .max(e) .first([pred]); inside them bare names refer to the element, falling back to the outer item.
Meta: $ (current item), $ref (stable id such as Node3D#1234), $type, $path, $index, $depth. Enums compare to strings; == on strings is case-sensitive.
Godot: every node has name, path (below the scene root, "." = root), class (the Godot class, e.g. "OmniLight3D"), parent, owner, children (list), childCount, index, script (res:// path), groups (list) and id; signals, connections (persistent: signal target method flags), incoming and overrides (how a scene instance differs from its scene file: node property value template) are read on request (select connections, get overrides). Members are the C# properties (Position, Rotation, Scale, Visible, Text …), matched case-insensitively, so position works too; Rotation is in radians, RotationDegrees in degrees. Every Godot property, including GDScript script variables (in the editor: @export ones, every one of a @tool script) and GDExtension properties, is in props["snake_name"] (e.g. props["light_energy"], props["speed"] on a scripted node); a Godot array lists the values it holds (get props["items"] | where props["price"] > 10). Vectors / colors print as (x, y, z); write "(1, 2, 3)", [1, 2, 3] or {x: 1, y: 2, z: 3}; colors also "#ff8800". Vector2/3/4, their integer variants (whole results only) and Color compute component-wise (set Scale = Scale * 1.5, Position + [0, 1, 0]); Quaternion, Basis and transforms do not. Lists of numbers compute too ([Position.X, 0, Position.Z] * 2), and length(v), normalize(v), distance(a, b), dot(a, b), lerp(a, b, t) take vectors or lists (distance(Position, godot.find("Player").Position) < 5). Units: meters in 3D, pixels in 2D. A resource path "res://…" works wherever a resource is expected, a node path or name wherever a node is expected. Freed nodes read as null. Input events are text: "Key:Space", "Key:Ctrl+S", "PhysicalKey:W", "Mouse:Left", "JoypadButton:A", "JoypadMotion:LeftX-". A TileSet has sources (id kind atlas texture tileCount), physicsLayers, terrainSets, customDataLayers; an atlas source has tiles (coords size data). A Theme has maps keyed "ControlType/item": colors, constants, fonts, fontSizes, icons, styleboxes. A ShaderMaterial has uniforms (name type default value overridden) and params["uniform"] (typed by the shader; null reverts to the default); a MeshLibrary has items (id name mesh meshTransform shapes navigationMesh preview). Layer masks by name: layerNames["collision_mask"] on any object with layer properties is a list of layer names (a number for an unnamed layer). Callables are "Player::_on_hit" (target::method); RIDs are read-only "RID(…)"; a Projection is 16 numbers. Navigate with paths or get; select only projects output columns.
Results come back as compact rows (header line, then col|col). Long results are truncated — refine with where/first or use count. Unknown members produce warnings with suggestions (in rql-mutate a misspelt member that leaves no target is an error); rql-schema(type) lists a type's members and Godot properties.

Language rules:
- Conditions are booleans. where, and / or / not, ?: and the predicates of .any() .all() .count() .first() take a comparison or has(), never a bare value: where material != null (or has(material)), where count > 0, where name != "", where len(tags) > 0. A number, string, object or list there fails with type-mismatch; null counts as false.
- cond ? a : b (the same as if(cond, a, b)) picks a value and evaluates only the taken branch: set y = (y < 0 ? 0 : y). Inside select, wrap computed values in parentheses: select kind = (intensity > 2 ? "bright" : "dim").
- Lists compare by value, in order: tags == ["a", "b"], x in [[1, 2], [3, 4]]; distinct and group-by merge equal lists.
- group-by gives rows with key (or one column per key), count and the group's items: group-by kind | select key, count, avg = items.avg(weight); also items.sum(e), items.min(e), items.max(e), items.count(pred), items.first().name. Group by the item's type with $type (group-by $type); a bare type is a member that most items do not have.
- Receipts list only real changes: a set row whose value stayed the same is left out and counted as unchanged, so no rows with N unchanged means the values already matched.
- call Method(args) runs the method of each item (the items must be objects). call source.Method(args) runs the method of that one source object once per stream item, with $, $index, $path and $depth in the arguments being the item's; every call counts against the target limit, and every argument is checked before the first call (a dry run checks without calling).
- Bulk create: range(start, end[, step]) streams numbers (end excluded), and a list literal streams its elements, so one call creates many objects: range(0, 150) | call editor.CreateNode("Node3D", {name: "Tile" + $, position: [($ % 15) * 2, 0, floor($ / 15) * 2]}) builds a 15-wide grid, [["Lamp", [0, 2, 0]], ["Sun", [6, 4, 0]]] | call editor.CreateNode("OmniLight3D", {name: $[0], position: $[1]}) one object per entry. The receipt has one row per call with the $ref of the created object.
- Vectors and colors the host marks as component-wise compute component by component: set Scale = Scale * 1.5, Position + [0, 1, 0], Position - Offset, -Velocity, v / 2. Adding a number to a vector is a type-mismatch (add [x, y, z] instead); quaternions and other types without that mark stay type-mismatch. Lists of numbers are vectors too: [x, 0, z] * k, [1, 2] + [3, 4]. Vector functions work on both: length(v) (or v.length(), magnitude), normalize(v), distance(a, b), dot(a, b), lerp(a, b, t); len(x) stays the size of a string or list.
- Trigonometry is in radians: sin, cos, tan, asin, acos, atan, atan2(y, x), pi(), deg(x), rad(x); trigonometric results are rounded (sin(pi()) is 0). In a set value or call argument $count is the number of items entering the stage ($index runs 0 to $count - 1), so set position = [5 * cos(2 * pi() * $index / $count), 0, 5 * sin(2 * pi() * $index / $count)] lays the items out on a circle of radius 5.
- A missing map entry reads as null: props["k"] == null and props["k"] is null are true without the entry, and has(props["k"]) tests whether it exists. A misspelt member is not null: it stays unknown and is reported.
- Arguments by name: call Method({name: value, ...}) with one object literal that no overload takes as a whole binds each key to the parameter of that name; parameters left out keep their defaults, and an unknown name fails listing the parameters: range(0, 10) | call editor.CreateNode({className: "Node3D", options: {name: "Tile" + $, position: [$ * 2, 0, 0]}}). Engine objects (scene objects, nodes, actors) are never built from an object literal: pass an existing one, e.g. ref("Type#id"). Value-like assets and owned subobjects that the host marks are built from a typed literal: set mesh = {$type: "TypeName", size: 2}.


Examples:
  nodes | where name contains "Enemy" | select $ref name class path
  nodes | where $ is Node3D and Position.Y > 0 | select name Position
  nodes | where class == "OmniLight3D" | select name props["light_energy"] props["light_color"]
  root | recurse children | where childCount == 0 | select path class
  godot.resources("StandardMaterial3D") | select path props["albedo_color"]
  files | where ext == "tscn" | select path
  scenes | select name path active
  classes | where parent == "Light3D" | select name
  console | where logType == "Error" | sort sequence desc | first 5 | select message
  editor | select isPlaying editedScene selection
  godot.group("enemies") | select name path
  nodes | where name == "Player" | get incoming | select source signal method
  settings | where section == "display" and changed | select name value
  inputActions | where builtIn == false | select name events
  autoloads | select name path singleton
  godot.resources("TileSet") | get sources | select id kind texture tileCount
  godot.resource("res://materials/water.tres") | get uniforms | select name type default value overridden
  nodes | where class == "StaticBody3D" | select name layerNames["collision_layer"] layerNames["collision_mask"]
  layers | where kind == "3d_physics" and name != "" | select index name
  godot.resources("MeshLibrary") | get items | select id name mesh
  nodes | group-by class, scripted=(script is not null)
  nodes | where $ is Node3D | group-by class | select key, count, avgY = items.avg(Position.Y)
  nodes | where $ is Node3D | sort distance(Position, [0, 0, 0]) | first 3 | select name dist = (distance(Position, [0, 0, 0]))
  nodes | where $ is Node3D | select name kind=($ is MeshInstance3D ? "mesh" : $ is Light3D ? "light" : "other")
  files | where type contains "Material" and usedBy.count() == 0 | select path
  files | where missingDeps.count() > 0 | select path missingDeps
  files | where not (path starts-with "res://addons/") | sort size desc | first 5 | select path size importedSize
  nodes | where SceneFilePath != "" and overrides.count() > 0 | select name overrides
  editor | select undoHistory redoHistory

## How to Call

Run from the project's root directory:

```powershell
[System.IO.File]::WriteAllText((Join-Path $PWD 'args.json'), @'
{
  "query": "string_value"
}
'@)
godot-cli.cmd run-tool rql-query --input-file args.json
```

> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

## Input

| Name | Type | Required | Description |
|------|------|----------|-------------|
| `query` | `string` | Yes | RQL query, e.g. nodes | where $ is Node3D | select $ref name Position |

### Input JSON Schema

```json
{
  "type": "object",
  "properties": {
    "query": {
      "type": "string",
      "description": "RQL query, e.g. nodes | where $ is Node3D | select $ref name Position"
    }
  },
  "required": [
    "query"
  ]
}
```

## Output

This tool does not return structured output.

