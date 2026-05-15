using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FishingPointGenerator.Core;
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
    private const uint DisabledColor = 0xff8080ff;
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
    private const float CandidateClickRadiusPixels = 22f;
    private const float CandidateSelectionDragThresholdPixels = 8f;
    private const uint CandidateSelectionFillColor = 0x334080ff;
    private const uint CandidateSelectionEdgeColor = 0xff4080ff;
    private const int MaxSurfaceDebugLabels = 4;
    private const int MaxCandidateLabels = 18;

    private Matrix4x4 viewProj;
    private Vector4 nearPlane;
    private Vector2 viewportSize;
    private bool previousLeftMouseDown;
    private bool candidateSelectionActive;
    private Vector2 candidateSelectionStart;
    private Vector2 candidateSelectionEnd;

    public void Draw(SpotWorkflowSession session)
    {
        if (!session.OverlayEnabled && !session.OverlayPointDisableMode)
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
        var windowFlags = ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoTitleBar
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoInputs;
        ImGui.Begin(
            "fpg_world_overlay",
            windowFlags);
        ImGui.SetWindowSize(ImGui.GetIO().DisplaySize);

        var drawList = ImGui.GetWindowDrawList();
        var canDrawCurrentTerritory = session.SelectedTerritoryIsCurrent;
        var showCandidates = session.OverlayShowCandidates || session.OverlayPointDisableMode;
        if (session.CurrentTarget is not null && canDrawCurrentTerritory)
            DrawTarget(drawList, session, player.Position);
        if (session.OverlayShowFishableDebug || session.OverlayShowWalkableDebug)
            DrawSurfaceDebug(drawList, session, player.Position, drawDistance, Math.Min(candidateLimit, 512));
        if (session.OverlayShowTerritoryCache && !showCandidates && canDrawCurrentTerritory)
            DrawTerritoryCache(drawList, session, player.Position, drawDistance, candidateLimit);
        if (showCandidates && canDrawCurrentTerritory)
            DrawCandidates(drawList, session, player.Position, drawDistance, candidateLimit);

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
        var survey = session.CurrentTerritorySurvey;
        if (survey is null || survey.Candidates.Count == 0)
            return;

        var territoryId = survey.TerritoryId != 0 ? survey.TerritoryId : session.SelectedTerritoryId;
        var recordedOwnersByKey = BuildRecordedCandidateOwnerIndex(session.CurrentTerritoryMaintenance);
        var riskOwnersByKey = BuildMixedRiskCandidateOwnerIndex(session.CurrentTerritoryMaintenance);
        var disabledOwnersByKey = BuildDisabledCandidateOwnerIndex(session.CurrentTerritoryMaintenance);

        var recordedTotal = survey.Candidates.Count(candidate => HasRecordedOwner(candidate, territoryId, recordedOwnersByKey));
        var riskTotal = survey.Candidates.Count(candidate => HasRiskOwner(candidate, territoryId, riskOwnersByKey));
        var disabledTotal = survey.Candidates.Count(candidate => HasDisabledOwner(candidate, territoryId, disabledOwnersByKey));
        var visibleCandidates = survey.Candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Distance = candidate.Position.HorizontalDistanceTo(Point3.From(playerPosition)),
            })
            .Where(item => item.Distance <= drawDistance)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Candidate.CandidateId, StringComparer.Ordinal)
            .ToList();
        var candidates = visibleCandidates
            .Take(candidateLimit)
            .ToList();

        var mousePosition = ImGui.GetIO().MousePos;
        var leftMouseDown = IsGameLeftMouseDown();
        var viewportMin = ImGuiHelpers.MainViewport.Pos;
        var viewportMax = viewportMin + viewportSize;
        var mouseInViewport = mousePosition.X >= viewportMin.X
            && mousePosition.Y >= viewportMin.Y
            && mousePosition.X <= viewportMax.X
            && mousePosition.Y <= viewportMax.Y;
        var canStartPointDisableSelection = session.OverlayPointDisableMode
            && leftMouseDown
            && !previousLeftMouseDown
            && !session.IsOverlayPointDisableMouseBlockedByUi(mousePosition)
            && mouseInViewport;
        var finishPointDisableSelection = session.OverlayPointDisableMode
            && !leftMouseDown
            && previousLeftMouseDown
            && candidateSelectionActive;
        if (!session.OverlayPointDisableMode)
            candidateSelectionActive = false;
        if (canStartPointDisableSelection)
        {
            candidateSelectionActive = true;
            candidateSelectionStart = mousePosition;
            candidateSelectionEnd = mousePosition;
        }
        else if (candidateSelectionActive && leftMouseDown)
        {
            candidateSelectionEnd = mousePosition;
        }

        List<OverlayCandidateScreenPoint> selectableCandidates = session.OverlayPointDisableMode
            ? new List<OverlayCandidateScreenPoint>(candidates.Count)
            : [];
        var labelCount = 0;
        foreach (var item in candidates)
        {
            var candidate = item.Candidate;
            var standing = ToVector3(candidate.Position);
            var isConfirmed = HasRecordedOwner(candidate, territoryId, recordedOwnersByKey);
            var isRisk = HasRiskOwner(candidate, territoryId, riskOwnersByKey);
            var isDisabled = HasDisabledOwner(candidate, territoryId, disabledOwnersByKey);
            var color = isDisabled ? DisabledColor : isConfirmed ? ConfirmedColor : isRisk ? WarningColor : CandidateColor;
            var pointRadius = isDisabled ? 4f : isConfirmed ? 4f : isRisk ? 3.5f : 3f;

            if (session.OverlayPointDisableMode && TryWorldToScreen(standing, out var candidateScreen))
                selectableCandidates.Add(new OverlayCandidateScreenPoint(candidate, candidateScreen));

            DrawFacingGuide(drawList, standing, candidate.Rotation, color);
            DrawWorldPoint(drawList, standing, pointRadius, color, true);

            if (isDisabled || isConfirmed || isRisk || labelCount < MaxCandidateLabels)
            {
                var recordedOwners = GetRecordedOwners(candidate, territoryId, recordedOwnersByKey);
                var riskOwners = GetRiskOwners(candidate, territoryId, riskOwnersByKey);
                var disabledOwners = GetDisabledOwners(candidate, territoryId, disabledOwnersByKey);
                DrawWorldText(
                    drawList,
                    standing + new Vector3(0f, 1.6f, 0f),
                    BuildCandidateLabel(candidate, item.Distance, recordedOwners, riskOwners, disabledOwners),
                    color);
                labelCount++;
            }
        }

        if (candidateSelectionActive && leftMouseDown)
            DrawCandidateSelectionRect(drawList, candidateSelectionStart, candidateSelectionEnd);

        if (TryWorldToScreen(playerPosition + new Vector3(0f, 2.5f, 0f), out var screen))
        {
            var clippedText = visibleCandidates.Count > candidates.Count
                ? $" 显示截断 {candidates.Count}/{visibleCandidates.Count}"
                : string.Empty;
            var pointDisableText = session.OverlayPointDisableMode ? " 点选/框选禁用/恢复" : string.Empty;
            drawList.AddText(screen, WarningColor, $"FPG overlay 已记录 {recordedTotal}/{survey.Candidates.Count} 风险 {riskTotal} 屏蔽 {disabledTotal}{pointDisableText}{clippedText}");
        }

        DrawTerritoryBlockLabels(drawList, session, playerPosition, drawDistance, territoryId, recordedOwnersByKey, riskOwnersByKey, disabledOwnersByKey);
        if (finishPointDisableSelection)
        {
            var selectedCandidates = SelectOverlayCandidates(selectableCandidates, candidateSelectionStart, candidateSelectionEnd, mousePosition);
            candidateSelectionActive = false;
            if (selectedCandidates.Count > 0)
                session.ToggleOverlayCandidatesDisabled(selectedCandidates);
        }

        previousLeftMouseDown = leftMouseDown;
    }

    private static IReadOnlyList<ApproachCandidate> SelectOverlayCandidates(
        IReadOnlyList<OverlayCandidateScreenPoint> candidates,
        Vector2 selectionStart,
        Vector2 selectionEnd,
        Vector2 mousePosition)
    {
        if (Vector2.DistanceSquared(selectionStart, selectionEnd) < CandidateSelectionDragThresholdPixels * CandidateSelectionDragThresholdPixels)
        {
            ApproachCandidate? clickedCandidate = null;
            var clickedCandidateDistanceSq = CandidateClickRadiusPixels * CandidateClickRadiusPixels;
            foreach (var item in candidates)
            {
                var distanceSq = Vector2.DistanceSquared(mousePosition, item.ScreenPosition);
                if (distanceSq > clickedCandidateDistanceSq)
                    continue;

                clickedCandidate = item.Candidate;
                clickedCandidateDistanceSq = distanceSq;
            }

            return clickedCandidate is null ? [] : [clickedCandidate];
        }

        var min = new Vector2(
            MathF.Min(selectionStart.X, selectionEnd.X),
            MathF.Min(selectionStart.Y, selectionEnd.Y));
        var max = new Vector2(
            MathF.Max(selectionStart.X, selectionEnd.X),
            MathF.Max(selectionStart.Y, selectionEnd.Y));
        return candidates
            .Where(item =>
                item.ScreenPosition.X >= min.X
                && item.ScreenPosition.Y >= min.Y
                && item.ScreenPosition.X <= max.X
                && item.ScreenPosition.Y <= max.Y)
            .Select(item => item.Candidate)
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.CandidateId))
            .GroupBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static void DrawCandidateSelectionRect(ImDrawListPtr drawList, Vector2 start, Vector2 end)
    {
        var min = new Vector2(MathF.Min(start.X, end.X), MathF.Min(start.Y, end.Y));
        var max = new Vector2(MathF.Max(start.X, end.X), MathF.Max(start.Y, end.Y));
        drawList.AddRectFilled(min, max, CandidateSelectionFillColor);
        drawList.AddRect(min, max, CandidateSelectionEdgeColor, 0f, ImDrawFlags.None, 1.5f);
    }

    private static string BuildCandidateLabel(
        ApproachCandidate candidate,
        float distanceToPlayer,
        IReadOnlyList<CandidateRecordOwner> recordedOwners,
        IReadOnlyList<CandidateRecordOwner> riskOwners,
        IReadOnlyList<CandidateRecordOwner> disabledOwners)
    {
        var status = "未记录";
        if (disabledOwners.Count > 0)
            status = $"屏蔽:{FormatRecordedOwners(disabledOwners)}";
        else if (recordedOwners.Count > 0)
            status = $"已记录:{FormatRecordedOwners(recordedOwners)}";
        else if (riskOwners.Count > 0)
            status = $"风险:{FormatRecordedOwners(riskOwners)}";
        if (disabledOwners.Count > 0 && recordedOwners.Count > 0)
            status += $" 已记录:{FormatRecordedOwners(recordedOwners)}";
        if (recordedOwners.Count > 0 && riskOwners.Count > 0)
            status += $" 风险:{FormatRecordedOwners(riskOwners)}";
        var reachability = candidate.Reachability switch
        {
            CandidateReachability.Flyable => "可飞",
            CandidateReachability.WalkReachable => "可走",
            _ => "未知",
        };
        var path = candidate.PathLengthMeters is { } pathLength ? $" path={pathLength:F1}m" : string.Empty;
        return $"候选 {status}/{reachability} p={distanceToPlayer:F1}m{path} b={ShortBlockId(candidate.BlockId)}";
    }

    private static bool HasRecordedOwner(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> recordedOwnersByKey)
    {
        return HasOwner(candidate, territoryId, recordedOwnersByKey, includeBlock: false);
    }

    private static bool HasRiskOwner(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> riskOwnersByKey)
    {
        return HasOwner(candidate, territoryId, riskOwnersByKey, includeBlock: true);
    }

    private static bool HasDisabledOwner(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> disabledOwnersByKey)
    {
        return HasOwner(candidate, territoryId, disabledOwnersByKey, includeBlock: false);
    }

    private static bool HasOwner(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> ownersByKey,
        bool includeBlock)
    {
        if (ownersByKey.Count == 0)
            return false;

        return HasOwnerKey(ownersByKey, candidate.CandidateId)
            || (includeBlock && HasOwnerKey(ownersByKey, candidate.BlockId))
            || HasOwnerKey(ownersByKey, GetTerritoryCandidateFingerprint(candidate, territoryId));
    }

    private static bool HasOwnerKey(
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> ownersByKey,
        string key)
    {
        return !string.IsNullOrWhiteSpace(key) && ownersByKey.ContainsKey(key);
    }

    private static IReadOnlyList<CandidateRecordOwner> GetRecordedOwners(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> recordedOwnersByKey)
    {
        if (recordedOwnersByKey.Count == 0)
            return [];

        var owners = new Dictionary<uint, CandidateRecordOwner>();
        AddRecordedOwners(owners, recordedOwnersByKey, candidate.CandidateId);
        AddRecordedOwners(owners, recordedOwnersByKey, GetTerritoryCandidateFingerprint(candidate, territoryId));
        return owners.Values
            .OrderBy(owner => owner.FishingSpotId)
            .ToList();
    }

    private static IReadOnlyList<CandidateRecordOwner> GetRiskOwners(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> riskOwnersByKey)
    {
        if (riskOwnersByKey.Count == 0)
            return [];

        var owners = new Dictionary<uint, CandidateRecordOwner>();
        AddRecordedOwners(owners, riskOwnersByKey, candidate.CandidateId);
        AddRecordedOwners(owners, riskOwnersByKey, candidate.BlockId);
        AddRecordedOwners(owners, riskOwnersByKey, GetTerritoryCandidateFingerprint(candidate, territoryId));
        return owners.Values
            .OrderBy(owner => owner.FishingSpotId)
            .ToList();
    }

    private static IReadOnlyList<CandidateRecordOwner> GetDisabledOwners(
        ApproachCandidate candidate,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> disabledOwnersByKey)
    {
        if (disabledOwnersByKey.Count == 0)
            return [];

        var owners = new Dictionary<uint, CandidateRecordOwner>();
        AddRecordedOwners(owners, disabledOwnersByKey, candidate.CandidateId);
        AddRecordedOwners(owners, disabledOwnersByKey, candidate.BlockId);
        AddRecordedOwners(owners, disabledOwnersByKey, GetTerritoryCandidateFingerprint(candidate, territoryId));
        return owners.Values
            .OrderBy(owner => owner.FishingSpotId)
            .ToList();
    }

    private static Dictionary<string, List<CandidateRecordOwner>> BuildRecordedCandidateOwnerIndex(
        TerritoryMaintenanceDocument? maintenance)
    {
        var ownersByKey = new Dictionary<string, List<CandidateRecordOwner>>(StringComparer.Ordinal);
        if (maintenance is null)
            return ownersByKey;

        foreach (var spot in maintenance.Spots)
        {
            var owner = new CandidateRecordOwner(spot.FishingSpotId, spot.Name);
            foreach (var point in spot.ApproachPoints.Where(point => point.Status == ApproachPointStatus.Confirmed))
            {
                AddRecordedOwner(ownersByKey, point.SourceCandidateId, owner);
                AddRecordedOwner(ownersByKey, point.SourceCandidateFingerprint, owner);
                if (maintenance.TerritoryId != 0)
                {
                    AddRecordedOwner(
                        ownersByKey,
                        SpotFingerprint.CreateTerritoryCandidateFingerprint(
                            maintenance.TerritoryId,
                            point.Position,
                            point.Rotation),
                        owner);
                }
            }
        }

        return ownersByKey;
    }

    private static Dictionary<string, List<CandidateRecordOwner>> BuildMixedRiskCandidateOwnerIndex(
        TerritoryMaintenanceDocument? maintenance)
    {
        var ownersByKey = new Dictionary<string, List<CandidateRecordOwner>>(StringComparer.Ordinal);
        if (maintenance is null)
            return ownersByKey;

        foreach (var spot in maintenance.Spots)
        {
            var owner = new CandidateRecordOwner(spot.FishingSpotId, spot.Name);
            foreach (var record in spot.MixedRiskBlocks)
            {
                AddRecordedOwner(ownersByKey, record.BlockId, owner);
                foreach (var candidateId in record.CandidateIds.Where(id => !string.IsNullOrWhiteSpace(id)))
                    AddRecordedOwner(ownersByKey, candidateId, owner);
            }
        }

        return ownersByKey;
    }

    private static Dictionary<string, List<CandidateRecordOwner>> BuildDisabledCandidateOwnerIndex(
        TerritoryMaintenanceDocument? maintenance)
    {
        var ownersByKey = new Dictionary<string, List<CandidateRecordOwner>>(StringComparer.Ordinal);
        if (maintenance is null)
            return ownersByKey;

        foreach (var spot in maintenance.Spots)
        {
            var owner = new CandidateRecordOwner(spot.FishingSpotId, spot.Name);
            foreach (var point in spot.ApproachPoints.Where(IsEffectiveDisabledApproachPoint))
            {
                AddRecordedOwner(ownersByKey, point.SourceCandidateId, owner);
                AddRecordedOwner(ownersByKey, point.SourceCandidateFingerprint, owner);
                if (maintenance.TerritoryId != 0)
                {
                    AddRecordedOwner(
                        ownersByKey,
                        SpotFingerprint.CreateTerritoryCandidateFingerprint(
                            maintenance.TerritoryId,
                            point.Position,
                            point.Rotation),
                        owner);
                }
            }
        }

        return ownersByKey;
    }

    private static void AddRecordedOwners(
        Dictionary<uint, CandidateRecordOwner> owners,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> ownersByKey,
        string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !ownersByKey.TryGetValue(key, out var matchedOwners))
            return;

        foreach (var owner in matchedOwners)
            owners.TryAdd(owner.FishingSpotId, owner);
    }

    private static void AddRecordedOwner(
        Dictionary<string, List<CandidateRecordOwner>> ownersByKey,
        string key,
        CandidateRecordOwner owner)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!ownersByKey.TryGetValue(key, out var owners))
        {
            owners = [];
            ownersByKey.Add(key, owners);
        }

        if (!owners.Any(existing => existing.FishingSpotId == owner.FishingSpotId))
            owners.Add(owner);
    }

    private static bool IsEffectiveDisabledApproachPoint(ApproachPoint point)
    {
        return point.Status == ApproachPointStatus.Disabled
            && point.SourceKind != ApproachPointSourceKind.AutoCastFill;
    }

    private static bool IsGameLeftMouseDown()
    {
        try
        {
            return InputManager.IsLeftMouseDown();
        }
        catch
        {
            return false;
        }
    }

    private static string GetTerritoryCandidateFingerprint(ApproachCandidate candidate, uint territoryId)
    {
        var effectiveTerritoryId = candidate.TerritoryId != 0 ? candidate.TerritoryId : territoryId;
        return effectiveTerritoryId == 0
            ? string.Empty
            : SpotFingerprint.CreateTerritoryCandidateFingerprint(
                effectiveTerritoryId,
                candidate.Position,
                candidate.Rotation);
    }

    private static string FormatRecordedOwners(IReadOnlyList<CandidateRecordOwner> owners)
    {
        return string.Join(
            ",",
            owners.Take(4).Select(owner => string.IsNullOrWhiteSpace(owner.Name)
                ? owner.FishingSpotId.ToString()
                : $"{owner.Name}#{owner.FishingSpotId}"));
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

        var territoryId = session.CurrentTerritorySurvey?.TerritoryId ?? session.SelectedTerritoryId;
        var recordedOwnersByKey = BuildRecordedCandidateOwnerIndex(session.CurrentTerritoryMaintenance);
        var riskOwnersByKey = BuildMixedRiskCandidateOwnerIndex(session.CurrentTerritoryMaintenance);
        var disabledOwnersByKey = BuildDisabledCandidateOwnerIndex(session.CurrentTerritoryMaintenance);
        var playerPoint = Point3.From(playerPosition);
        var candidates = blocks
            .SelectMany(block => block.Candidates)
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
            var isConfirmed = HasRecordedOwner(candidate, territoryId, recordedOwnersByKey);
            var isRisk = HasRiskOwner(candidate, territoryId, riskOwnersByKey);
            var isDisabled = HasDisabledOwner(candidate, territoryId, disabledOwnersByKey);
            var color = isDisabled ? DisabledColor : isConfirmed ? ConfirmedColor : isRisk ? WarningColor : TerritoryCandidateColor;
            DrawFacingGuide(drawList, standing, candidate.Rotation, color);
            DrawWorldPoint(drawList, standing, isDisabled ? 3f : isConfirmed ? 3f : isRisk ? 2.5f : 2f, color, true);
        }
    }

    private void DrawTerritoryBlockLabels(
        ImDrawListPtr drawList,
        SpotWorkflowSession session,
        Vector3 playerPosition,
        float drawDistance,
        uint territoryId,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> recordedOwnersByKey,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> riskOwnersByKey,
        IReadOnlyDictionary<string, List<CandidateRecordOwner>> disabledOwnersByKey)
    {
        if (session.CurrentTerritoryBlocks.Count == 0)
            return;

        var playerPoint = Point3.From(playerPosition);
        foreach (var item in session.CurrentTerritoryBlocks
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
            var confirmedCount = item.Block.Candidates.Count(candidate =>
                HasRecordedOwner(candidate, territoryId, recordedOwnersByKey));
            var riskCount = item.Block.Candidates.Count(candidate =>
                HasRiskOwner(candidate, territoryId, riskOwnersByKey));
            var disabledCount = item.Block.Candidates.Count(candidate =>
                HasDisabledOwner(candidate, territoryId, disabledOwnersByKey));
            var label = $"{ShortBlockId(item.Block.BlockId)} {confirmedCount}/{item.Block.Candidates.Count}";
            if (riskCount > 0)
                label += $" r{riskCount}";
            if (disabledCount > 0)
                label += $" x{disabledCount}";
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

    private sealed record CandidateRecordOwner(uint FishingSpotId, string Name);
    private sealed record OverlayCandidateScreenPoint(ApproachCandidate Candidate, Vector2 ScreenPosition);
}
