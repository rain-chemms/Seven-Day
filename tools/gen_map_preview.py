"""从地图 JSON 生成预览图（六边形网格），输出 mapPreview_<mapId>.png。

用法：
    python tools/gen_map_preview.py <输出目录>

配色与游戏内地形一致：
    g 草地  d 沙地  r 岩石  n 雪地  w 水面  （dirt 用 'd' 之外的旧字符也兼容）
"""
import json
import math
import os
import sys

from PIL import Image, ImageDraw

W, H = 800, 450
BG = (246, 238, 220, 255)
BORDER = (59, 46, 22, 255)

TERRAIN = {
    "g": (127, 166, 80, 255),    # 草地
    "d": (217, 197, 140, 255),   # 沙地
    "r": (154, 160, 160, 255),   # 岩石
    "n": (232, 238, 242, 255),   # 雪地
    "w": (121, 184, 214, 255),   # 水面
    "e": (176, 138, 94, 255),    # 泥地（兼容）
}
# 游戏里 'd' 是 dirt（泥地），'s' 是 sand（沙地）
TERRAIN = {
    "g": (127, 166, 80, 255),
    "s": (217, 197, 140, 255),
    "d": (176, 138, 94, 255),
    "r": (154, 160, 160, 255),
    "n": (232, 238, 242, 255),
    "w": (121, 184, 214, 255),
}


def hex_points(cx, cy, size):
    pts = []
    for i in range(6):
        a = math.radians(60 * i - 30)
        pts.append((cx + size * math.cos(a), cy + size * math.sin(a)))
    return pts


def render(map_path, out_path):
    data = json.load(open(map_path, encoding="utf-8"))
    rows = data["terrainRows"]
    height = data["height"]
    width = data["width"]

    size = 30.0
    sq3 = math.sqrt(3.0)

    cells = []
    for r in range(height):
        row = rows[r] if r < len(rows) else ""
        start_q = -(r // 2)
        for c in range(width):
            ch = row[c] if c < len(row) else "g"
            q = start_q + c
            x = sq3 * size * (q + r / 2.0)
            y = 1.5 * size * r
            cells.append((x, y, ch))

    xs = [c[0] for c in cells]
    ys = [c[1] for c in cells]
    cx = (min(xs) + max(xs)) / 2.0
    cy = (min(ys) + max(ys)) / 2.0

    img = Image.new("RGBA", (W * 2, H * 2), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([8, 8, W * 2 - 8, H * 2 - 8], radius=28, fill=BG, outline=BORDER, width=6)

    for x, y, ch in cells:
        px = (x - cx) * 2 + W
        py = (y - cy) * 2 + H
        pts = hex_points(px, py, size * 2 * 0.96)
        d.polygon(pts, fill=TERRAIN.get(ch, TERRAIN["g"]), outline=(59, 46, 22, 160), width=2)

    # 双方指挥部标记
    def mark(coord, color):
        q, r = coord
        x = sq3 * size * (q + r / 2.0)
        y = 1.5 * size * r
        px = (x - cx) * 2 + W
        py = (y - cy) * 2 + H
        d.ellipse([px - 13, py - 13, px + 13, py + 13], fill=color, outline=BORDER, width=3)

    mark(data.get("playerHq", [0, 0]), (61, 123, 214, 255))
    mark(data.get("enemyHq", [0, 0]), (194, 84, 62, 255))

    img = img.resize((W, H), Image.LANCZOS)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    img.save(out_path)
    print("wrote", out_path)


if __name__ == "__main__":
    out_dir = sys.argv[1] if len(sys.argv) > 1 else "gameAsset/textures/mapPreview"
    maps_dir = os.path.join(os.path.dirname(__file__), "..", "data", "maps")
    for name in sorted(os.listdir(maps_dir)):
        if not name.endswith(".json"):
            continue
        path = os.path.join(maps_dir, name)
        map_id = json.load(open(path, encoding="utf-8")).get("id") or os.path.splitext(name)[0]
        render(path, os.path.join(out_dir, f"mapPreview_{map_id}.png"))
