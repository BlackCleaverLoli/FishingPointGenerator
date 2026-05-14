using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FishingPointGenerator.Core.Models;
using FishingPointGenerator.Plugin.Services;
using FishingPointGenerator.Plugin.Services.Scanning;
using OmenTools;

namespace FishingPointGenerator.Plugin.Services.Overlay;

internal sealed unsafe class WorldOverlayRenderer
{
    private const uint TargetColor = 0xff45b7ff;
    private const uint TerritoryCandidateColor = 0x66808080;
    private const uint CandidateColor = 0xffd0d0d0;
    private const uint ConfirmedColor = 0xff55d779;
    private const uint SelectedCandidateColor = 0xff35f0ff;
    private const uint BlockLabelColor = 0xffc0a060;
    private const uint WarningColor = 0xff4080ff;
    private const uint FishableDebugFillColor = 0x3345a0ff;
    private const uint FishableDebugEdgeColor = 0xff45a0ff;
    private const uint FishableDebugTextColor = 0xffd0f0ff;
    private const uint WalkableDebugEdgeColor = 0xffff9a2f;
    private const uint WalkableDebugHatchColor = 0xbbff9a2f;
    private const uint WalkableDebugTextColor = 0xffffddb0;
    private const int CircleSegments = 48;
    private const float FacingGuideLengthMeters = 5f;
    private const float DebugSurfaceHatchSpacingMeters = 4f;
    private const int MaxSurfaceDebugLabels = 4;
    private const int MaxCandidateLabels = 18;

    private Matrix4x4 viewProj;
    private Vector4 nearPlane;
    private Vector2 viewportSize;

    public void Draw(SpotWorkflowSession session)
    {
        if (!session.OverlayEnabled)
            return;

        var player = DService.Instance().ObjectTable.LocalPlayer;
        if (player is null || !TryUpdateCamera())
            return;

        var drawDistance = Math.Clamp(session.OverlayMaxDistanceMeters, 10f, 1000f);
        var candidateLimit = Math.Clamp(session.OverlayCandidateLimit, 10, 5000);
        session.OverlayMaxDistanceMeters = drawDistance;
        session.OverlayCandidateLimit = candidateLimit;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(Vector2.Zero);
        ImGui.Begin(
            "fpg_world_overlay",
            ImGuiWindowFlags.NoInputs
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoBackground);
        ImGui.SetWindowSize(ImGui.GetIO().DisplaySize);

        var territoryLimit = session.OverlayShowCandidates ? candidateLimit / 2 : candidateLimit;
        var selectedLimit = session.OverlayShowTerritoryCache ? candidateLimit - territoryLimit : candidateLimit;
        var drawList = ImGui.GetWindowDrawList();
        var canDrawSelectedTerritory = session.SelectedTerritoryIsCurrent;
        if (session.CurrentTarget is not null && canDrawSelectedTerritory)
            DrawTarget(drawList, session, player.Position);
        if (session.OverlayShowFishableDebug || session.OverlayShowWalkableDebug)
            DrawSurfaceDebug(drawList, session, player.Position, drawDistance, Math.Min(candidateLimit, 512));
        if (session.OverlayShowTerritoryCache && territoryLimit > 0 && canDrawSelectedTerritory)
            DrawTerritoryCache(drawList, session, player.Position, drawDistance, territoryLimit);
        if (session.OverlayShowCandidates && selectedLimit > 0 && canDrawSelectedTerritory)
            DrawCandidates(drawList, session, player.Position, drawDistance, selectedLimit);

        ImGui.End();
        ImGui.PopStyleVar();
    }

    private bool TryUpdateCamera()
    {
        var controlCamera = CameraManager.Instance()->GetActiveCamera();
        var renderCamera = controlCamera != null ? controlCamera->SceneCamera.RenderCamera : null;
        if (renderCamera == null)
            return false;

        var view = renderCamera->ViewMatrix;
        view.M44 = 1f;
        viewProj = view * renderCamera->ProjectionMatrix;
        nearPlane = new Vector4(view.M13, view.M23, view.M33, view.M43 + renderCamera->NearPlane);

        var device = Device.Instance();
        viewportSize = device != null
            ? new Vector2(device->Width, device->Height)
            : ImGui.GetIO().DisplaySize;
        return viewportSize.X > 0f && viewportSize.Y > 0f;
    }

    private void DrawTarget(ImDrawListPtr drawList, SpotWorkflowSession session, Vector3 playerPosition)
    {
        var target = session.CurrentTarget!;
        var baseY = session.CurrentCandidateSelection?.Candidate.Position.Y ?? playerPosition.Y;
        var center = new Vector3(target.WorldX, baseY, target.WorldZ);

        DrawWorldPoint(drawList, center, 5f, TargetColor, true);
        DrawWorldText(drawList, center + new Vector3(0f, 2f, 0f), $"{target.FishingSpotId} {target.Name}", TargetColor);

        if (session.OverlayShowTargetRadius && target.Radius > 0f)
            DrawWorldCircle(drawList, center, Math.Clamp(target.Radius, 3f, 140f), TargetColor);
    }

    private void DrawCandidates(
        ImDrawListPtr drawList,
        SpotWorkflowSession session,
        Vector3 playerPosition,
        float drawDistance,
        int candidateLimit)
    {
        var scan = session.CurrentScan;
        if (scan is null || scan.Candidates.Count == 0)
            return;

        var selectedCandidateFingerprint = session.CurrentCandidateSelection?.Candidate.CandidateFingerprint;
        var recordedCandidateIds = session.CurrentTerritoryRecordedCandidateIds;
        var recordedCandidateFingerprints = session.CurrentTerritoryRecordedCandidateFingerprints;

        var candidates = scan.Candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Distance = candidate.Position.HorizontalDistanceTo(Point3.From(playerPosition)),
            })
            .Where(item => item.Distance <= drawDistance)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Candidate.CandidateFingerprint, StringComparer.Ordinal)
            .Take(candidateLimit)
            .ToList();

        var selection = session.CurrentCandidateSelection;
        var labelCount = 0;
        foreach (var item in candidates)
        {
            var candidate = item.Candidate;
            var standing = ToVector3(candidate.Position);
            var isSelectedCandidate = string.Equals(candidate.CandidateFingerprint, selectedCandidateFingerprint, StringComparison.Ordinal);
            var isConfirmed = IsRecordedCandidate(candidate, recordedCandidateIds, recordedCandidateFingerprints);
            var color = isSelectedCandidate ? SelectedCandidateColor : isConfirmed ? ConfirmedColor : CandidateColor;
            var pointRadius = isSelectedCandidate ? 5f : isConfirmed ? 4f : 3f;
            var lineThickness = isSelectedCandidate ? 2 : 1;

            DrawFacingGuide(drawList, standing, candidate.Rotation, color, lineThickness);
            DrawWorldPoint(drawList, standing, pointRadius, color, true);

            if (isSelectedCandidate || isConfirmed || labelCount < MaxCandidateLabels)
            {
                DrawWorldText(
                    drawList,
                    standing + new Vector3(0f, 1.6f, 0f),
                    BuildCandidateLabel(candidate, item.Distance, isSelectedCandidate, isConfirmed, selection),
                    isSelectedCandidate ? SelectedCandidateColor : isConfirmed ? ConfirmedColor : CandidateColor);
                labelCount++;
            }
        }

        if (scan.Candidates.Count > candidates.Count && TryWorldToScreen(playerPosition + new Vector3(0f, 2.5f, 0f), out var screen))
            drawList.AddText(screen, WarningColor, $"FPG overlay {candidates.Count}/{scan.Candidates.Count}");

        DrawTargetBlockLabels(drawList, session, playerPosition, drawDistance, recordedCandidateIds);
    }

    private static string BuildCandidateLabel(
        SpotCandidate candidate,
        float distanceToPlayer,
        bool isSelectedCandidate,
        bool isConfirmed,
        CandidateSelection? selection)
    {
        var status = isConfirmed
            ? "已记录"
            : isSelectedCandidate && selection is not null
                ? $"当前/{selection.ModeText}"
                : "未记录";
        var path = isSelectedCandidate && selection?.PathLengthMeters is { } pathLength
            ? $" path={pathLength:F1}m"
            : string.Empty;
        return $"候选 {status} p={distanceToPlayer:F1}m c={candidate.DistanceToTargetCenterMeters:F1}m{path} b={ShortBlockId(candidate.BlockId)}";
    }

    private static bool IsRecordedCandidate(
        SpotCandidate candidate,
        IReadOnlySet<string> recordedCandidateIds,
        IReadOnlySet<string> recordedCandidateFingerprints)
    {
        var candidateId = !string.IsNullOrWhiteSpace(candidate.SourceCandidateId)
            ? candidate.SourceCandidateId
            : candidate.CandidateFingerprint;
        return (!string.IsNullOrWhiteSpace(candidateId) && recordedCandidateIds.Contains(candidateId))
            || (!string.IsNullOrWhiteSpace(candidate.CandidateFingerprint)
                && recordedCandidateFingerprints.Contains(candidate.CandidateFingerprint));
    }

    private void DrawSurfaceDebug(
        ImDrawListPtr drawList,
        SpotWorkflowSession session,
        Vector3 playerPosition,
        float drawDistance,
        int triangleLimit)
    {
        var debug = session.NearbyDebugOverlay;
        if (debug is null)
            return;

        if (debug.TerritoryId != 0 && debug.TerritoryId != session.CurrentTerritoryId)
            return;

        var fishableDrawn = 0;
        var walkableDrawn = 0;
        var limit = Math.Clamp(triangleLimit, 1, 512);
        if (session.OverlayShowFishableDebug)
            fishableDrawn = DrawSurfaceDebugSet(
                drawList,
                debug.FishableTriangles,
                playerPosition,
                drawDistance,
                limit,
                "water",
                FishableDebugEdgeColor,
                FishableDebugFillColor,
                FishableDebugTextColor);

        if (session.OverlayShowWalkableDebug)
            walkableDrawn = DrawSurfaceDebugSet(
                drawList,
                debug.WalkableTriangles,
                playerPosition,
                drawDistance,
                limit,
                "walk",
                WalkableDebugEdgeColor,
                WalkableDebugHatchColor,
                WalkableDebugTextColor);

        var candidateDrawn = DrawNearbyDebugCandidates(drawList, debug.Candidates, playerPosition, drawDistance, limit);
        if (TryWorldToScreen(debug.PlayerPosition + new Vector3(0f, 3f, 0f), out var screen))
            drawList.AddText(
                screen,
                FishableDebugTextColor,
                $"FPG surfaces water {fishableDrawn}/{debug.FishableTriangles.Count} walk {walkableDrawn}/{debug.WalkableTriangles.Count} cand {candidateDrawn}/{debug.Candidates.Count} r={debug.RadiusMeters:F0}m");
    }

    private int DrawSurfaceDebugSet(
        ImDrawListPtr drawList,
        IReadOnlyList<DebugOverlayTriangle> source,
        Vector3 playerPosition,
        float drawDistance,
        int triangleLimit,
        string labelPrefix,
        uint edgeColor,
        uint surfaceColor,
        uint textColor)
    {
        if (source.Count == 0)
            return 0;

        var triangles = source
            .Select(triangle => new
            {
                Triangle = triangle,
                Distance = HorizontalDistance(triangle.Centroid, playerPosition),
            })
            .Where(item => item.Distance <= drawDistance)
            .OrderBy(item => item.Distance)
            .Take(triangleLimit)
            .ToList();

        foreach (var item in triangles)
        {
            var triangle = item.Triangle;
            if (labelPrefix == "water")
                DrawWorldTriangleFilled(drawList, triangle.A, triangle.B, triangle.C, surfaceColor);
            else
                DrawWorldTriangleHatch(drawList, triangle, surfaceColor);

            DrawWorldLine(drawList, triangle.A, triangle.B, edgeColor);
            DrawWorldLine(drawList, triangle.B, triangle.C, edgeColor);
            DrawWorldLine(drawList, triangle.C, triangle.A, edgeColor);
            DrawWorldPoint(drawList, triangle.Centroid, 2f, edgeColor, false);
        }

        var labelIndex = 0;
        foreach (var item in triangles.Take(MaxSurfaceDebugLabels))
        {
            labelIndex++;
            DrawWorldText(
                drawList,
                item.Triangle.Centroid + new Vector3(0f, 0.5f, 0f),
                $"{labelPrefix}{labelIndex} {FormatMaterial(item.Triangle.Material)} {item.Distance:F1}m",
                textColor);
        }

        return triangles.Count;
    }

    private int DrawNearbyDebugCandidates(
        ImDrawListPtr drawList,
        IReadOnlyList<ApproachCandidate> source,
        Vector3 playerPosition,
        float drawDistance,
        int candidateLimit)
    {
        if (source.Count == 0)
            return 0;

        var playerPoint = Point3.From(playerPosition);
        var candidates = source
            .Select(candidate => new
            {
                Candidate = candidate,
                Distance = candidate.Position.HorizontalDistanceTo(playerPoint),
            })
            .Where(item => item.Distance <= drawDistance)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Candidate.CandidateId, StringComparer.Ordinal)
            .Take(candidateLimit)
            .ToList();

        var labelCount = 0;
        foreach (var item in candidates)
        {
            var candidate = item.Candidate;
            var standing = ToVector3(candidate.Position);
            DrawFacingGuide(drawList, standing, candidate.Rotation, WarningColor);
            DrawWorldPoint(drawList, standing, 3f, WarningColor, true);

            if (labelCount < MaxCandidateLabels)
            {
                DrawWorldText(
                    drawList,
                    standing + new Vector3(0f, 1.6f, 0f),
                    $"near {ShortId(candidate.CandidateId)} {ShortBlockId(candidate.BlockId)} {candidate.Status}",
                    WarningColor);
                labelCount++;
            }
        }

        return candidates.Count;
    }

    private void DrawTerritoryCache(
        ImDrawListPtr drawList,
        SpotWorkflowSession session,
        Vector3 playerPosition,
        float drawDistance,
        int candidateLimit)
    {
        var blocks = session.CurrentTerritoryBlocks;
        if (blocks.Count == 0)
            return;

        IReadOnlySet<string> selectedSourceIds = session.OverlayShowCandidates
            ? (session.CurrentScan?.Candidates
                .Select(candidate => candidate.SourceCandidateId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal)
                ?? [])
            : [];
        var recordedCandidateIds = session.CurrentTerritoryRecordedCandidateIds;
        var playerPoint = Point3.From(playerPosition);
        var candidates = blocks
            .SelectMany(block => block.Candidates)
            .Where(candidate => !selectedSourceIds.Contains(candidate.CandidateId))
            .Select(candidate => new
            {
                Candidate = candidate,
                Distance = candidate.Position.HorizontalDistanceTo(playerPoint),
            })
            .Where(item => item.Distance <= drawDistance)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Candidate.CandidateId, StringComparer.Ordinal)
            .Take(candidateLimit)
            .ToList();

        foreach (var item in candidates)
        {
            var candidate = item.Candidate;
            var standing = ToVector3(candidate.Position);
            var isConfirmed = recordedCandidateIds.Contains(candidate.CandidateId);
            var color = isConfirmed ? ConfirmedColor : TerritoryCandidateColor;
            DrawFacingGuide(drawList, standing, candidate.Rotation, color);
            DrawWorldPoint(drawList, standing, isConfirmed ? 3f : 2f, color, true);
        }
    }

    private void DrawTargetBlockLabels(
        ImDrawListPtr drawList,
        SpotWorkflowSession session,
        Vector3 playerPosition,
        float drawDistance,
        IReadOnlySet<string> recordedCandidateIds)
    {
        if (session.CurrentTargetBlocks.Count == 0)
            return;

        var playerPoint = Point3.From(playerPosition);
        foreach (var item in session.CurrentTargetBlocks
            .Select(block => new
            {
                Block = block,
                Center = block.Center,
                Distance = block.Center.HorizontalDistanceTo(playerPoint),
            })
            .Where(item => item.Distance <= drawDistance)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Block.BlockId, StringComparer.Ordinal)
            .Take(32))
        {
            var confirmedCount = item.Block.Candidates.Count(candidate => recordedCandidateIds.Contains(candidate.CandidateId));
            var label = $"{ShortBlockId(item.Block.BlockId)} {confirmedCount}/{item.Block.Candidates.Count}";
            DrawWorldText(drawList, ToVector3(item.Center) + new Vector3(0f, 2f, 0f), label, BlockLabelColor);
        }
    }

    private void DrawWorldCircle(ImDrawListPtr drawList, Vector3 center, float radius, uint color)
    {
        var previous = center + new Vector3(radius, 0f, 0f);
        for (var index = 1; index <= CircleSegments; index++)
        {
            var angle = MathF.Tau * index / CircleSegments;
            var current = center + new Vector3(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
            DrawWorldLine(drawList, previous, current, color);
            previous = current;
        }
    }

    private void DrawWorldLine(ImDrawListPtr drawList, Vector3 start, Vector3 end, uint color, int thickness = 1)
    {
        if (!ClipLineToNearPlane(ref start, ref end))
            return;

        if (TryWorldToScreen(start, out var screenStart) && TryWorldToScreen(end, out var screenEnd))
            drawList.AddLine(screenStart, screenEnd, color, thickness);
    }

    private void DrawWorldTriangleHatch(ImDrawListPtr drawList, DebugOverlayTriangle triangle, uint color)
    {
        var maxLength = MathF.Max(
            HorizontalDistance(triangle.A, triangle.B),
            MathF.Max(
                HorizontalDistance(triangle.B, triangle.C),
                HorizontalDistance(triangle.C, triangle.A)));
        var stripeCount = Math.Clamp(
            (int)MathF.Ceiling(maxLength / DebugSurfaceHatchSpacingMeters),
            2,
            24);

        for (var index = 1; index <= stripeCount; index++)
        {
            var t = index / (stripeCount + 1f);
            var start = Vector3.Lerp(triangle.A, triangle.B, t);
            var end = Vector3.Lerp(triangle.A, triangle.C, t);
            DrawWorldLine(drawList, start, end, color);
        }
    }

    private void DrawWorldTriangleFilled(ImDrawListPtr drawList, Vector3 a, Vector3 b, Vector3 c, uint color)
    {
        if (TryWorldToScreen(a, out var screenA)
            && TryWorldToScreen(b, out var screenB)
            && TryWorldToScreen(c, out var screenC))
            drawList.AddTriangleFilled(screenA, screenB, screenC, color);
    }

    private void DrawFacingGuide(ImDrawListPtr drawList, Vector3 standing, float rotation, uint color, int thickness = 1)
    {
        var direction = new Vector3(MathF.Sin(rotation), 0f, MathF.Cos(rotation));
        DrawWorldLine(drawList, standing, standing + (direction * FacingGuideLengthMeters), color, thickness);
    }

    private void DrawWorldPoint(ImDrawListPtr drawList, Vector3 point, float radius, uint color, bool filled)
    {
        if (!TryWorldToScreen(point, out var screen))
            return;

        if (filled)
            drawList.AddCircleFilled(screen, radius, color);
        else
            drawList.AddCircle(screen, radius, color);
    }

    private void DrawWorldText(ImDrawListPtr drawList, Vector3 point, string text, uint color)
    {
        if (string.IsNullOrWhiteSpace(text) || !TryWorldToScreen(point, out var screen))
            return;

        drawList.AddText(screen, color, text);
    }

    private bool TryWorldToScreen(Vector3 world, out Vector2 screen)
    {
        screen = default;
        if (Vector4.Dot(new Vector4(world, 1f), nearPlane) >= 0f)
            return false;

        var projected = Vector4.Transform(world, viewProj);
        if (MathF.Abs(projected.W) <= 0.0001f)
            return false;

        var inverseW = 1f / projected.W;
        screen = new Vector2(
            0.5f * viewportSize.X * (1f + projected.X * inverseW),
            0.5f * viewportSize.Y * (1f - projected.Y * inverseW))
            + ImGuiHelpers.MainViewport.Pos;
        return true;
    }

    private bool ClipLineToNearPlane(ref Vector3 start, ref Vector3 end)
    {
        var startDistance = Vector4.Dot(new Vector4(start, 1f), nearPlane);
        var endDistance = Vector4.Dot(new Vector4(end, 1f), nearPlane);
        if (startDistance >= 0f && endDistance >= 0f)
            return false;

        if (startDistance > 0f || endDistance > 0f)
        {
            var delta = end - start;
            var normal = new Vector3(nearPlane.X, nearPlane.Y, nearPlane.Z);
            var denominator = Vector3.Dot(delta, normal);
            if (MathF.Abs(denominator) <= 0.0001f)
                return false;

            var ratio = -startDistance / denominator;
            var clipped = start + (ratio * delta);
            if (startDistance > 0f)
                start = clipped;
            else
                end = clipped;
        }

        return true;
    }

    private static Vector3 ToVector3(Point3 point) => new(point.X, point.Y, point.Z);

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        var dx = left.X - right.X;
        var dz = left.Z - right.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static string FormatMaterial(ulong material)
    {
        return "0x" + material.ToString("X");
    }

    private static string ShortBlockId(string blockId)
    {
        if (string.IsNullOrWhiteSpace(blockId))
            return "block";

        var index = blockId.LastIndexOf("_block_", StringComparison.Ordinal);
        return index >= 0 ? "b" + blockId[(index + "_block_".Length)..] : blockId;
    }

    private static string ShortId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "-";

        return value.Length <= 10 ? value : value[..10];
    }
}
