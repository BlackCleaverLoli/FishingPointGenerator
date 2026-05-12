# Data Format

`SpotJsonStore` 使用 `System.Text.Json` 写入 UTF-8 JSON，属性名为 camelCase，枚举值为 camelCase 字符串。插件运行时以 Dalamud 插件配置目录作为根目录；本仓库的 `data` 目录仅用于说明和未来样例。

## Catalog

路径：

```text
data/catalog/fishing_spots.json
```

用途：从 Lumina `FishingSpot` 表生成长期目标清单。维护主键是：

```text
SpotKey = (territoryId, fishingSpotId)
```

每个 target 保存 `fishingSpotId`、名称、territory、map/world 坐标、radius、itemIds、catalogVersion 和 sourceGameDataVersion。默认跳过无有效 Item 的钓场，并排除 territory `900`、`1163`。

## Spot Scan

路径：

```text
data/scans/territory_{territoryId}/spot_{fishingSpotId}.scan.json
```

用途：保存某个 `SpotKey` 的候选点缓存。底层扫描器扫描当前 territory 的 active layout 几何；spot 级 scan 从全图缓存派生，不按 `FishingSpot` 半径硬裁剪。实际归属由抛竿日志中的 `FishingSpot.RowId` 确认。

候选点使用稳定 fingerprint：

```text
hash(territoryId, fishingSpotId, quantized position, quantized rotation)
```

其中 position 按 0.5m 量化，rotation 按 0.05 rad 量化。`Position + Rotation` 是下游消费形态。

## Label Ledger

路径：

```text
data/labels/territory_{territoryId}/spot_{fishingSpotId}.ledger.json
```

用途：保存人工事件日志，不因重扫删除。事件类型包括 `confirm`、`reject`、`mismatch`、`ignoreTarget`、`override`。

确认事件记录实际点位、面向、sourceScanId、sourceScannerVersion 和 candidateFingerprint。重扫后先按 fingerprint 精确重绑，再按点位和面向做空间近邻重绑；仍无法匹配的确认事件进入 orphaned label 报告。

## Review And Reports

路径：

```text
data/review/territory_{territoryId}/spot_{fishingSpotId}.review.json
data/reports/territory_{territoryId}/spot_{fishingSpotId}.validation.json
```

`review` 保存人工复核决策，例如忽略某钓场或允许弱覆盖导出。`reports` 是可重建的验证输出，用于记录 `mixedRisk`、`noCandidate`、`orphanedLabels` 等状态。

## Export

路径：

```text
data/exports/FishingSpotApproachPoints.json
```

导出规则：

- 只导出 `confirmed` 的 `SpotKey`。
- `weakCoverage`、`mixedRisk`、`noCandidate`、`ignored`、`orphanedLabels` 默认不导出。
- 每个点包含 `FishingSpot`、`PositionX`、`PositionY`、`PositionZ`、`Rotation`，对齐 MissFisher `FishRecordSpotIndex` 的消费字段。
- 每个点保留 `sourceLabelId`、`sourceCandidateFingerprint`、`sourceScanId`，便于回溯。
