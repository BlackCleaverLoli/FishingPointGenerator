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
    private readonly IPCSubscriber<Vector3, float, float, Vector3?> meshNearestPoint =
        new("vnavmesh.Query.Mesh.NearestPoint", () => (Vector3?)null);
    private readonly IPCSubscriber<Vector3, float, float, Vector3?> meshNearestPointReachable =
        new("vnavmesh.Query.Mesh.NearestPointReachable", () => (Vector3?)null);
    private readonly IPCSubscriber<Vector3, bool, float, Vector3?> meshPointOnFloor =
        new("vnavmesh.Query.Mesh.PointOnFloor", () => (Vector3?)null);
    private readonly IPCSubscriber<Vector3, bool, float, bool> pathfindAndMoveCloseTo =
        new("vnavmesh.SimpleMove.PathfindAndMoveCloseTo", () => false);
    private readonly IPCSubscriber<bool> pathIsRunning = new("vnavmesh.Path.IsRunning", () => false);
    private readonly IPCSubscriber<bool> pathfindInProgress = new("vnavmesh.SimpleMove.PathfindInProgress", () => false);
    private readonly IPCSubscriber<float> pathDistance = new("vnavmesh.Path.GetDistance", () => 0f);
    private readonly IPCSubscriber<object> pathStop = new("vnavmesh.Path.Stop", () => null!);

    public bool IsReady => navIsReady.TryInvokeFunc();
    public bool IsPathRunning => pathIsRunning.TryInvokeFunc();
    public bool IsPathfindInProgress => pathfindInProgress.TryInvokeFunc();
    public float PathLeftDistance => pathDistance.TryInvokeFunc();

    public MeshPointQueryResult QueryNearestReachablePoint(Vector3 point, float halfExtentXZ, float halfExtentY)
    {
        if (!IsReady)
            return MeshPointQueryResult.Unavailable("vnavmesh not ready");

        try
        {
            var nearest = meshNearestPointReachable.TryInvokeFunc(point, halfExtentXZ, halfExtentY);
            return nearest is { } reachablePoint
                ? MeshPointQueryResult.Reachable(reachablePoint)
                : MeshPointQueryResult.Unreachable("no nearby reachable mesh point");
        }
        catch (Exception ex)
        {
            return MeshPointQueryResult.Unavailable(ex.Message);
        }
    }

    public MeshPointQueryResult QueryLandingPoint(Vector3 point, float probeHeight, float halfExtentXZ, float halfExtentY)
    {
        if (!IsReady)
            return MeshPointQueryResult.Unavailable("vnavmesh not ready");

        try
        {
            var probe = new Vector3(point.X, point.Y + probeHeight, point.Z);
            var floor = meshPointOnFloor.TryInvokeFunc(probe, true, halfExtentXZ);
            if (floor is { } floorPoint)
                return MeshPointQueryResult.Reachable(floorPoint);

            var nearest = meshNearestPoint.TryInvokeFunc(point, halfExtentXZ, halfExtentY);
            return nearest is { } nearestPoint
                ? MeshPointQueryResult.Reachable(nearestPoint)
                : MeshPointQueryResult.Unreachable("no nearby landing mesh point");
        }
        catch (Exception ex)
        {
            return MeshPointQueryResult.Unavailable(ex.Message);
        }
    }

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

    public MoveCommandResult MoveCloseTo(Vector3 destination, bool fly, float range)
    {
        if (!IsReady)
            return MoveCommandResult.Unavailable("vnavmesh not ready");

        try
        {
            return pathfindAndMoveCloseTo.TryInvokeFunc(destination, fly, range)
                ? MoveCommandResult.Started()
                : MoveCommandResult.Unavailable("vnavmesh move IPC unavailable or rejected");
        }
        catch (Exception ex)
        {
            return MoveCommandResult.Unavailable(ex.Message);
        }
    }

    public void StopMovement()
    {
        try
        {
            pathStop.TryInvokeAction();
        }
        catch
        {
            // vnavmesh may disappear while the user is stopping automation.
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

internal sealed record MeshPointQueryResult(
    PathQueryStatus Status,
    Vector3 Point,
    string Message)
{
    public bool IsReachable => Status == PathQueryStatus.Reachable;

    public static MeshPointQueryResult Reachable(Vector3 point) =>
        new(PathQueryStatus.Reachable, point, string.Empty);

    public static MeshPointQueryResult Unreachable(string message) =>
        new(PathQueryStatus.Unreachable, default, message);

    public static MeshPointQueryResult Unavailable(string message) =>
        new(PathQueryStatus.Unavailable, default, message);
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

internal sealed record MoveCommandResult(
    PathQueryStatus Status,
    string Message)
{
    public bool IsStarted => Status == PathQueryStatus.Reachable;

    public static MoveCommandResult Started() =>
        new(PathQueryStatus.Reachable, string.Empty);

    public static MoveCommandResult Unavailable(string message) =>
        new(PathQueryStatus.Unavailable, message);
}
