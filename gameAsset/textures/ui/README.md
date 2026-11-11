# UI 素材（Godot 用）

风格与项目里的 3D 资产一致：扁平色块 + 深棕描边 `#3B2E16` + 米色/沙色/军绿调色板，
所有图片都是 **RGBA PNG**（带透明通道），可直接拖进 Godot。

```
UI素材/
├── button/          8 张  btn_{normal,hover,pressed,disabled}.png
│                          btn_accent_{...}.png
├── panel/           4 张  panel.png / panel_dark.png / panel_light.png / titlebar.png
├── progressBar/     5 张  bar_bg.png + bar_fill_{green,red,blue,gold}.png
├── slider/          2 张  slider_track.png / slider_grabber.png
├── scrollBar/       2 张  scroll_track.png / scroll_grabber.png
├── checkBox/        4 张  check_on/off.png, radio_on/off.png
├── tab/             2 张  tab_normal.png / tab_selected.png
├── windowControl/   3 张  close.png / minimize.png / maximize.png
├── icon/           12 张  icon_{gold,oil,ammo,population,attack,defense,speed,
│                          health,time,food,wood,metal}.png
├── cursor/          3 张  cursor_{arrow,attack,move}.png
├── portraitFrame/   1 张  portrait_frame.png
├── tooltip/         1 张  tooltip.png
└── preview.png           全部 47 张的拼图预览
```

## 九宫格（9-slice）参数

Godot 里用 `StyleBoxTexture` 时按下表填 **Texture Margins**（左右/上下相同即可）：

| 文件 | 尺寸 | 建议 Margin |
|---|---|---|
| `button/btn_*.png` | 128 × 64 | **14**（四边） |
| `panel/panel*.png` | 192 × 192 | **18** |
| `panel/titlebar.png` | 256 × 48 | 左右 12 / 上下 8 |
| `progressBar/bar_bg.png`、`bar_fill_*.png` | 256 × 32 | **10** |
| `slider/slider_track.png` | 256 × 24 | 左右 8 / 上下 8 |
| `scrollBar/scroll_track.png` | 24 × 256 | **10** |
| `scrollBar/scroll_grabber.png` | 24 × 64 | **10** |
| `tab/tab_*.png` | 96 × 44 | 左右 12 / 上下 10（**只横向拉伸**） |
| `tooltip/tooltip.png` | 192 × 96 | **14** |
| `portraitFrame/portrait_frame.png` | 128 × 128 | **22** |

不用九宫格（固定尺寸）：`slider/slider_grabber.png`(32²)、`checkBox/*.png`(32²)、
`windowControl/*.png`(32²)、`icon/*.png`(64²)、`cursor/*.png`(64²)。

## Godot 用法示例

**按钮**

```gdscript
var sb := StyleBoxTexture.new()
sb.texture = preload("res://ui/button/btn_normal.png")
sb.set_texture_margin_all(14)
sb.content_margin_left = 18
$Button.add_theme_stylebox_override("normal", sb)
$Button.add_theme_stylebox_override("hover",
        load("res://ui/button/btn_hover.png").to_stylebox())
```

**进度条**

```gdscript
$ProgressBar.add_theme_stylebox_override("background", bg_sb)   # bar_bg.png
$ProgressBar.add_theme_stylebox_override("fill",       fill_sb) # bar_fill_green.png
```

**光标热点**（`Input.set_custom_mouse_cursor` 的 hotspot 参数）

| 文件 | hotspot |
|---|---|
| `cursor/cursor_arrow.png` | (8, 4) |
| `cursor/cursor_attack.png` | (8, 4) |
| `cursor/cursor_move.png` | (32, 32) |

```gdscript
Input.set_custom_mouse_cursor(load("res://ui/cursor/cursor_arrow.png"),
        Input.CURSOR_ARROW, Vector2(8, 4))
```

**图标**：直接当 `TextureRect` / `Button.icon` 用，建议关掉过滤
（`texture_filter = TEXTURE_FILTER_NEAREST`）保持像素质感。

## 配色

| 名称 | 色值 | 用途 |
|---|---|---|
| 描边 | `#3B2E16` | 所有 UI 边框 |
| 米色 | `#EEE7D8` | 面板底 / 背景 |
| 沙色 | `#C9B98A` | 按钮常态 |
| 沙浅 | `#D6C99C` | 按钮悬停 |
| 沙深 | `#A89566` | 按钮按下 |
| 红 | `#C2543E` | 强调按钮 / 危险 |
| 军绿 | `#6C7C4A` | 次要强调 |
| 金 | `#D9A63A` | 资源图标 |
| 灰 | `#9A9A92` | 禁用态 |
