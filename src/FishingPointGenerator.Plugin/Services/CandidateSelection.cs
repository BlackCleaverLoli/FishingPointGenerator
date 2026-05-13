using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Plugin.Services;

internal enum CandidateSelectionMode
{
    FlyableDistance,
    WalkReachable,
    WalkReachabilityPending,
    NoReachableFallback,
    NavmeshUnavailableFallback,
    NoPlayerFallback,
}

internal sealed record CandidateSelection(
    SpotCandidate Candidate,
    CandidateSelectionMode Mode,
    bool CanFly,
    float? PathLengthMeters,
    float? DistanceToPlayerMeters,
    float DistanceToTargetCenterMeters,
    int CheckedCandidateCount,
    string Note)
{
    public string ModeText => Mode switch
    {
        CandidateSelectionMode.FlyableDistance => "可飞：按距中心",
        CandidateSelectionMode.WalkReachable => "不可飞：步行可达",
        CandidateSelectionMode.WalkReachabilityPending => "不可飞：检查可达性",
        CandidateSelectionMode.NoReachableFallback => "不可飞：未找到可达点",
        CandidateSelectionMode.NavmeshUnavailableFallback => "不可飞：vnavmesh 不可用",
        CandidateSelectionMode.NoPlayerFallback => "不可飞：无玩家位置",
        _ => Mode.ToString(),
    };
}
