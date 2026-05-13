# Labeling Workflow

当前主流程是客户端目录驱动的 `Territory -> FishingSpot` 维护，而不是 block-first。长期维护事实是 `SpotKey = (territoryId, fishingSpotId)` 下的真实可钓点 `Position + Rotation`；扫描候选点用于插旗、可达性判断、抛竿点亮和人工标记辅助，不作为长期事实。

## 标注步骤

1. 进入需要采集的地图。
2. 运行 `/fpg catalog` 生成或刷新 Lumina `FishingSpot` 目录。
3. 运行 `/fpg refresh` 读取当前 territory 的钓场目标。
4. 在 UI 左侧领地抽屉选择 Territory，再在钓场列表中选择目标；也可运行 `/fpg next` 选择下一个需要维护的钓场。领地抽屉由可维护 FishingSpot 目录按 Territory 推导，`需维护` 统计来自每个钓场的分析状态。
5. 运行 `/fpg scan` 重扫当前 Territory 全图候选点。
6. 运行 `/fpg scantarget`，或在 UI 中为已选 `SpotKey` 从 Territory 缓存派生候选。
7. UI 显示真实可钓点列表、当前候选、朝向、可飞状态、距角色距离和路径检查结果；overlay 的短线表示该点位记录的 `Rotation`。
8. 人工移动到候选点附近并朝可钓碰撞面抛竿。
9. 如果抛竿日志命中当前目标钓场，自动记录同块的局部范围；也可运行 `/fpg confirm` 或点击确认手动记录玩家当前站位。
10. 如果当前候选确认不可用，运行 `/fpg rejectcandidate` 或点击排除当前候选，后续候选选择会跳过它。
11. 如果只有弱覆盖但人工确认足够可靠，运行 `/fpg allowweak` 允许该钓场导出。
12. 如果存在混合风险但已经人工复核，运行 `/fpg allowrisk` 允许该钓场导出。
    两个复核许可可以叠加；同时弱覆盖和混合风险的钓场需要分别执行这两个操作。
13. 如果该钓场暂不维护，运行 `/fpg ignore`。
14. 运行 `/fpg report` 生成当前钓场验证报告。
15. 运行 `/fpg export` 导出所有 confirmed 钓场。

`/fpg label <fishingSpotId>` 会先选择已选 Territory 中对应钓场，再用玩家当前站位确认该钓场。

## 状态规则

- `needsScan`：目录中有目标，但该 Territory 还没有全图扫描缓存。
- `needsVisit`：已有候选点，但维护层还没有真实可钓点。
- `confirmed`：维护层已有真实可钓点，且没有阻断导出的风险状态。
- `weakCoverage`：确认点数量不足，默认不导出，除非 review 明确允许。
- `mixedRisk`：候选点可能靠近其它 FishingSpot 目标，默认不导出，除非 review 明确允许风险导出。
- `noCandidate`：扫描完成但没有当前钓场候选点。
- `ignored`：人工忽略该钓场。

## 扫描器状态

当前插件默认接入 `VnavmeshSceneScanner`。它会从当前 active layout 中找出 fishable 材质面与纯 walkable 面的边界线，沿边界生成 Territory 级候选点；候选 `Rotation` 垂直于边界线并朝向纯 walkable 面。`SpotScanService` 再为当前 `FishingSpot` 按需派生候选，但长期事实只写入维护层的真实可钓点。`PlaceholderScanner` 仅作为开发回退实现保留，默认不会使用。
