# Godot 接入说明（找回 3渲2 的描边与色阶）

## 为什么会丢

Blender 里的效果由三部分组成，glTF/FBX **一个都带不过去**：

| Blender 里的东西 | 实际是什么 | 导出后 |
|---|---|---|
| 描边 | **Solidify 修改器**（反向外壳，material_offset=1）+ 背面剔除的自发光材质 | 修改器不是网格数据，默认不烘焙 → **完全没有几何** |
| 色阶（3渲2） | `Diffuse BSDF → Shader to RGB → ColorRamp` | `Shader to RGB` 是 EEVEE 专用节点，glTF 无法表达 → 只剩 base color 平涂 |
| 描边颜色/宽度 | 修改器厚度 0.016~0.022 m + 材质 | 若勾了"应用修改器"，壳会导出成第 2 个材质，但会变成 PBR 自发光，且宽度按米算，模型一缩小就看不见 |

所以到 Godot 里看到的是：**一个平涂的、没有描边的模型**。

## 正确做法：在 Godot 里重建（比烘焙更灵活）

用 `next_pass` 做描边是 Godot 的标准做法，宽度可调、不受模型缩放影响，也不用改 Blender 工程。

### 方案 A：不用写代码（最快）

1. 选中 `MeshInstance3D` → `Surface Material Override` 新建 `StandardMaterial3D`
2. 贴图槽 `Albedo → Texture` 选模型对应的图集 PNG
3. `Shading Mode = Per-Vertex`（或 `Unshaded` + 自己压暗），`Diffuse Mode = Toon`，`Specular Mode = Disabled`
4. 展开 `Next Pass` → 新建 `StandardMaterial3D`：
   - `Shading Mode = Unshaded`
   - `Albedo → Color = #3B2E16`
   - `Cull Mode = Front`（只渲染背面）
   - `Grow = Enabled`，`Grow Amount = 0.02`

### 方案 B：用附带 shader（和 Blender 观感一致）

本目录两个文件：

- `toon_cel.gdshader` —— 平涂 + 三段硬边色阶（分界默认 0.42 / 0.72，与 Blender 的 ColorRamp 对齐）
- `toon_outline.gdshader` —— 反向外壳描边，`width` 默认 0.018，`screen_uniform` 可让粗细不随距离变化

步骤：

1. 把两个 `.gdshader` 拷进项目（如 `res://shaders/`）
2. 新建 `ShaderMaterial` → Shader 选 `toon_cel.gdshader` → `albedo_tex` 指定图集 PNG
3. 把它设为 `MeshInstance3D` 的 `Surface Material Override`（模型只有一个材质槽）
4. 再新建 `ShaderMaterial` → Shader 选 `toon_outline.gdshader`，设成上一步材质的 **Next Pass**

## 注意事项

- **缩放**：`width` 是模型空间单位。模型 1 单位 = 1 米，若在 Godot 里把模型缩放到 0.1，描边会变成 0.0018 米 → 看不见。这种情况把 `width` 按缩放倍数放大，或开 `screen_uniform`。
- **骨骼模型（步兵）**：`toon_outline.gdshader` 的顶点外推对蒙皮网格通常没问题；若发现描边跟着骨骼乱抖，改用方案 A 的 `StandardMaterial3D`（`Grow` 由引擎处理蒙皮，最稳）。
- **发射物/炮口闪光**：`tank_Flash` / `heli_Flash` 这类自发光部件不要加描边（Blender 里也没加），单独给一个 unshaded 材质即可。
- **动画**：导出时用 glTF + `Animation Mode = NLA Tracks`，会得到 `Idle` / `Move` / `Fire` 三个 AnimationPlayer 片段；步兵是骨骼动画，三个 clip 在 Armature 上。
- **材质槽数量**：如果导出时勾选了"应用修改器"，mesh 会有 2 个 surface（0 = 本体，1 = 描边壳）。这时可以只给 surface 0 覆盖材质，surface 1 直接删掉/忽略，描边统一交给 next_pass。
