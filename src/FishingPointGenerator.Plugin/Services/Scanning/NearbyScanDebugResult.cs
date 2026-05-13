using System.Numerics;
using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Plugin.Services.Scanning;

internal sealed record NearbyScanDebugResult
{
    public string Message { get; init; } = string.Empty;
    public uint TerritoryId { get; init; }
    public Vector3 PlayerPosition { get; init; }
    public float RadiusMeters { get; init; }
    public IReadOnlyList<DebugOverlayTriangle> FishableTriangles { get; init; } = [];
    public IReadOnlyList<DebugOverlayTriangle> WalkableTriangles { get; init; } = [];
    public IReadOnlyList<ApproachCandidate> Candidates { get; init; } = [];
}

internal readonly record struct DebugOverlayTriangle(
    Vector3 A,
    Vector3 B,
    Vector3 C,
    ulong Material,
    SceneMeshType MeshType)
{
    public Vector3 Centroid => (A + B + C) / 3f;
}
