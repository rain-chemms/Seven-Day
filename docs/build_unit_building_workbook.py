import json
import math
from datetime import date
from pathlib import Path
from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter

ROOT = Path(r"D:\PROJECT\Godot\Seven-Day")
units = json.loads((ROOT / "data/units.json").read_text(encoding="utf-8"))
buildings = json.loads((ROOT / "data/buildings.json").read_text(encoding="utf-8"))
cards = json.loads((ROOT / "data/cards.json").read_text(encoding="utf-8"))
card_by_unit = {c["unit"]: c for c in cards if c.get("kind") == "unit"}

OUT = ROOT / "docs" / "Seven-Day_军队与建筑数值表.xlsx"
TODAY = date.today().isoformat()

# ---------- palette ----------
BROWN = "5B3A29"
BROWN_LIGHT = "6E4F35"
CREAM = "FDF2E2"
CREAM_DARK = "F4E7D3"
WHITE = "FFFFFF"
GOLD = "C8A97E"
TEXT = "3D2B1F"
MUTED = "7A6652"
GREEN = "E8F3DF"
BLUE = "E4F0FA"
NAVY = "E6EAF7"
RED = "F9E4DF"

thin = Side(style="thin", color="D8C6AE")
BORDER = Border(left=thin, right=thin, top=thin, bottom=thin)

FONT_CN = "Microsoft YaHei"
TITLE_FONT = Font(name=FONT_CN, size=20, bold=True, color=WHITE)
SUB_FONT = Font(name=FONT_CN, size=10, color=MUTED)
HEADER_FONT = Font(name=FONT_CN, size=10.5, bold=True, color=WHITE)
BODY_FONT = Font(name=FONT_CN, size=10.5, color=TEXT)
SMALL_FONT = Font(name=FONT_CN, size=9.5, color=TEXT)
BOLD_FONT = Font(name=FONT_CN, size=10.5, bold=True, color=TEXT)
SECTION_FONT = Font(name=FONT_CN, size=12, bold=True, color=BROWN)

HEADER_FILL = PatternFill("solid", fgColor=BROWN_LIGHT)
TITLE_FILL = PatternFill("solid", fgColor=BROWN)
ALT_FILL = PatternFill("solid", fgColor="FFF9EF")
SECTION_FILL = PatternFill("solid", fgColor=CREAM_DARK)
CENTER = Alignment(horizontal="center", vertical="center", wrap_text=True)
LEFT = Alignment(horizontal="left", vertical="center", wrap_text=True)


def style_title(ws, last_col, title, subtitle):
    ws.merge_cells(start_row=1, start_column=1, end_row=1, end_column=last_col)
    c = ws.cell(row=1, column=1, value=title)
    c.fill = TITLE_FILL
    c.font = TITLE_FONT
    c.alignment = Alignment(horizontal="left", vertical="center")
    ws.row_dimensions[1].height = 34

    ws.merge_cells(start_row=2, start_column=1, end_row=2, end_column=last_col)
    c = ws.cell(row=2, column=1, value=subtitle)
    c.font = SUB_FONT
    c.alignment = Alignment(horizontal="left", vertical="center")
    ws.row_dimensions[2].height = 22


def style_header(ws, row, values):
    for col, value in enumerate(values, 1):
        c = ws.cell(row=row, column=col, value=value)
        c.fill = HEADER_FILL
        c.font = HEADER_FONT
        c.alignment = CENTER
        c.border = BORDER
    ws.row_dimensions[row].height = 34


def style_body(ws, first_row, last_row, left_cols, center_cols, alt=True):
    for r in range(first_row, last_row + 1):
        for c in range(1, ws.max_column + 1):
            cell = ws.cell(row=r, column=c)
            cell.border = BORDER
            cell.font = BODY_FONT
            cell.alignment = LEFT if c in left_cols else CENTER
            if alt and (r - first_row) % 2 == 1:
                cell.fill = ALT_FILL
        ws.row_dimensions[r].height = 25


def yes_no(v):
    return "是" if v else "否"


# ---------- 总览 ----------
wb = Workbook()
ws = wb.active
ws.title = "总览"
ws.sheet_view.showGridLines = False
style_title(ws, 6, "Seven-Day 军队与建筑数值总览",
            f"数据版本：{TODAY} ｜ 数据源：data/units.json、data/buildings.json、data/cards.json")

ws["A4"] = "规模"
ws["A4"].font = SECTION_FONT
ws.merge_cells("A4:F4")
ws["A4"].fill = SECTION_FILL
ws["A4"].alignment = LEFT
ws.row_dimensions[4].height = 26

summary = [
    ("军队单位", len(units), "含地面、空中、海军单位"),
    ("建筑种类", len(buildings), "均含 Lv.1～Lv.3"),
    ("建筑等级行", len(buildings) * 3, "按每级独立列出"),
]
r = 5
for label, value, note in summary:
    ws.cell(row=r, column=1, value=label).font = BOLD_FONT
    ws.cell(row=r, column=2, value=value).font = BODY_FONT
    ws.cell(row=r, column=2).alignment = CENTER
    ws.merge_cells(start_row=r, start_column=3, end_row=r, end_column=6)
    ws.cell(row=r, column=3, value=note).font = SMALL_FONT
    ws.cell(row=r, column=3).alignment = LEFT
    for c in range(1, 7):
        ws.cell(row=r, column=c).border = BORDER
    r += 1

r += 1
ws.cell(row=r, column=1, value="关键极值").font = SECTION_FONT
ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=6)
ws.cell(row=r, column=1).fill = SECTION_FILL
ws.cell(row=r, column=1).alignment = LEFT
ws.row_dimensions[r].height = 26
r += 1

max_hp = max(units, key=lambda u: u["hp"])
max_dmg = max(units, key=lambda u: u["damage"])
max_move = max(units, key=lambda u: u["move"])
max_range = max(units, key=lambda u: u["range"])
cheapest = min(units, key=lambda u: card_by_unit[u["id"]]["cost"])
priciest = max(units, key=lambda u: card_by_unit[u["id"]]["cost"])
hq = next(b for b in buildings if b["id"] == "hq")
best_income = max(buildings, key=lambda b: max(b["income"]))
best_batk = max(buildings, key=lambda b: max(b["attackDamage"]))
extreme_rows = [
    ("最高单位 HP", f'{max_hp["nameZh"]}（{max_hp["hp"]}）', "军队表 F 列"),
    ("最高单位攻击", f'{max_dmg["nameZh"]}（{max_dmg["damage"]}）', "单次攻击伤害"),
    ("最远移动 / 射程", f'{max_move["nameZh"]} 移动 {max_move["move"]}；{max_range["nameZh"]} 射程 {max_range["range"]}', "射程单位为六边形格"),
    ("最便宜 / 最贵单位", f'{cheapest["nameZh"]} {card_by_unit[cheapest["id"]]["cost"]}$；{priciest["nameZh"]} {card_by_unit[priciest["id"]]["cost"]}$', "单位卡造价"),
    ("最高建筑 HP", f'{hq["nameZh"]} Lv.3（{hq["hp"][2]}）', "建筑表 F 列"),
    ("最高建筑收入", f'{best_income["nameZh"]} Lv.3（{best_income["income"][2]}$/回合）', "HQ 75/105/135；城市 45/80/125"),
    ("最高建筑攻击", f'{best_batk["nameZh"]} Lv.3（{best_batk["attackDamage"][2]}）', "单次攻击伤害"),
]
for label, value, note in extreme_rows:
    ws.cell(row=r, column=1, value=label).font = BOLD_FONT
    ws.cell(row=r, column=2, value=value).font = BODY_FONT
    ws.merge_cells(start_row=r, start_column=2, end_row=r, end_column=4)
    ws.cell(row=r, column=2).alignment = LEFT
    ws.cell(row=r, column=5, value=note).font = SMALL_FONT
    ws.merge_cells(start_row=r, start_column=5, end_row=r, end_column=6)
    ws.cell(row=r, column=5).alignment = LEFT
    for c in range(1, 7):
        ws.cell(row=r, column=c).border = BORDER
    ws.row_dimensions[r].height = 24
    r += 1

r += 1
ws.cell(row=r, column=1, value="速览").font = SECTION_FONT
ws.merge_cells(start_row=r, start_column=1, end_row=r, end_column=6)
ws.cell(row=r, column=1).fill = SECTION_FILL
ws.cell(row=r, column=1).alignment = LEFT
ws.row_dimensions[r].height = 26
r += 1
quick = [
    ("军队工作簿页", "「军队」：9 个单位的基础战斗数值与造价。"),
    ("建筑工作簿页", "「建筑」：8 种建筑 × 3 个等级，共 24 行。"),
    ("承受次数页", "「承受次数」：单位对单位时，击杀所需攻击次数矩阵。"),
    ("费用口径", "单位造价取自卡牌；建筑建造费 = UpgradeCost[0]。"),
    ("未计入", "天气、卡牌增益、指挥区加成、等级外的临时 Buff。"),
]
for label, note in quick:
    ws.cell(row=r, column=1, value=label).font = BOLD_FONT
    ws.merge_cells(start_row=r, start_column=2, end_row=r, end_column=6)
    ws.cell(row=r, column=2, value=note).font = SMALL_FONT
    ws.cell(row=r, column=2).alignment = LEFT
    for c in range(1, 7):
        ws.cell(row=r, column=c).border = BORDER
    ws.row_dimensions[r].height = 24
    r += 1

for col, width in {"A": 20, "B": 13, "C": 20, "D": 20, "E": 26, "F": 22}.items():
    ws.column_dimensions[col].width = width
ws.sheet_view.showGridLines = False

# ---------- 军队 ----------
ws = wb.create_sheet("军队")
ws.sheet_view.showGridLines = False
style_title(ws, 15, "军队单位数值",
            "单位造价来自 data/cards.json ｜ 伤害为单次攻击 ｜ 射程单位为六边形格")
headers = ["序号", "ID", "中文名", "英文名", "作战域", "HP", "攻击", "移动", "射程",
           "对空", "对地", "造价 ($)", "伤害/造价", "HP/造价", "特性 / 备注"]
style_header(ws, 4, headers)

domain = {
    "infantry": "地面", "tank": "地面", "missileLauncher": "地面",
    "helicopter": "空中", "fpvDrone": "空中", "fighter": "空中",
    "submarine": "海军（可下潜）", "carrier": "海军", "destroyer": "海军",
}
traits = {
    "infantry": "廉价地面单位，适合占点与推进。",
    "tank": "高生命地面单位，前排承伤。",
    "missileLauncher": "远程压制，可对空；移动较慢。",
    "helicopter": "空中单位，可越过地形；只能打地面。",
    "fpvDrone": "自杀式：撞击目标造成伤害后自毁。",
    "fighter": "空中单位；高机动，可对空/对地。",
    "submarine": "只能在水面部署；可深潜，深潜时不可攻击且敌方不可见。",
    "carrier": "每回合放飞战斗机/武装直升机；可雷达搜索敌方潜艇。",
    "destroyer": "水面主力舰，对陆/海/空全能。",
}
row = 5
for idx, u in enumerate(units, 1):
    card = card_by_unit[u["id"]]
    cost = card["cost"]
    vals = [
        idx, u["id"], u["nameZh"], u["nameEn"], domain.get(u["id"], "地面"),
        u["hp"], u["damage"], u["move"], u["range"],
        yes_no(u["canHitAir"]), yes_no(u["canHitGround"]), cost,
        round(u["damage"] / cost, 3), round(u["hp"] / cost, 3), traits.get(u["id"], ""),
    ]
    for col, v in enumerate(vals, 1):
        ws.cell(row=row, column=col, value=v)
    row += 1
last = row - 1

style_body(ws, 5, last, left_cols={3, 4, 5, 15}, center_cols=set(range(1, 16)) - {3, 4, 5, 15})
for r in range(5, last + 1):
    ws.cell(row=r, column=6).number_format = "#,##0"
    ws.cell(row=r, column=7).number_format = "#,##0"
    ws.cell(row=r, column=12).number_format = "#,##0"
    ws.cell(row=r, column=13).number_format = "0.000"
    ws.cell(row=r, column=14).number_format = "0.000"
    d = ws.cell(row=r, column=5).value
    if d == "空中":
        ws.cell(row=r, column=5).fill = PatternFill("solid", fgColor=BLUE)
    elif d.startswith("海军"):
        ws.cell(row=r, column=5).fill = PatternFill("solid", fgColor=NAVY)
    else:
        ws.cell(row=r, column=5).fill = PatternFill("solid", fgColor=GREEN)

widths = {"A": 7, "B": 20, "C": 14, "D": 22, "E": 15, "F": 9, "G": 9, "H": 9,
          "I": 9, "J": 8, "K": 8, "L": 12, "M": 12, "N": 11, "O": 60}
for col, w in widths.items():
    ws.column_dimensions[col].width = w
ws.freeze_panes = "A5"
ws.auto_filter.ref = f"A4:O{last}"

# ---------- 建筑 ----------
ws = wb.create_sheet("建筑")
ws.sheet_view.showGridLines = False
style_title(ws, 15, "建筑数值（Lv.1～Lv.3）",
            "建造费 = UpgradeCost[0] ｜ 升至本级费用 = 从上一级升级到该等级的花费 ｜ 收入/攻击均为每回合/单次")
b_headers = ["序号", "ID", "中文名", "英文名", "等级", "HP", "每回合收入", "攻击", "射程",
             "控制半径", "对空", "对地", "建造费 ($)", "升至本级 ($)", "特殊 / 产出"]
style_header(ws, 4, b_headers)

building_traits = {
    "hq": "核心建筑：被摧毁即战败；每回合产金；射程 2，对空/对地。",
    "city": "经济建筑：每回合产金；无攻击。",
    "barracks": "提供控制区；按等级召唤：Lv.1 步兵 / Lv.2 步兵、FPV、坦克 / Lv.3 再加导弹车。",
    "helipad": "提供控制区；按等级召唤：Lv.1 FPV / Lv.2 加直升机 / Lv.3 加战斗机。",
    "gunTurret": "防御建筑：射程 1，只打地面；可花 50$ 追加一次攻击。",
    "aaGun": "防御建筑：射程 2，只打空中；可花 50$ 追加一次攻击。",
    "missileSilo": "每回合生成一张「导弹袭击」加入手牌；本体不攻击。",
    "navyBase": "建在临接陆地的水面；每 3 回合生成一张「潜水艇」牌；等级解锁潜艇/驱逐舰/航母。",
}
row = 5
idx = 0
for b in buildings:
    for level in range(3):
        idx += 1
        upgrade_to_level = "—" if level == 0 else b["upgradeCost"][level]
        vals = [
            idx, b["id"], b["nameZh"], b["nameEn"], level + 1,
            b["hp"][level], b["income"][level], b["attackDamage"][level],
            b["attackRange"], b["controlRadius"][level],
            yes_no(b["canHitAir"]), yes_no(b["canHitGround"]),
            b["upgradeCost"][0], upgrade_to_level, building_traits.get(b["id"], ""),
        ]
        for col, v in enumerate(vals, 1):
            ws.cell(row=row, column=col, value=v)
        row += 1
last = row - 1

style_body(ws, 5, last, left_cols={3, 4, 15}, center_cols=set(range(1, 16)) - {3, 4, 15})
for r in range(5, last + 1):
    for col in (6, 7, 8, 13, 14):
        ws.cell(row=r, column=col).number_format = "#,##0"
    if ws.cell(row=r, column=14).value == "—":
        ws.cell(row=r, column=14).alignment = CENTER
        ws.cell(row=r, column=14).font = SMALL_FONT
    # 等级底色
    lv = ws.cell(row=r, column=5).value
    fill = PatternFill("solid", fgColor="FFF4E0" if lv == 1 else ("F7E9D2" if lv == 2 else "EFD9BC"))
    ws.cell(row=r, column=5).fill = fill
    ws.cell(row=r, column=5).font = BOLD_FONT

for col, w in {"A": 7, "B": 17, "C": 14, "D": 22, "E": 7, "F": 9, "G": 12,
               "H": 9, "I": 8, "J": 10, "K": 8, "L": 8, "M": 12, "N": 13, "O": 64}.items():
    ws.column_dimensions[col].width = w
ws.freeze_panes = "A5"
ws.auto_filter.ref = f"A4:O{last}"

# ---------- 承受次数 ----------
from openpyxl.formatting.rule import ColorScaleRule

ws = wb.create_sheet("承受次数")
ws.sheet_view.showGridLines = False
style_title(ws, 17, "单位承受攻击次数（含致命一击）",
            "行 = 防守单位；列 = 可命中它的攻击单位 ｜ 数值 = 击杀该单位需要的攻击次数")

ws.merge_cells("A3:Q3")
legend = ws.cell(row=3, column=1,
                 value="“—”表示该攻击单位无法命中此目标；本表只比较军队单位，不含建筑攻击、天气、卡牌增益和 Buff。")
legend.font = SMALL_FONT
legend.fill = PatternFill("solid", fgColor=CREAM_DARK)
legend.alignment = LEFT
for c in range(1, 18):
    ws.cell(row=3, column=c).border = BORDER
ws.row_dimensions[3].height = 24

headers = ["防守单位", "类型", "HP", "召唤费用 ($)"] + [f'{u["nameZh"]}\n({u["damage"]})' for u in units] + [
    "最低击杀次数", "最高击杀次数", "最耐打来源", "最脆弱来源"
]
style_header(ws, 4, headers)

def unit_type(u):
    if u["isAir"]:
        return "空中"
    if u.get("navy", False):
        return "海军"
    return "地面"

row = 5
for defender in units:
    is_air = defender["isAir"]
    hits = []
    sources = []
    for attacker in units:
        can_hit = attacker["canHitAir"] if is_air else attacker["canHitGround"]
        if can_hit and attacker["damage"] > 0:
            n = math.ceil(defender["hp"] / attacker["damage"])
            hits.append(n)
            sources.append((n, attacker["nameZh"]))
        else:
            hits.append("—")

    if sources:
        low = min(sources, key=lambda x: (x[0], x[1]))
        high = max(sources, key=lambda x: (x[0], x[1]))
        low_text = f"{low[1]}（{low[0]} 次）"
        high_text = f"{high[1]}（{high[0]} 次）"
        low_n, high_n = low[0], high[0]
    else:
        low_text = high_text = "—"
        low_n = high_n = "—"

    vals = [
        defender["nameZh"], unit_type(defender), defender["hp"],
        card_by_unit[defender["id"]]["cost"],
        *hits, low_n, high_n, low_text, high_text,
    ]
    for col, value in enumerate(vals, 1):
        ws.cell(row=row, column=col, value=value)
    row += 1

last = row - 1
style_body(ws, 5, last, left_cols={1, 16, 17}, center_cols=set(range(1, 18)) - {1, 16, 17})

# 类型与矩阵配色
for r in range(5, last + 1):
    ws.cell(row=r, column=3).number_format = "#,##0"
    ws.cell(row=r, column=4).number_format = "#,##0"
    t = ws.cell(row=r, column=2).value
    fill = {"空中": BLUE, "海军": NAVY, "地面": GREEN}.get(t, CREAM_DARK)
    ws.cell(row=r, column=2).fill = PatternFill("solid", fgColor=fill)
    ws.cell(row=r, column=1).font = BOLD_FONT
    for c in range(5, 14):
        cell = ws.cell(row=r, column=c)
        if isinstance(cell.value, int):
            cell.font = BOLD_FONT
        else:
            cell.fill = PatternFill("solid", fgColor="E2D8CA")
            cell.font = SMALL_FONT

ws.conditional_formatting.add(
    f"E5:M{last}",
    ColorScaleRule(start_type="min", start_color="F8696B",
                   mid_type="percentile", mid_value=50, mid_color="FFEB84",
                   end_type="max", end_color="63BE7B")
)

note_row = last + 2
ws.merge_cells(start_row=note_row, start_column=1, end_row=note_row, end_column=17)
note = ws.cell(row=note_row, column=1,
               value="口径：击杀所需次数 = ceil(HP ÷ 攻击力)。对空目标只列出 canHitAir 的单位；对地/海军目标只列出 canHitGround 的单位。"
                       "潜艇按浮出状态计算，FPV 按自爆伤害计算。")
note.font = SMALL_FONT
note.alignment = LEFT
note.fill = PatternFill("solid", fgColor=CREAM)

for col, width in {"A": 15, "B": 9, "C": 8, "D": 12, "N": 13, "O": 13, "P": 24, "Q": 24}.items():
    ws.column_dimensions[col].width = width
for c in range(5, 14):
    ws.column_dimensions[get_column_letter(c)].width = 11
ws.freeze_panes = "E5"
ws.auto_filter.ref = f"A4:Q{last}"

# ---------- 说明 ----------
ws = wb.create_sheet("说明")
ws.sheet_view.showGridLines = False
style_title(ws, 8, "数值口径与说明", "用于快速核对当前版本的单位与建筑数值")
notes = [
    ("数据源", "data/units.json、data/buildings.json、data/cards.json。单位造价直接取自单位卡；建筑造价取自 UpgradeCost[0]。"),
    ("单位数值", "HP、攻击、移动、射程均为基础值；未应用天气、卡牌、指挥区、等级或临时 Buff。"),
    ("建筑数值", "每个建筑有 Lv.1/Lv.2/Lv.3 三行；“升至本级费用”指从上一等级升到该等级的费用。"),
    ("建造与升级", "建筑建造费 = UpgradeCost[0]；Lv.1→Lv.2 = UpgradeCost[1]；Lv.2→Lv.3 = UpgradeCost[2]。"),
    ("伤害", "攻击列为单次攻击伤害；炮台/防空炮可用 50$ 追加一次攻击。"),
    ("射程", "单位为六边形格；0 表示无攻击能力。"),
    ("收入", "每回合开始时获得；HQ 与城市按等级提供 100/150/200。"),
    ("控制半径", "建筑提供的控制区半径，与攻击射程分开计算。"),
    ("对空/对地", "“是/否”表示该单位或建筑能否攻击对应目标。"),
    ("特殊机制", "潜艇可深潜；航母每回合放飞飞机；兵营/直升机坪/海军基地按等级解锁召唤单位；导弹井每回合生成导弹牌。"),
    ("未收录", "天气伤害、卡牌效果、维修费、建筑拆除返还等情境数值不在本表内。"),
]
r = 4
for title, body in notes:
    ws.cell(row=r, column=1, value=title).font = BOLD_FONT
    ws.cell(row=r, column=1).fill = PatternFill("solid", fgColor=CREAM_DARK)
    ws.cell(row=r, column=1).alignment = LEFT
    ws.merge_cells(start_row=r, start_column=2, end_row=r, end_column=8)
    ws.cell(row=r, column=2, value=body).font = BODY_FONT
    ws.cell(row=r, column=2).alignment = LEFT
    for c in range(1, 9):
        ws.cell(row=r, column=c).border = BORDER
    ws.row_dimensions[r].height = 42
    r += 1

ws.column_dimensions["A"].width = 16
for col in "BCDEFGH":
    ws.column_dimensions[col].width = 16

# print settings
for sheet in wb.worksheets:
    sheet.sheet_properties.pageSetUpPr.fitToPage = True
    sheet.page_setup.fitToWidth = 1
    sheet.page_setup.fitToHeight = 0

OUT.parent.mkdir(parents=True, exist_ok=True)
wb.save(OUT)
print(OUT)


# ---------- 保存后自检 ----------
if __name__ == "__main__":
    from openpyxl import load_workbook
    wb = load_workbook(OUT, data_only=True)
    uws, bws = wb["军队"], wb["建筑"]
    assert uws.max_row == 4 + len(units)
    assert bws.max_row == 4 + len(buildings) * 3
    for i, src in enumerate(units, 5):
        assert uws.cell(i, 2).value == src["id"]
        assert uws.cell(i, 6).value == src["hp"]
        assert uws.cell(i, 12).value == card_by_unit[src["id"]]["cost"]
    for i, src in enumerate(buildings):
        for lv in range(3):
            r = 5 + i * 3 + lv
            assert bws.cell(r, 2).value == src["id"]
            assert bws.cell(r, 5).value == lv + 1
            assert bws.cell(r, 6).value == src["hp"][lv]
            assert bws.cell(r, 7).value == src["income"][lv]

    mws = wb["承受次数"]
    assert mws.max_row >= 4 + len(units) + 2
    for i, src in enumerate(units, 5):
        assert mws.cell(i, 1).value == src["nameZh"]
        assert mws.cell(i, 3).value == src["hp"]
        assert mws.cell(i, 4).value == card_by_unit[src["id"]]["cost"]

    print("verified", len(units), "units", len(buildings) * 3,
          "building levels", len(units), "x", len(units), "damage matrix")
