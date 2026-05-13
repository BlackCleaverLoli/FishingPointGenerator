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
        CandidateSelectionMode.FlyableDistance => "可飞：按距中心",
        CandidateSelectionMode.WalkReachable => "不可飞：步行可达",
        _ => Mode.ToString(),
    };
}
