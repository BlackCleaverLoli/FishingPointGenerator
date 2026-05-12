using System.Numerics;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.STD;
using Lumina.Excel.Sheets;
using OmenTools;
using ColliderType = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType;

namespace FishingPointGenerator.Plugin.Services.Scanning;

internal sealed class ActiveLayoutScene
{
    public uint TerritoryId { get; private set; }
    public List<string> Terrains { get; } = [];
    public Dictionary<uint, (Transform Transform, Vector3 BoundsMin, Vector3 BoundsMax)> AnalyticShapes { get; } = [];
    public Dictionary<uint, string> MeshPaths { get; } = [];
    public List<BgPartEntry> BgParts { get; } = [];
    public List<ColliderEntry> Colliders { get; } = [];

    public unsafe void FillFromActiveLayout()
    {
        FillFromLayout(LayoutWorld.Instance()->GlobalLayout);
        FillFromLayout(LayoutWorld.Instance()->ActiveLayout);
    }

    private unsafe void FillFromLayout(LayoutManager* layout)
    {
        if (layout == null || layout->InitState != 7 || layout->FestivalStatus is > 0 and < 5)
            return;

        var filter = LayoutReader.FindFilter(layout);
        TerritoryId = filter != null ? filter->TerritoryTypeId : layout->TerritoryTypeId;

        foreach (var (key, value) in layout->CrcToAnalyticShapeData)
            AnalyticShapes[key.Key] = (value.Transform, value.BoundsMin, value.BoundsMax);

        foreach (var (_, value) in layout->Terrains)
            Terrains.Add($"{value.Value->PathString}/collision");

        var bgParts = layout->InstancesByType.FindPtr(InstanceType.BgPart);
        if (bgParts != null)
            FillBgParts(layout, bgParts);

        var colliders = layout->InstancesByType.FindPtr(InstanceType.CollisionBox);
        if (colliders != null)
            FillColliders(layout, colliders);
    }

    private unsafe void FillBgParts(LayoutManager* layout, StdMap<ulong, Pointer<ILayoutInstance>>* bgParts)
    {
        foreach (var (key, value) in *bgParts)
        {
            var bgPart = (BgPartsLayoutInstance*)value.Value;
            if ((bgPart->Flags3 & 0x10) == 0)
                continue;

            var materialId = (ulong)bgPart->CollisionMaterialIdHigh << 32 | bgPart->CollisionMaterialIdLow;
            var materialMask = (ulong)bgPart->CollisionMaterialMaskHigh << 32 | bgPart->CollisionMaterialMaskLow;
            var transform = *value.Value->GetTransformImpl();

            if (bgPart->AnalyticShapeDataCrc != 0)
            {
                BgParts.Add(new BgPartEntry(key, transform, bgPart->AnalyticShapeDataCrc, materialId, materialMask, true));
                continue;
            }

            if (bgPart->CollisionMeshPathCrc == 0)
                continue;

            EnsureMeshPath(layout, bgPart->CollisionMeshPathCrc);
            BgParts.Add(new BgPartEntry(key, transform, bgPart->CollisionMeshPathCrc, materialId, materialMask, false));
        }
    }

    private unsafe void FillColliders(LayoutManager* layout, StdMap<ulong, Pointer<ILayoutInstance>>* colliders)
    {
        foreach (var (key, value) in *colliders)
        {
            var collider = (CollisionBoxLayoutInstance*)value.Value;
            if ((collider->Flags3 & 0x10) == 0)
                continue;

            if (collider->PcbPathCrc != 0)
                EnsureMeshPath(layout, collider->PcbPathCrc);

            var materialId = (ulong)collider->MaterialIdHigh << 32 | collider->MaterialIdLow;
            var materialMask = (ulong)collider->MaterialMaskHigh << 32 | collider->MaterialMaskLow;
            Colliders.Add(new ColliderEntry(
                key,
                collider->Transform,
                collider->PcbPathCrc,
                materialId,
                materialMask,
                collider->TriggerBoxLayoutInstance.Type));
        }
    }

    private unsafe void EnsureMeshPath(LayoutManager* layout, uint crc)
    {
        if (MeshPaths.ContainsKey(crc))
            return;

        MeshPaths[crc] = LayoutReader.ReadString(layout->CrcToPath.FindPtr(crc));
    }

    public string GetTerritoryName(uint fallbackTerritoryId = 0)
    {
        var territoryId = TerritoryId != 0 ? TerritoryId : fallbackTerritoryId;
        var row = DService.Instance().Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId);
        return row?.Name.ToString() ?? string.Empty;
    }
}

internal readonly record struct BgPartEntry(
    ulong Key,
    Transform Transform,
    uint Crc,
    ulong MaterialId,
    ulong MaterialMask,
    bool Analytic);

internal readonly record struct ColliderEntry(
    ulong Key,
    Transform Transform,
    uint Crc,
    ulong MaterialId,
    ulong MaterialMask,
    ColliderType Type);

internal static unsafe class LayoutReader
{
    public static string ReadString(byte* data) => data != null ? MemoryHelper.ReadStringNullTerminated((nint)data) : string.Empty;

    public static string ReadString(RefCountedString* data) => data != null ? data->DataString : string.Empty;

    public static TValue* FindPtr<TKey, TValue>(ref this StdMap<TKey, Pointer<TValue>> map, TKey key)
        where TKey : unmanaged, IComparable
        where TValue : unmanaged =>
        map.TryGetValuePointer(key, out var pointer) && pointer != null ? pointer->Value : null;

    public static LayoutManager.Filter* FindFilter(LayoutManager* layout)
    {
        if (layout->CfcId != 0)
        {
            foreach (var (_, value) in layout->Filters)
            {
                if (value.Value->CfcId == layout->CfcId)
                    return value.Value;
            }
        }

        if (layout->TerritoryTypeId != 0)
        {
            foreach (var (_, value) in layout->Filters)
            {
                if (value.Value->TerritoryTypeId == layout->TerritoryTypeId)
                    return value.Value;
            }
        }

        return layout->TerritoryTypeId == 0 ? layout->Filters.FindPtr(layout->LayerFilterKey) : null;
    }
}
