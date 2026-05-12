using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class SurveyAnalyzer
{
    private readonly SurveyBlockOptions options;

    public SurveyAnalyzer(SurveyBlockOptions? options = null)
    {
        this.options = options ?? new SurveyBlockOptions();
    }

    public IReadOnlyList<SurveyBlockState> Analyze(
        IEnumerable<SurveyBlock> blocks,
        IEnumerable<FishingSpotLabel> labels)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(labels);

        var labelLookup = labels
            .GroupBy(label => label.BlockId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var states = blocks
            .Select(block =>
            {
                labelLookup.TryGetValue(block.BlockId, out var blockLabels);
                IReadOnlyList<FishingSpotLabel> labelsForBlock = blockLabels is null
                    ? Array.Empty<FishingSpotLabel>()
                    : blockLabels;

                return AnalyzeBlock(block, labelsForBlock);
            })
            .ToList();

        if (options.MixedBlockQuarantinePaddingMeters <= 0f)
            return states;

        var mixedBlocks = states
            .Where(state => state.Status == SurveyBlockStatus.Mixed)
            .Select(state => state.Block)
            .ToList();

        if (mixedBlocks.Count == 0)
            return states;

        return states
            .Select(state => state.Status == SurveyBlockStatus.Unlabeled && IsNearMixedBlock(state.Block, mixedBlocks)
                ? state with { Status = SurveyBlockStatus.Quarantined }
                : state)
            .ToList();
    }

    public SurveyRecommendation? RecommendNext(
        IEnumerable<SurveyBlockState> states,
        Point3? playerPosition = null)
    {
        ArgumentNullException.ThrowIfNull(states);

        var stateList = states.ToList();
        return RecommendFromStatus(stateList, SurveyBlockStatus.Unlabeled, SurveyRecommendationReason.UnlabeledBlock, playerPosition)
            ?? RecommendFromStatus(stateList, SurveyBlockStatus.Quarantined, SurveyRecommendationReason.MixedBoundary, playerPosition)
            ?? RecommendFromStatus(stateList, SurveyBlockStatus.Mixed, SurveyRecommendationReason.MixedBoundary, playerPosition)
            ?? RecommendWeakCoverage(stateList, playerPosition);
    }

    private static SurveyBlockState AnalyzeBlock(SurveyBlock block, IReadOnlyList<FishingSpotLabel> labels)
    {
        var acceptedLabels = labels
            .Where(label => label.Status is LabelStatus.Accepted or LabelStatus.ManualAccepted)
            .Where(label => label.FishingSpotId != 0)
            .ToList();

        if (acceptedLabels.Count > 0)
        {
            var fishingSpotIds = acceptedLabels
                .Select(label => label.FishingSpotId)
                .ToHashSet();

            return new SurveyBlockState
            {
                Block = block,
                Status = fishingSpotIds.Count == 1 ? SurveyBlockStatus.SingleSpot : SurveyBlockStatus.Mixed,
                FishingSpotIds = fishingSpotIds,
                LabelCount = acceptedLabels.Count,
            };
        }

        if (labels.Any(label => label.Status == LabelStatus.Ignored)
            || (block.Candidates.Count > 0 && block.Candidates.All(candidate => candidate.Status == CandidateStatus.Ignored)))
        {
            return new SurveyBlockState
            {
                Block = block,
                Status = SurveyBlockStatus.Ignored,
                FishingSpotIds = new HashSet<uint>(),
                LabelCount = labels.Count,
            };
        }

        if (block.Candidates.Count > 0 && block.Candidates.All(candidate => candidate.Status == CandidateStatus.Quarantined))
        {
            return new SurveyBlockState
            {
                Block = block,
                Status = SurveyBlockStatus.Quarantined,
                FishingSpotIds = new HashSet<uint>(),
                LabelCount = labels.Count,
            };
        }

        return new SurveyBlockState
        {
            Block = block,
            Status = SurveyBlockStatus.Unlabeled,
            FishingSpotIds = new HashSet<uint>(),
            LabelCount = labels.Count,
        };
    }

    private bool IsNearMixedBlock(SurveyBlock block, IReadOnlyList<SurveyBlock> mixedBlocks)
    {
        foreach (var mixedBlock in mixedBlocks)
        {
            if (string.Equals(block.BlockId, mixedBlock.BlockId, StringComparison.Ordinal))
                continue;

            foreach (var candidate in block.Candidates)
            {
                foreach (var mixedCandidate in mixedBlock.Candidates)
                {
                    if (candidate.Position.HorizontalDistanceTo(mixedCandidate.Position) <= options.MixedBlockQuarantinePaddingMeters)
                        return true;
                }
            }
        }

        return false;
    }

    private static SurveyRecommendation? RecommendFromStatus(
        IReadOnlyList<SurveyBlockState> states,
        SurveyBlockStatus status,
        SurveyRecommendationReason reason,
        Point3? playerPosition)
    {
        return states
            .Where(state => state.Status == status)
            .Where(state => state.Block.Candidates.Count > 0)
            .Select(state => CreateRecommendation(state, reason, playerPosition))
            .Where(recommendation => recommendation.Candidate is not null)
            .OrderBy(recommendation => recommendation.DistanceMeters)
            .ThenBy(recommendation => recommendation.BlockId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static SurveyRecommendation? RecommendWeakCoverage(
        IReadOnlyList<SurveyBlockState> states,
        Point3? playerPosition)
    {
        return states
            .Where(state => state.Status == SurveyBlockStatus.SingleSpot)
            .Where(state => state.LabelCount <= 1)
            .Where(state => state.Block.Candidates.Count > 0)
            .Select(state => CreateRecommendation(state, SurveyRecommendationReason.WeakCoverage, playerPosition))
            .Where(recommendation => recommendation.Candidate is not null)
            .OrderBy(recommendation => recommendation.DistanceMeters)
            .ThenBy(recommendation => recommendation.BlockId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static SurveyRecommendation CreateRecommendation(
        SurveyBlockState state,
        SurveyRecommendationReason reason,
        Point3? playerPosition)
    {
        var candidate = PickRepresentativeCandidate(state.Block, playerPosition);
        return new SurveyRecommendation
        {
            Reason = reason,
            BlockId = state.Block.BlockId,
            Candidate = candidate,
            DistanceMeters = candidate is null || playerPosition is null
                ? 0f
                : candidate.Position.HorizontalDistanceTo(playerPosition.Value),
        };
    }

    private static ApproachCandidate? PickRepresentativeCandidate(SurveyBlock block, Point3? playerPosition)
    {
        var candidates = block.Candidates
            .Where(candidate => candidate.Status is not CandidateStatus.Ignored and not CandidateStatus.Quarantined)
            .ToList();

        if (candidates.Count == 0)
            candidates = block.Candidates.ToList();

        if (playerPosition is not null)
        {
            return candidates
                .OrderBy(candidate => candidate.Position.HorizontalDistanceTo(playerPosition.Value))
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
