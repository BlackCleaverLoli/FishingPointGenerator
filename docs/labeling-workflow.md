# Labeling Workflow

当前主流程是钓场目标驱动，而不是 block-first。长期维护单位是 `SpotKey = (territoryId, fishingSpotId)`；扫描候选点只是当前钓场的证据和推荐辅助。

## 标注步骤

1. 进入需要采集的地图。
2. 运行 `/fpg catalog` 生成或刷新 Lumina `FishingSpot` 目录。
3. 运行 `/fpg refresh` 读取当前 territory 的钓场目标。
4. 运行 `/fpg next` 选择下一个需要维护的钓场，或在 UI 列表中选择目标。
5. 运行 `/fpg scan` 重扫当前 Territory 全图候选点。
6. 运行 `/fpg scantarget`，或在 UI 列表中为已选 `SpotKey` 生成点缓存。
7. UI 显示推荐点位、朝向和当前状态；overlay 的短线表示该点位记录的 `Rotation`。
8. 人工移动到候选点附近并朝可钓碰撞面抛竿。
9. 如果抛竿日志命中当前目标钓场，自动记录同块的局部范围；也可运行 `/fpg confirm` 或点击确认手动记录当前推荐。
10. 如果抛竿结果不匹配当前目标，运行 `/fpg mismatch`，保留复核证据。
11. 如果只有弱覆盖但人工确认足够可靠，运行 `/fpg allowweak` 允许该钓场导出。
12. 如果该钓场暂不维护，运行 `/fpg ignore`。
13. 运行 `/fpg report` 生成当前钓场验证报告。
14. 运行 `/fpg export` 导出所有 confirmed 钓场。

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

当前插件默认接入 `VnavmeshSceneScanner`。它会从当前 active layout 中找出 fishable 材质面与纯 walkable 面的边界线，沿边界生成 Territory 级候选点；候选 `Rotation` 垂直于边界线并朝向纯 walkable 面。`SpotScanService` 再为当前 `FishingSpot` 派生 spot 级点缓存，但钓场归属以抛竿日志确认。`PlaceholderScanner` 仅作为开发回退实现保留，默认不会使用。
