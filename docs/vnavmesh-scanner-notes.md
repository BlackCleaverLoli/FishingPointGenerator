# vnavmesh Scanner Notes

真实扫描器是项目核心，独立在服务接口后面实现，避免污染标注、钓场分析和导出流程。

当前实现为 `VnavmeshSceneScanner`：它不通过 IPC 查询 vnavmesh，也不依赖 MissFisher。扫描器参考 vnavmesh 的 `SceneDefinition` / `SceneExtractor` 做法，直接读取当前 active layout 中的 terrain、bgpart 与 collision collider，解析 PCB/analytic collider，并复用 vnavmesh 对材质位的判定：`0x8000` 视为 fishable。

注意：`.navmesh` 运行时缓存主要保存 Detour navmesh 与飞行 volume；fishable primitive/material flag 不适合作为稳定来源。因此当前第一版优先从 active layout 源碰撞数据提取 fishable 几何，而不是只读 `.navmesh` 缓存文件。

## 接口边界

建议插件层定义：

```csharp
public interface ICurrentTerritoryScanner
{
    TerritorySurveyDocument ScanCurrentTerritory();
}
```

Core 只接收 `ApproachCandidate`，不依赖 Dalamud、OmenTools 或 vnavmesh 类型。

spot-driven 主流程在插件层用 `SpotScanService` 包装该接口：底层扫描当前 territory 几何并写入全图缓存，再为当前 `FishingSpotTarget` 派生 `data/scans/territory_{territoryId}/spot_{fishingSpotId}.scan.json`。派生时不按钓场半径硬裁剪，钓场归属由抛竿日志确认。

## 目标能力

1. 读取当前 territory 的 active layout。
2. 提取 terrain、bgpart 和 collider 的碰撞 mesh/analytic primitive。
3. 抽取 material/primitive flags。
4. 识别 fishable 或等价可钓材质。
5. 找出 fishable 面与纯 walkable 面的共享边界；如果没有精确共享边，则使用近邻平行边作为回退。
6. 沿边界线采样候选点，点位向 walkable 侧偏移 0.5m；`Rotation` 垂直于边界线并朝向 walkable 面。
7. 清洗重复点，先形成 territory 级候选，再由 `SpotScanService` 收敛到当前 `SpotKey`。

## 约束

- 不通过 IPC 运行时查询 vnavmesh。
- 不把 MissFisher 作为运行时依赖。
- 缓存格式可能变化，扫描失败需要给出清晰错误。
- 扫描器不得直接决定某钓场是否可导出；`mixedRisk`、`weakCoverage`、`orphanedLabels` 等策略由 Core 分析和导出流程统一处理。
