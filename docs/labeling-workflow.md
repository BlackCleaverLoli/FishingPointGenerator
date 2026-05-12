# Labeling Workflow

当前主流程是钓场目标驱动，而不是 block-first。长期维护单位是 `SpotKey = (territoryId, fishingSpotId)`；扫描候选点只是当前钓场的证据和推荐辅助。

## 标注步骤

1. 进入需要采集的地图。
2. 运行 `/fpg catalog` 生成或刷新 Lumina `FishingSpot` 目录。
3. 运行 `/fpg refresh` 读取当前 territory 的钓场目标。
4. 运行 `/fpg next` 选择下一个需要维护的钓场，或在 UI 列表中选择目标。
5. 运行 `/fpg scan` 重扫当前钓场。扫描器会读取当前地图几何，但只保存当前 `SpotKey` 的候选点。
6. UI 显示推荐站位、目标点、面向和当前状态。
7. 人工移动到推荐站位抛竿。
8. 如果推荐点命中当前钓场，运行 `/fpg confirm` 或点击确认。
9. 如果抛竿结果不匹配当前目标，运行 `/fpg mismatch`，保留复核证据。
10. 如果只有弱覆盖但人工确认足够可靠，运行 `/fpg allowweak` 允许该钓场导出。
11. 如果该钓场暂不维护，运行 `/fpg ignore`。
12. 运行 `/fpg report` 生成当前钓场验证报告。
13. 运行 `/fpg export` 导出所有 confirmed 钓场。

`/fpg label <fishingSpotId>` 作为兼容快捷命令保留：它会先选择当前 territory 中对应钓场，再确认当前推荐点。

## 状态规则

- `needsScan`：目录中有目标，但还没有该 `SpotKey` 的 scan 文件。
- `needsVisit`：已有候选点，但还没有确认事件。
- `confirmed`：确认事件可重绑到当前 scan，且没有阻断导出的风险状态。
- `weakCoverage`：确认点数量不足，默认不导出，除非 review 明确允许。
- `mixedRisk`：候选点可能靠近其它 FishingSpot 目标，默认不导出。
- `noCandidate`：扫描完成但没有当前钓场候选点。
- `ignored`：人工忽略该钓场。
- `orphanedLabels`：重扫后已有确认事件无法重绑，默认不导出。

## 扫描器状态

当前插件默认接入 `VnavmeshSceneScanner`。它会从当前 active layout 的 fishable 材质碰撞面生成候选站位；`SpotScanService` 再按当前 `FishingSpot` 目标中心和半径过滤为 spot 级 scan。`PlaceholderScanner` 仅作为开发回退实现保留，默认不会使用。
