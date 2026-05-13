# FishingPointGenerator

FishingPointGenerator 是一个用于采集和维护 FINAL FANTASY XIV 钓场点位数据的 Dalamud 工具。

工具会生成钓场目录，扫描当前地图的全图候选点，维护真实可钓点与面向，并将已确认的点位导出为 JSON。

## 功能

- 从游戏数据生成钓场目录。
- 扫描当前 Territory 的全图候选点，作为推荐缓存。
- 以客户端已有 Territory -> FishingSpot 层级维护真实可钓点和面向。
- 支持抛竿自动点亮、人工确认、不匹配记录、忽略/复核状态和验证报告。
- 只导出已确认点位，并保留来源信息便于回溯。

## 使用流程

1. 进入需要采集的地图。
2. 运行 `/fpg catalog` 生成钓场目录。
3. 运行 `/fpg refresh` 读取当前地图的目标钓场。
4. 运行 `/fpg next`，或在界面中选择一个目标钓场。
5. 运行 `/fpg scan` 扫描当前 Territory 全图候选点。
6. 运行 `/fpg scantarget`，或在界面中为已选钓场从 Territory 缓存派生推荐候选。
7. 移动到候选点附近并朝可钓碰撞面抛竿；抛竿日志命中当前钓场时会自动点亮同块的局部范围。
8. 如果需要手动记录，运行 `/fpg confirm`；如果推荐点不匹配当前目标钓场，运行 `/fpg mismatch`。
9. 运行 `/fpg export` 导出已确认点位。

## 命令

```text
/fpg
/fpg catalog
/fpg refresh
/fpg next
/fpg target <fishingSpotId>
/fpg scan
/fpg scantarget
/fpg debugnear [radius]
/fpg debugcandidates [radius] [limit]
/fpg debugclear
/fpg flag
/fpg flagstand
/fpg confirm
/fpg mismatch
/fpg allowweak
/fpg allowrisk
/fpg ignore
/fpg report
/fpg export
```

## 数据文件

运行时数据保存在插件配置目录下：

```text
data/catalog/fishing_spots.json
data/maintenance/territory_{territoryId}.json
data/generated/territory_{territoryId}.json
data/scans/territory_{territoryId}/spot_{fishingSpotId}.scan.json   # 旧版兼容回退
data/labels/territory_{territoryId}/spot_{fishingSpotId}.ledger.json       # 旧版兼容证据
data/review/territory_{territoryId}/spot_{fishingSpotId}.review.json
data/reports/territory_{territoryId}/spot_{fishingSpotId}.validation.json
data/exports/FishingSpotApproachPoints.json
```

## 导出规则

FishingPointGenerator 默认只导出经过验证的数据。

- `confirmed` 钓场会被导出。
- `weakCoverage`、`mixedRisk`、`noCandidate`、`ignored` 默认不会导出。
- 弱覆盖数据可通过 `/fpg allowweak` 显式允许导出。
- 混合风险数据可通过 `/fpg allowrisk` 显式复核后允许导出。
- 导出点使用 MissFisher 消费侧的 `FishingSpot`、`PositionX/Y/Z`、`Rotation` 形态，并附带来源 evidence、candidate fingerprint 和 scan id 便于追踪。

## 文档

- [数据格式](docs/data-format.md)
- [标注流程](docs/labeling-workflow.md)
- [扫描器说明](docs/vnavmesh-scanner-notes.md)
