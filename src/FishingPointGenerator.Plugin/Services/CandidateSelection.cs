using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Plugin.Services;

internal enum CandidateSelectionMode
{
    Filtered,
    FlyableDistance,
    WalkReachable,
}

internal sealed record CandidateSelection(
    SpotCandidate Candidate,
    CandidateSelectionMode Mode,
    bool CanFly,
    float? PathLengthMeters,
    float? DistanceToPlayerMeters,
    float DistanceToTargetCenterMeters,
    int CheckedCandidateCount,
    bool IsTerritoryRecorded,
    string Note)
{
    public string ModeText => Mode switch
    {
        CandidateSelectionMode.Filtered => IsTerritoryRecorded ? "已过滤候选：冲突待覆盖" : "已过滤候选",
        CandidateSelectionMode.FlyableDistance => IsTerritoryRecorded ? "可飞：冲突待覆盖/距玩家" : "可飞：未记录/距玩家",
        CandidateSelectionMode.WalkReachable => IsTerritoryRecorded ? "不可飞：冲突待覆盖/距玩家" : "不可飞：未记录/距玩家",
        _ => Mode.ToString(),
    };
}
