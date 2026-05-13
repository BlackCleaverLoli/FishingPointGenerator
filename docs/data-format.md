# Data Format

`SpotJsonStore` 使用 `System.Text.Json` 写入 UTF-8 JSON，属性名为 camelCase，枚举值为 camelCase 字符串。插件运行时以 Dalamud 插件配置目录作为根目录；本仓库的 `data` 目录仅用于说明和未来样例。

## Catalog

路径：

```text
data/catalog/fishing_spots.json
```

用途：从 Lumina `FishingSpot` 表生成长期目标清单。筛选口径对齐 MissFisher 传送索引：`PlaceName.RowId != 0`、`TerritoryType.RowId > 0`，并排除 territory `900`、`1163`；不要求 `Item` 列表非空。维护主键是：

```text
SpotKey = (territoryId, fishingSpotId)
```

每个 target 保存 `fishingSpotId`、名称、territory、map/world 坐标、radius、itemIds、catalogVersion 和 sourceGameDataVersion。`itemIds` 仅用于参考，空列表不影响钓场进入维护目录。

## Maintenance

路径：

```text
data/maintenance/territory_{territoryId}.json
```

用途：保存某个 Territory 下所有 FishingSpot 的长期维护事实。UI 以客户端目录的 `Territory -> FishingSpot` 展示此文件；`ApproachPoint.Position + Rotation` 是权威可钓站位与面向。扫描候选、ledger 和 review 只能作为证据或复核输入，不应覆盖维护事实。

每个 `SpotMaintenanceRecord` 保存 `fishingSpotId`、review 决策、`ApproachPoints` 和 `Evidence`。重扫只刷新 scan cache，不删除 `ApproachPoints`、`Evidence` 或 review。

## Legacy Spot Scan

路径：

```text
data/scans/territory_{territoryId}/spot_{fishingSpotId}.scan.json
```

用途：旧版 spot 级候选点缓存，仅作为回退输入。当前主流程使用 `data/generated/territory_{territoryId}.json` 的 Territory 全图缓存按需派生候选，不再把 spot scan 文件作为长期维护数据。

候选点使用稳定 fingerprint：

```text
hash(territoryId, fishingSpotId, quantized position, quantized rotation)
```

其中 position 按 0.5m 量化，rotation 按 0.05 rad 量化。`Position + Rotation` 是下游消费形态。候选点同时保留扫描期 `surfaceGroupId`，表示同一片 fishable 水面连通组件；抛竿自动点亮会优先在 seed 候选点的同一 `surfaceGroupId` 内沿候选点图连锁扩展。
从全图缓存派生到单个钓场时，候选会记录 `distanceToTargetCenterMeters` 与 `isWithinTargetSearchRadius`。状态统计和当前候选选择只看目标范围内候选；抛竿自动点亮仍可使用全图候选作为实证兜底。

## Legacy Label Ledger

路径：

```text
data/labels/territory_{territoryId}/spot_{fishingSpotId}.ledger.json
```

用途：旧版人工事件日志。当前主流程会把旧 confirm/override 事件兼容导入 `data/maintenance/territory_{territoryId}.json` 的 `ApproachPoints` 与 `Evidence`，但新维护事实以 maintenance 文件为准。

确认事件记录实际点位、面向、sourceScanId、sourceScannerVersion 和 candidateFingerprint。ledger 不因重扫删除，但不再作为导出事实来源。

## Review And Reports

路径：

```text
data/review/territory_{territoryId}/spot_{fishingSpotId}.review.json
data/reports/territory_{territoryId}/spot_{fishingSpotId}.validation.json
```

`review` 保存人工复核决策，例如忽略某钓场、允许弱覆盖导出或允许风险导出；维护层中的 review decision 可组合，因此同一钓场可以同时允许弱覆盖和混合风险导出。`reports` 是可重建的验证输出，会记录当前分析状态、候选数量、维护层 `ApproachPoints` 与 `Evidence` 快照。

## Export

路径：

```text
data/exports/FishingSpotApproachPoints.json
```

导出规则：

- 只导出 `confirmed` 的 `SpotKey`。
- `weakCoverage`、`mixedRisk`、`noCandidate`、`ignored` 默认不导出。
- 每个点包含 `FishingSpot`、`PositionX`、`PositionY`、`PositionZ`、`Rotation`，对齐 MissFisher `FishRecordSpotIndex` 的消费字段。
- 每个点保留 `sourceEvidenceId`、`sourceCandidateFingerprint`、`sourceScanId`，便于回溯。
