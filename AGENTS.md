# Seven-Day 项目协作约定

## 数值表同步（必须遵守）

- `docs/Seven-Day_军队与建筑数值表.xlsx` 是军队与建筑数值的唯一汇总表。
- 只要修改了以下任一内容，就必须同步重新生成该 Excel：
  - `data/units.json`
  - `data/buildings.json`
  - `data/cards.json` 中单位卡的 `cost`
  - 影响单位/建筑基础数值的代码默认值
- 重新生成命令：

```powershell
python docs/build_unit_building_workbook.py
```

- 生成脚本会：
  - 重新读取上述 JSON；
  - 更新总览、军队、建筑和说明四个工作表；
  - 保存后自检单位数量、HP 和造价。
- 生成后必须确认脚本输出 `verified ...`，并将更新后的 Excel 一并交付。
- 不要在未更新 Excel 的情况下只修改 JSON 或代码中的数值。
- 如果 Excel 正在被占用导致保存失败，先关闭表格再重新生成，不能静默跳过。

## 数值口径

- 单位造价取自单位卡 `cost`。
- 建筑建造费 = `UpgradeCost[0]`。
- Lv.1→Lv.2 = `UpgradeCost[1]`；Lv.2→Lv.3 = `UpgradeCost[2]`。
- 表中不含天气、卡牌增益、临时 Buff 等情境数值。
