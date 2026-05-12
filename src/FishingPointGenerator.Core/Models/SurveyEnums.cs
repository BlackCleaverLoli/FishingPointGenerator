namespace FishingPointGenerator.Core.Models;

public enum CandidateStatus
{
    Unlabeled,
    ManualAccepted,
    Ignored,
    Quarantined,
}

public enum LabelStatus
{
    Accepted,
    ManualAccepted,
    Ignored,
}

public enum SurveyBlockStatus
{
    Unlabeled,
    SingleSpot,
    Mixed,
    Ignored,
    Quarantined,
}

public enum SurveyRecommendationReason
{
    UnlabeledBlock,
    MixedBoundary,
    WeakCoverage,
}
