---
name: godot-rql
description: |-
  How to read and change anything in the Godot editor with RQL (rql-query / rql-mutate / rql-schema): the query
  language, operators, sources (scene nodes, resources, files, scenes, classes, console logs, editor state), editor
  operations (add / duplicate / reparent / remove nodes, scenes, resource files, selection, play) and what installed
  extensions add.
metadata:
  generated-by: godot-mcp
  ai-game-dev: exclude
---
# RQL — query and change the Godot editor

RQL is one pipeline language for the whole editor: find nodes and resources, read members and Godot properties, filter,
aggregate, and change them in one call. Prefer one precise query over many exploratory calls.

- `rql-query` — read-only queries, compact table results.
- `rql-mutate` — queries ending with `set` / `call`; all-or-nothing, undoable (Ctrl+Z), `dry_run: true` previews. Scene
  edits mark the scene unsaved (`editor | call SaveScene()` writes the `.tscn`); edited resource files are saved.
- `rql-schema` — sources and the node classes in the scene; `rql-schema(type)` lists one type's members, methods and
  Godot properties.

```bash
godot-cli run-tool rql-query --input '{"query": "nodes | where name contains \"Enemy\" | select $ref name"}'
godot-cli run-tool rql-mutate --input '{"query": "nodes | where name == \"Player\" | set Visible = false"}'
```

## Language

```
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

```

## Examples

```
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
```

## Changing things (rql-mutate)

```
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
```

## Sources

| Source | What |
|---|---|
| `autoloads` | autoloads (singletons) in load order: name path singleton order; call Remove |
| `classes` | Godot classes (ClassDB): name parent canInstantiate isNode isResource |
| `console` | captured editor log lines, oldest first (last 1000): Sequence LogType Message Timestamp StackTrace |
| `editor` | editor state (isPlaying, playingScene, editedScene, selection, godotVersion, undoHistory, redoHistory) and operations: CreateNode, InstantiateScene (bulk create: range(0, N) \| call editor.CreateNode(class, {name, parent, position …})), NewScene, OpenScene, ReloadScene, SaveScene, SaveAllScenes, PlayScene, StopScene, Undo, Redo, CreateResource, MoveResource, DeleteResource, CreateFolder, DeleteFolder, Reimport, ClearConsole (rql-schema "editor" for signatures) |
| `files` | every file the editor filesystem knows (at most 20000): path name dir ext type uid; on request size (bytes), importedSize (bytes of its import outputs, every platform variant), deps (what it loads), missingDeps (broken references), usedBy (the files that load it or name it as a res:// string) and importSettings["option"] (writable) |
| `folders` | every folder the editor filesystem knows: path name fileCount folderCount empty (empty folders: folders \| where empty; delete one with editor \| call DeleteFolder(path)) |
| `gameLog` | what the game run from the editor (PlayScene / isPlaying) printed, its errors and warnings, oldest first (last 2000): Sequence LogType Message StackTrace (the console source is the editor's own log) |
| `groups` | node groups of the edited scene and the project's global groups: name nodes count global description |
| `inputActions` | the input map: name events deadzone builtIn (events as "Key:W", "Mouse:Left", "JoypadButton:A", "JoypadMotion:LeftX-"); calls AddEvent, RemoveEvent, Remove |
| `layers` | the project's layers (Project Settings → Layer Names): kind index bit name setting; set name renames one (objects read and write their masks by these names in layerNames["…"]) |
| `nodes` | every node of the edited scene, depth-first, root first (internal children excluded) |
| `project` | project settings (project.godot): settings["section/key"], name, mainScene; calls SetSetting, ClearSetting, AddInputAction, RemoveInputAction, AddInputEvent, RemoveInputEvent, AddAutoload, RemoveAutoload, AddGlobalGroup, RemoveGlobalGroup |
| `root` | the root node of the edited scene |
| `scenes` | scenes open in the editor: path name active root; calls Open, Save |
| `selection` | nodes selected in the editor |
| `settings` | every project setting: name section value type changed (write with project \| set settings["<name>"] = …) |
| `tools` | every MCP tool of this editor, hidden ones included: name title visible description; a tool with visible false is not offered to this client: to use it, ask the user to show it in the AI Game Developer dock (or godot-cli configure) |
| `godot.callable(…)` | godot.callable(target, method): a Callable of a method on a node (path or node), a resource (res://) or Class#id, e.g. godot.callable("Player", "_on_hit") |
| `godot.find(…)` | godot.find(name): the node with that unique name or path below the scene root ("Level/Player"), e.g. call Reparent(godot.find("Props")) |
| `godot.group(…)` | godot.group(name): the nodes of the edited scene in a group, e.g. godot.group("enemies") \| set Visible = false |
| `godot.resource(…)` | godot.resource(path): the resource at a res:// or uid:// path, or null |
| `godot.resources(…)` | godot.resources(type, folder?): resources of a Godot class (subclasses included) below an optional res:// folder, e.g. "Texture2D" (at most 200, loaded as needed) |

## Installed RQL modules

- `com.IvanMurzak.Godot.MCP.Editor.Rql.Modules.GodotCoreModule`
- `com.IvanMurzak.Godot.MCP.Editor.Rql.Modules.ToolTypesModule`
- `com.IvanMurzak.Godot.MCP.Editor.Rql.Modules.TestHooksModule`


> CLI installation and usage: see the `godot-cli` skill.
> If this task needs unavailable asset-generation or Blender tools, consult `ai-game-dev-app` for an optional integrated workflow.

