# vnavmesh Scanner Notes

真实扫描器是项目核心，独立在服务接口后面实现，避免污染标注、钓场分析和导出流程。

当前实现为 `VnavmeshSceneScanner`：它不通过 IPC 查询 vnavmesh，也不依赖 MissFisher。扫描器参考 vnavmesh 的 `SceneDefinition` / `SceneExtractor` 做法，直接读取当前 active layout 中的 terrain、bgpart 与 collision collider，解析 PCB/analytic collider，并复用 vnavmesh 对材质位的判定：`0x8000` 视为 fishable。

注意：`.navmesh` 运行时缓存主要保存 Detour navmesh 与飞行 volume；fishable primitive/material flag 不适合作为稳定来源。因此当前第一版优先从 active layout 源碰撞数据提取 fishable 几何，而不是只读 `.navmesh` 缓存文件。

## 接口边界

建议插件层定义：

```csharp
public interface ICurrentTerritoryScanner
{
    string Name { get; }
    bool IsPlaceholder { get; }
    TerritorySurveyDocument ScanCurrentTerritory();
    NearbyScanDebugResult DebugScanNearby(float radiusMeters);
}
```

Core 只接收 `ApproachCandidate`，不依赖 Dalamud、OmenTools 或 vnavmesh 类型。

维护主流程在插件层用 `SpotScanService` 包装该接口：底层扫描当前 territory 几何并生成内存候选，再为当前 `FishingSpotTarget` 在内存中派生候选。候选不写入文件；只有人工确认或抛竿点亮后的维护记录会保存到领地维护文件。派生时不按钓场半径硬裁剪，钓场归属由抛竿日志和人工复核确认。

## 目标能力

1. 读取当前 territory 的 active layout。
2. 提取 terrain、bgpart 和 collider 的碰撞 mesh/analytic primitive。
3. 抽取 material/primitive flags。
4. 识别 fishable 或等价可钓材质。
5. 找出 fishable 面与纯 walkable 面的水平邻近关系；优先沿 fishable 外边界采样，也会从 walkable 面的水平投影补充候选，不用水面与站立面的高度差排除候选。
6. 边界候选点位向 walkable 侧偏移 0.5m；投影候选保留 walkable 面上的采样点；`Rotation` 朝向对应 fishable 水面。
7. 为候选点记录 `surfaceGroupId`，表示其面对的 fishable 水面连通组件；后续抛竿连锁点亮以该值作为几何边界。
8. 清洗重复点，先形成 territory 级候选，再由 `SpotScanService` 收敛到当前 `SpotKey`。

## 约束

- 不通过 IPC 运行时查询 vnavmesh。
- 不把 MissFisher 作为运行时依赖。
- 缓存格式可能变化，扫描失败需要给出清晰错误。
- 扫描器不得直接决定某钓场是否可导出；`mixedRisk`、`weakCoverage` 等策略由 Core 分析和导出流程统一处理。
- `/fpg debugnear [radius]` 只分析角色附近碰撞面，不写入缓存；日志包含 fishable 水面高度、材质、mesh 类型、边界匹配和候选点样例。
