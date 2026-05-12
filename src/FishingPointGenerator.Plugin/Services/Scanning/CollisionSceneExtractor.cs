using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using OmenTools;
using ColliderType = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType;

namespace FishingPointGenerator.Plugin.Services.Scanning;

internal sealed class CollisionSceneExtractor
{
    private const string KeyAnalyticBox = "<box>";
    private const string KeyAnalyticSphere = "<sphere>";
    private const string KeyAnalyticCylinder = "<cylinder>";
    private const string KeyAnalyticPlaneSingle = "<plane one-sided>";
    private const string KeyAnalyticPlaneDouble = "<plane two-sided>";
    private const string KeyMeshCylinder = "<mesh cylinder>";

    private static readonly List<MeshPart> BoxMesh = BuildBoxMesh();
    private static readonly List<MeshPart> SphereMesh = BuildSphereMesh(16);
    private static readonly List<MeshPart> CylinderMesh = BuildCylinderMesh(16);
    private static readonly List<MeshPart> PlaneMesh = BuildPlaneMesh();

    private readonly Dictionary<string, SceneMesh> meshes = [];

    public unsafe CollisionSceneExtractor(ActiveLayoutScene scene)
    {
        meshes[KeyAnalyticBox] = CreateBuiltinMesh(BoxMesh, SceneMeshType.AnalyticShape);
        meshes[KeyAnalyticSphere] = CreateBuiltinMesh(SphereMesh, SceneMeshType.AnalyticShape);
        meshes[KeyAnalyticCylinder] = CreateBuiltinMesh(CylinderMesh, SceneMeshType.AnalyticShape);
        meshes[KeyAnalyticPlaneSingle] = CreateBuiltinMesh(PlaneMesh, SceneMeshType.AnalyticPlane);
        meshes[KeyAnalyticPlaneDouble] = CreateBuiltinMesh(PlaneMesh, SceneMeshType.AnalyticPlane);
        meshes[KeyMeshCylinder] = CreateBuiltinMesh(CylinderMesh, SceneMeshType.CylinderMesh);

        foreach (var path in scene.MeshPaths.Values.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal))
            AddMesh(path, SceneMeshType.FileMesh);

        foreach (var terrain in scene.Terrains.Distinct(StringComparer.Ordinal))
            AddTerrainMeshes(terrain);

        foreach (var bgPart in scene.BgParts)
        {
            var info = ExtractBgPartInfo(scene, bgPart);
            if (info.Path.Length > 0 && meshes.TryGetValue(info.Path, out var mesh))
                AddInstance(mesh, bgPart.Key, info.Transform, info.Bounds, bgPart.MaterialId);
        }

        foreach (var collider in scene.Colliders)
        {
            if ((collider.MaterialId & 0x410) == 0x400)
                continue;

            var info = ExtractColliderInfo(scene, collider);
            if (info.Path.Length > 0 && meshes.TryGetValue(info.Path, out var mesh))
                AddInstance(mesh, collider.Key, info.Transform, info.Bounds, collider.MaterialId);
        }
    }

    public IReadOnlyList<ExtractedSceneTriangle> ExtractTriangles()
    {
        var triangles = new List<ExtractedSceneTriangle>();

        foreach (var mesh in meshes.Values)
        {
            foreach (var instance in mesh.Instances)
            {
                foreach (var part in mesh.Parts)
                    ExtractPartTriangles(mesh, instance, part, triangles);
            }
        }

        return triangles;
    }

    private static void ExtractPartTriangles(
        SceneMesh mesh,
        MeshInstance instance,
        MeshPart part,
        List<ExtractedSceneTriangle> triangles)
    {
        foreach (var primitive in part.Primitives)
        {
            var flags = primitive.Flags & ~instance.ForceClearPrimitiveFlags | instance.ForceSetPrimitiveFlags;
            if (primitive.V1 < 0 || primitive.V1 >= part.Vertices.Count
                || primitive.V2 < 0 || primitive.V2 >= part.Vertices.Count
                || primitive.V3 < 0 || primitive.V3 >= part.Vertices.Count)
                continue;

            var a = instance.WorldTransform.TransformCoordinate(part.Vertices[primitive.V1]);
            var b = instance.WorldTransform.TransformCoordinate(part.Vertices[primitive.V2]);
            var c = instance.WorldTransform.TransformCoordinate(part.Vertices[primitive.V3]);
            var ab = b - a;
            var ac = c - a;
            var cross = Vector3.Cross(ab, ac);
            var length = cross.Length();
            if (length <= 0.0001f)
                continue;

            var area = length * 0.5f;
            var normal = cross / length;
            triangles.Add(new ExtractedSceneTriangle(a, b, c, normal, area, flags, primitive.Material, mesh.MeshType));
        }
    }

    private unsafe void AddTerrainMeshes(string terrain)
    {
        var list = DService.Instance().Data.GetFile($"{terrain}/list.pcb");
        if (list is null || list.Data.Length == 0)
            return;

        fixed (byte* rawData = &list.Data[0])
        {
            var header = (ColliderStreamed.FileHeader*)rawData;
            foreach (ref var entry in new Span<ColliderStreamed.FileEntry>(header + 1, header->NumMeshes))
            {
                var mesh = AddMesh($"{terrain}/tr{entry.MeshId:d4}.pcb", SceneMeshType.Terrain);
                AddInstance(mesh, 0, Matrix4x3.Identity, entry.Bounds, 0);
            }
        }
    }

    private unsafe SceneMesh AddMesh(string path, SceneMeshType type)
    {
        if (meshes.TryGetValue(path, out var existing))
            return existing;

        var mesh = new SceneMesh { MeshType = type };
        var file = DService.Instance().Data.GetFile(path);
        if (file is not null && file.Data.Length > 0)
        {
            fixed (byte* rawData = &file.Data[0])
            {
                var header = (MeshPCB.FileHeader*)rawData;
                if (header->Version is 1 or 4)
                    FillFromFileNode(mesh.Parts, (MeshPCB.FileNode*)(header + 1));
            }
        }

        mesh.LocalBounds = CalculateLocalBounds(mesh.Parts);
        meshes[path] = mesh;
        return mesh;
    }

    private (string Path, Matrix4x3 Transform, AABB Bounds) ExtractBgPartInfo(ActiveLayoutScene scene, BgPartEntry bgPart)
    {
        if (!bgPart.Analytic)
        {
            var path = scene.MeshPaths.GetValueOrDefault(bgPart.Crc) ?? string.Empty;
            var transform1 = new Matrix4x3(bgPart.Transform.Compose());
            return path.Length == 0 || !meshes.TryGetValue(path, out var mesh)
                ? (string.Empty, Matrix4x3.Identity, default)
                : (path, transform: transform1, CalculateMeshBounds(mesh, transform1));
        }

        if (!scene.AnalyticShapes.TryGetValue(bgPart.Crc, out var shape))
            return (string.Empty, Matrix4x3.Identity, default);

        var scaleVector = (shape.BoundsMax - shape.BoundsMin) * 0.5f;
        if (shape.Transform.Type == (int)FileLayerGroupAnalyticCollider.Type.Cylinder)
            scaleVector.Z = scaleVector.X;

        var boundsTransform = Matrix4x4.CreateScale(scaleVector);
        boundsTransform.Translation = (shape.BoundsMin + shape.BoundsMax) * 0.5f;
        var fullTransform = boundsTransform * shape.Transform.Compose() * bgPart.Transform.Compose();
        var transform = new Matrix4x3(fullTransform);

        return (FileLayerGroupAnalyticCollider.Type)shape.Transform.Type switch
        {
            FileLayerGroupAnalyticCollider.Type.Box => (KeyAnalyticBox, transform, CalculateBoxBounds(transform)),
            FileLayerGroupAnalyticCollider.Type.Sphere => (KeyAnalyticSphere, transform, CalculateSphereBounds(transform)),
            FileLayerGroupAnalyticCollider.Type.Cylinder => (KeyMeshCylinder, transform, CalculateBoxBounds(transform)),
            FileLayerGroupAnalyticCollider.Type.Plane => (KeyAnalyticPlaneSingle, transform, CalculatePlaneBounds(transform)),
            _ => (string.Empty, Matrix4x3.Identity, default),
        };
    }

    private (string Path, Matrix4x3 Transform, AABB Bounds) ExtractColliderInfo(ActiveLayoutScene scene, ColliderEntry collider)
    {
        var transform = new Matrix4x3(collider.Transform.Compose());
        return collider.Type switch
        {
            ColliderType.Box => (KeyAnalyticBox, transform, CalculateBoxBounds(transform)),
            ColliderType.Sphere => (KeyAnalyticSphere, transform, CalculateSphereBounds(transform)),
            ColliderType.Cylinder => (KeyAnalyticCylinder, transform, CalculateBoxBounds(transform)),
            ColliderType.Plane => (KeyAnalyticPlaneSingle, transform, CalculatePlaneBounds(transform)),
            ColliderType.Mesh => ExtractMeshColliderInfo(scene, collider.Crc, transform),
            ColliderType.PlaneTwoSided => (KeyAnalyticPlaneDouble, transform, CalculatePlaneBounds(transform)),
            _ => (string.Empty, Matrix4x3.Identity, default),
        };
    }

    private (string Path, Matrix4x3 Transform, AABB Bounds) ExtractMeshColliderInfo(ActiveLayoutScene scene, uint crc, Matrix4x3 transform)
    {
        var path = scene.MeshPaths.GetValueOrDefault(crc) ?? string.Empty;
        return path.Length == 0 || !meshes.TryGetValue(path, out var mesh)
            ? (string.Empty, Matrix4x3.Identity, default)
            : (path, transform, CalculateMeshBounds(mesh, transform));
    }

    private static SceneMesh CreateBuiltinMesh(List<MeshPart> parts, SceneMeshType type) => new()
    {
        Parts = parts,
        MeshType = type,
        LocalBounds = CalculateLocalBounds(parts),
    };

    private static void AddInstance(SceneMesh mesh, ulong id, Matrix4x3 worldTransform, AABB worldBounds, ulong materialId)
    {
        mesh.Instances.Add(new MeshInstance(
            id,
            worldTransform,
            worldBounds,
            materialId,
            SceneMaterialFlags.FromMaterial(materialId),
            ScenePrimitiveFlags.None));
    }

    private unsafe void FillFromFileNode(List<MeshPart> parts, MeshPCB.FileNode* node)
    {
        if (node == null)
            return;

        parts.Add(BuildMeshFromNode(node));
        FillFromFileNode(parts, node->Child1);
        FillFromFileNode(parts, node->Child2);
    }

    private static unsafe MeshPart BuildMeshFromNode(MeshPCB.FileNode* node)
    {
        var part = new MeshPart();
        for (var index = 0; index < node->NumVertsRaw + node->NumVertsCompressed; index++)
            part.Vertices.Add(node->Vertex(index));

        foreach (ref var primitive in node->Primitives)
        {
            part.Primitives.Add(new ScenePrimitive(
                primitive.V1,
                primitive.V2,
                primitive.V3,
                SceneMaterialFlags.FromMaterial(primitive.Material),
                primitive.Material));
        }

        part.LocalBounds = CalculateLocalBounds(part.Vertices);
        return part;
    }

    private static AABB CalculateLocalBounds(List<MeshPart> parts)
    {
        var result = new AABB { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
        foreach (var part in parts)
        {
            result.Min = Vector3.Min(result.Min, part.LocalBounds.Min);
            result.Max = Vector3.Max(result.Max, part.LocalBounds.Max);
        }

        return result;
    }

    private static AABB CalculateLocalBounds(List<Vector3> vertices)
    {
        var result = new AABB { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
        foreach (var vertex in vertices)
        {
            result.Min = Vector3.Min(result.Min, vertex);
            result.Max = Vector3.Max(result.Max, vertex);
        }

        return result;
    }

    private static AABB CalculateBoxBounds(Matrix4x3 world)
    {
        var result = new AABB { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
        for (var index = 0; index < 8; index++)
        {
            var point = ((index & 1) != 0 ? world.Row0 : -world.Row0)
                + ((index & 2) != 0 ? world.Row1 : -world.Row1)
                + ((index & 4) != 0 ? world.Row2 : -world.Row2)
                + world.Row3;
            result.Min = Vector3.Min(result.Min, point);
            result.Max = Vector3.Max(result.Max, point);
        }

        return result;
    }

    private static AABB CalculateSphereBounds(Matrix4x3 world)
    {
        var scale = world.Row0.Length();
        var vectorScale = new Vector3(scale);
        return new AABB { Min = world.Row3 - vectorScale, Max = world.Row3 + vectorScale };
    }

    private static AABB CalculateMeshBounds(SceneMesh mesh, Matrix4x3 world) => CalculateTransformedBounds(mesh.LocalBounds, world);

    private static AABB CalculateTransformedBounds(AABB bounds, Matrix4x3 world)
    {
        var result = new AABB { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
        for (var index = 0; index < 8; index++)
        {
            var local = new Vector3(
                (index & 1) != 0 ? bounds.Max.X : bounds.Min.X,
                (index & 2) != 0 ? bounds.Max.Y : bounds.Min.Y,
                (index & 4) != 0 ? bounds.Max.Z : bounds.Min.Z);
            var point = world.TransformCoordinate(local);
            result.Min = Vector3.Min(result.Min, point);
            result.Max = Vector3.Max(result.Max, point);
        }

        return result;
    }

    private static AABB CalculatePlaneBounds(Matrix4x3 world)
    {
        var result = new AABB { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
        for (var index = 0; index < 4; index++)
        {
            var point = ((index & 1) != 0 ? world.Row0 : -world.Row0)
                + ((index & 2) != 0 ? world.Row1 : -world.Row1)
                + world.Row3;
            result.Min = Vector3.Min(result.Min, point);
            result.Max = Vector3.Max(result.Max, point);
        }

        return result;
    }

    private static MeshPart FinalizePart(MeshPart part)
    {
        part.LocalBounds = CalculateLocalBounds(part.Vertices);
        return part;
    }

    private static List<MeshPart> BuildBoxMesh()
    {
        var mesh = new MeshPart();
        mesh.Vertices.Add(new Vector3(-1, -1, -1));
        mesh.Vertices.Add(new Vector3(-1, -1, +1));
        mesh.Vertices.Add(new Vector3(+1, -1, -1));
        mesh.Vertices.Add(new Vector3(+1, -1, +1));
        mesh.Vertices.Add(new Vector3(-1, +1, -1));
        mesh.Vertices.Add(new Vector3(-1, +1, +1));
        mesh.Vertices.Add(new Vector3(+1, +1, -1));
        mesh.Vertices.Add(new Vector3(+1, +1, +1));
        mesh.Primitives.Add(new ScenePrimitive(0, 2, 1, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(1, 2, 3, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(5, 7, 4, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(4, 7, 6, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(0, 1, 4, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(4, 1, 5, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(2, 6, 3, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(3, 6, 7, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(0, 4, 2, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(2, 4, 6, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(1, 3, 5, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(5, 3, 7, ScenePrimitiveFlags.None));
        return [FinalizePart(mesh)];
    }

    private static List<MeshPart> BuildSphereMesh(int segmentCount)
    {
        var mesh = new MeshPart();
        var angle = 360f / segmentCount;
        var maxParallel = segmentCount / 4 - 1;

        for (var parallel = -maxParallel; parallel <= maxParallel; parallel++)
        {
            var r = DegreesToDirection(parallel * angle);
            for (var index = 0; index < segmentCount; index++)
            {
                var vertex = DegreesToDirection(index * angle) * r.Y;
                mesh.Vertices.Add(new Vector3(vertex.X, r.X, vertex.Y));
            }
        }

        var capIndex = mesh.Vertices.Count;
        mesh.Vertices.Add(new Vector3(0, -1, 0));
        mesh.Vertices.Add(new Vector3(0, +1, 0));

        for (var parallel = 0; parallel < maxParallel * 2; parallel++)
        {
            var parallelOffset = parallel * segmentCount;
            for (var index = 0; index < segmentCount - 1; index++)
            {
                var vertexIndex = parallelOffset + index;
                mesh.Primitives.Add(new ScenePrimitive(vertexIndex, vertexIndex + 1, vertexIndex + segmentCount, ScenePrimitiveFlags.None));
                mesh.Primitives.Add(new ScenePrimitive(vertexIndex + segmentCount, vertexIndex + 1, vertexIndex + segmentCount + 1, ScenePrimitiveFlags.None));
            }

            mesh.Primitives.Add(new ScenePrimitive(parallelOffset + segmentCount - 1, parallelOffset, parallelOffset + (segmentCount * 2) - 1, ScenePrimitiveFlags.None));
            mesh.Primitives.Add(new ScenePrimitive(parallelOffset + (segmentCount * 2) - 1, parallelOffset, parallelOffset + segmentCount, ScenePrimitiveFlags.None));
        }

        for (var index = 0; index < segmentCount - 1; index++)
            mesh.Primitives.Add(new ScenePrimitive(index + 1, index, capIndex, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(0, segmentCount - 1, capIndex, ScenePrimitiveFlags.None));

        var topIndex = capIndex - segmentCount;
        for (var index = 0; index < segmentCount - 1; index++)
            mesh.Primitives.Add(new ScenePrimitive(topIndex + index, topIndex + index + 1, capIndex + 1, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(topIndex + segmentCount - 1, topIndex, capIndex + 1, ScenePrimitiveFlags.None));
        return [FinalizePart(mesh)];
    }

    private static List<MeshPart> BuildCylinderMesh(int segmentCount)
    {
        var mesh = new MeshPart();
        var angle = 360f / segmentCount;

        for (var index = 0; index < segmentCount; index++)
        {
            var direction = DegreesToDirection((index + 5) * angle);
            mesh.Vertices.Add(new Vector3(direction.X, -1, direction.Y));
            mesh.Vertices.Add(new Vector3(direction.X, +1, direction.Y));
        }

        mesh.Vertices.Add(new Vector3(0, -1, 0));
        mesh.Vertices.Add(new Vector3(0, +1, 0));

        for (var index = 0; index < segmentCount - 1; index++)
        {
            var vertexIndex = index * 2;
            mesh.Primitives.Add(new ScenePrimitive(vertexIndex, vertexIndex + 2, vertexIndex + 1, ScenePrimitiveFlags.None));
            mesh.Primitives.Add(new ScenePrimitive(vertexIndex + 1, vertexIndex + 2, vertexIndex + 3, ScenePrimitiveFlags.None));
        }

        var lastVertexIndex = (segmentCount - 1) * 2;
        mesh.Primitives.Add(new ScenePrimitive(lastVertexIndex, 0, lastVertexIndex + 1, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(lastVertexIndex + 1, 0, 1, ScenePrimitiveFlags.None));

        var bottomCenter = segmentCount * 2;
        for (var index = 0; index < segmentCount - 1; index++)
        {
            var vertexIndex = index * 2;
            mesh.Primitives.Add(new ScenePrimitive(vertexIndex + 2, vertexIndex, bottomCenter, ScenePrimitiveFlags.None));
        }

        mesh.Primitives.Add(new ScenePrimitive(0, lastVertexIndex, bottomCenter, ScenePrimitiveFlags.None));

        var topCenter = bottomCenter + 1;
        for (var index = 0; index < segmentCount - 1; index++)
        {
            var vertexIndex = index * 2 + 1;
            mesh.Primitives.Add(new ScenePrimitive(vertexIndex, vertexIndex + 2, topCenter, ScenePrimitiveFlags.None));
        }

        mesh.Primitives.Add(new ScenePrimitive(lastVertexIndex + 1, 1, topCenter, ScenePrimitiveFlags.None));
        return [FinalizePart(mesh)];
    }

    private static List<MeshPart> BuildPlaneMesh()
    {
        var mesh = new MeshPart();
        mesh.Vertices.Add(new Vector3(-1, +1, 0));
        mesh.Vertices.Add(new Vector3(-1, -1, 0));
        mesh.Vertices.Add(new Vector3(+1, -1, 0));
        mesh.Vertices.Add(new Vector3(+1, +1, 0));
        mesh.Primitives.Add(new ScenePrimitive(0, 1, 2, ScenePrimitiveFlags.None));
        mesh.Primitives.Add(new ScenePrimitive(0, 2, 3, ScenePrimitiveFlags.None));
        return [FinalizePart(mesh)];
    }

    private static Vector2 DegreesToDirection(float degrees)
    {
        var radians = degrees * MathF.PI / 180f;
        return new Vector2(MathF.Sin(radians), MathF.Cos(radians));
    }

    private sealed class SceneMesh
    {
        public List<MeshPart> Parts { get; init; } = [];
        public List<MeshInstance> Instances { get; } = [];
        public SceneMeshType MeshType { get; init; }
        public AABB LocalBounds;
    }

    private sealed class MeshPart
    {
        public List<Vector3> Vertices { get; } = [];
        public List<ScenePrimitive> Primitives { get; } = [];
        public AABB LocalBounds;
    }

    private sealed record MeshInstance(
        ulong Id,
        Matrix4x3 WorldTransform,
        AABB WorldBounds,
        ulong Material,
        ScenePrimitiveFlags ForceSetPrimitiveFlags,
        ScenePrimitiveFlags ForceClearPrimitiveFlags);

    private readonly record struct ScenePrimitive(
        int V1,
        int V2,
        int V3,
        ScenePrimitiveFlags Flags,
        ulong Material = 0);
}

internal readonly record struct ExtractedSceneTriangle(
    Vector3 A,
    Vector3 B,
    Vector3 C,
    Vector3 Normal,
    float Area,
    ScenePrimitiveFlags Flags,
    ulong Material,
    SceneMeshType MeshType)
{
    public Vector3 Centroid => (A + B + C) / 3f;

    public bool IsFishable => Flags.HasFlag(ScenePrimitiveFlags.Fishable);

    public bool IsWalkable
    {
        get
        {
            if (Flags.HasFlag(ScenePrimitiveFlags.Fishable)
                || Flags.HasFlag(ScenePrimitiveFlags.ForceUnwalkable)
                || Flags.HasFlag(ScenePrimitiveFlags.FlyThrough))
                return false;

            if (Flags.HasFlag(ScenePrimitiveFlags.Unlandable) && !Flags.HasFlag(ScenePrimitiveFlags.ForceWalkable))
                return false;

            return Normal.Y > 0.55f;
        }
    }
}

internal enum SceneMeshType
{
    Terrain,
    FileMesh,
    CylinderMesh,
    AnalyticShape,
    AnalyticPlane,
}
