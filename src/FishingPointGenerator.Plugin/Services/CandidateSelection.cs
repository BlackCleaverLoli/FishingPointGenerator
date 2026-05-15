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
    string Note)
{
    public string ModeText => Mode switch
    {
        CandidateSelectionMode.Filtered => "已过滤候选",
        CandidateSelectionMode.FlyableDistance => "可飞：未记录/距玩家",
        CandidateSelectionMode.WalkReachable => "不可飞：未记录/距玩家",
        _ => Mode.ToString(),
    };
}
