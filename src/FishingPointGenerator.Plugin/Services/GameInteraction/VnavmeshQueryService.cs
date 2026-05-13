using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using OmenTools.Dalamud.Abstractions;

namespace FishingPointGenerator.Plugin.Services.GameInteraction;

internal sealed class VnavmeshQueryService
{
    private readonly IPCSubscriber<bool> navIsReady = new("vnavmesh.Nav.IsReady", () => false);
    private readonly IPCSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>> navPathfind =
        new("vnavmesh.Nav.Pathfind", () => null!);
    private readonly IPCSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> navPathfindCancelable =
        new("vnavmesh.Nav.PathfindCancelable", () => null!);

    public bool IsReady => navIsReady.TryInvokeFunc();

    public async Task<PathQueryResult> QueryPathAsync(Vector3 from, Vector3 to, bool fly, CancellationToken cancellationToken)
    {
        if (!IsReady)
            return PathQueryResult.Unavailable("vnavmesh not ready");

        try
        {
            var task = navPathfindCancelable.TryInvokeFunc(from, to, fly, cancellationToken)
                ?? navPathfind.TryInvokeFunc(from, to, fly);
            if (task is null)
                return PathQueryResult.Unavailable("vnavmesh pathfind IPC unavailable");

            var waypoints = await task.ConfigureAwait(false);
            if (waypoints.Count == 0)
                return PathQueryResult.Unreachable("empty path");

            return PathQueryResult.Reachable(GetPathLength(from, waypoints, to), waypoints.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PathQueryResult.Unavailable(ex.Message);
        }
    }

    private static float GetPathLength(Vector3 from, IReadOnlyList<Vector3> waypoints, Vector3 to)
    {
        var length = 0f;
        var previous = from;
        foreach (var waypoint in waypoints)
        {
            length += Vector3.Distance(previous, waypoint);
            previous = waypoint;
        }

        if (waypoints.Count == 0 || Vector3.DistanceSquared(previous, to) > 0.01f)
            length += Vector3.Distance(previous, to);

        return length;
    }
}

internal enum PathQueryStatus
{
    Reachable,
    Unreachable,
    Unavailable,
}

internal sealed record PathQueryResult(
    PathQueryStatus Status,
    float PathLengthMeters,
    int WaypointCount,
    string Message)
{
    public bool IsReachable => Status == PathQueryStatus.Reachable;

    public static PathQueryResult Reachable(float pathLengthMeters, int waypointCount) =>
        new(PathQueryStatus.Reachable, pathLengthMeters, waypointCount, string.Empty);

    public static PathQueryResult Unreachable(string message) =>
        new(PathQueryStatus.Unreachable, float.MaxValue, 0, message);

    public static PathQueryResult Unavailable(string message) =>
        new(PathQueryStatus.Unavailable, float.MaxValue, 0, message);
}
