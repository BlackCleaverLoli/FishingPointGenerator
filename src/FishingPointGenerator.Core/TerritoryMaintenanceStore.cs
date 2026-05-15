using System.Text.Json;
using System.Text.Json.Serialization;
using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class TerritoryMaintenanceStore
{
    private readonly string rootDirectory;
    private readonly JsonSerializerOptions jsonOptions;

    public TerritoryMaintenanceStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        this.rootDirectory = rootDirectory;
        jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public string GetTerritoryMaintenancePath(uint territoryId)
    {
        if (territoryId == 0)
            throw new ArgumentException("TerritoryId 必须非 0。", nameof(territoryId));

        return Path.Combine(rootDirectory, "data", "maintenance", $"territory_{territoryId}.json");
    }

    public TerritoryMaintenanceDocument LoadTerritory(uint territoryId, string territoryName = "")
    {
        var path = GetTerritoryMaintenancePath(territoryId);
        if (!File.Exists(path))
        {
            return new TerritoryMaintenanceDocument
            {
                TerritoryId = territoryId,
                TerritoryName = territoryName,
            };
        }

        var loaded = LoadTerritoryFile(path, territoryId, territoryName)
            ?? new TerritoryMaintenanceDocument { TerritoryId = territoryId, TerritoryName = territoryName };

        return loaded with
        {
            TerritoryId = loaded.TerritoryId != 0 ? loaded.TerritoryId : territoryId,
            TerritoryName = string.IsNullOrWhiteSpace(loaded.TerritoryName) ? territoryName : loaded.TerritoryName,
        };
    }

    public IReadOnlyList<TerritoryMaintenanceDocument> LoadAllTerritories()
    {
        var directory = Path.Combine(rootDirectory, "data", "maintenance");
        if (!Directory.Exists(directory))
            return [];

        return Directory
            .EnumerateFiles(directory, "territory_*.json", SearchOption.TopDirectoryOnly)
            .Select(LoadTerritoryFile)
            .Where(document => document is not null)
            .Select(document => document!)
            .OrderBy(document => document.TerritoryId)
            .ToList();
    }

    public void SaveTerritory(TerritoryMaintenanceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.TerritoryId == 0)
            throw new ArgumentException("TerritoryId 必须非 0。", nameof(document));

        WriteJson(
            GetTerritoryMaintenancePath(document.TerritoryId),
            ToSlimDocument(document));
    }

    public bool DeleteTerritory(uint territoryId)
    {
        return DeleteFile(GetTerritoryMaintenancePath(territoryId));
    }

    private TerritoryMaintenanceDocument? LoadTerritoryFile(string path)
    {
        return LoadTerritoryFile(path, 0, string.Empty);
    }

    private TerritoryMaintenanceDocument? LoadTerritoryFile(string path, uint fallbackTerritoryId, string fallbackTerritoryName)
    {
        try
        {
            var json = File.ReadAllText(path);
            using var parsed = JsonDocument.Parse(json);
            if (IsSlimDocument(parsed.RootElement))
            {
                var slim = JsonSerializer.Deserialize<SlimTerritoryMaintenanceDocument>(json, jsonOptions);
                return slim is null
                    ? null
                    : FromSlimDocument(slim, fallbackTerritoryId, fallbackTerritoryName);
            }

            var legacy = JsonSerializer.Deserialize<TerritoryMaintenanceDocument>(json, jsonOptions);
            return legacy is null
                ? null
                : NormalizeLoadedDocument(legacy, fallbackTerritoryId, fallbackTerritoryName);
        }
        catch
        {
            return null;
        }
    }

    private void WriteJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(value, jsonOptions) + "\n");
    }

    private static bool DeleteFile(string path)
    {
        if (!File.Exists(path))
            return false;

        File.Delete(path);
        return true;
    }

    private static bool IsSlimDocument(JsonElement root)
    {
        if (root.TryGetProperty("version", out var version)
            && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var value)
            && value >= 2)
            return true;

        if (!root.TryGetProperty("spots", out var spots) || spots.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var spot in spots.EnumerateArray())
        {
            if (spot.TryGetProperty("points", out _))
                return true;
        }

        return false;
    }

    private static TerritoryMaintenanceDocument NormalizeLoadedDocument(
        TerritoryMaintenanceDocument document,
        uint fallbackTerritoryId,
        string fallbackTerritoryName)
    {
        var territoryId = document.TerritoryId != 0 ? document.TerritoryId : fallbackTerritoryId;
        return document with
        {
            TerritoryId = territoryId,
            TerritoryName = string.IsNullOrWhiteSpace(document.TerritoryName) ? fallbackTerritoryName : document.TerritoryName,
            Spots = document.Spots
                .Select(spot => NormalizeLoadedSpot(spot, territoryId))
                .Where(ShouldKeepSpot)
                .OrderBy(spot => spot.FishingSpotId)
                .ToList(),
        };
    }

    private static SpotMaintenanceRecord NormalizeLoadedSpot(SpotMaintenanceRecord spot, uint territoryId)
    {
        return spot with
        {
            ReviewNote = string.Empty,
            ApproachPoints = spot.ApproachPoints
                .Where(IsPersistedPointTrusted)
                .Select(point => NormalizeLoadedPoint(point, territoryId, spot.FishingSpotId))
                .OrderBy(point => point.PointId, StringComparer.Ordinal)
                .ToList(),
            MixedRiskBlocks = NormalizeMixedRiskBlocks(spot.MixedRiskBlocks),
            Evidence = [],
        };
    }

    private static ApproachPoint NormalizeLoadedPoint(ApproachPoint point, uint territoryId, uint fishingSpotId)
    {
        var candidateId = FirstNonEmpty(point.SourceCandidateId, point.SourceCandidateFingerprint);
        return point with
        {
            PointId = string.IsNullOrWhiteSpace(point.PointId) && territoryId != 0 && fishingSpotId != 0
                ? SpotFingerprint.CreateApproachPointId(new SpotKey(territoryId, fishingSpotId), point.Position, point.Rotation)
                : point.PointId,
            SourceKind = point.Status == ApproachPointStatus.Disabled
                ? ApproachPointSourceKind.Candidate
                : point.SourceKind,
            SourceCandidateId = candidateId,
            SourceCandidateFingerprint = candidateId,
            SourceSurfaceGroupId = string.Empty,
            SourceScanId = string.Empty,
            SourceScannerVersion = string.Empty,
            EvidenceIds = [],
            Note = string.Empty,
        };
    }

    private static bool IsPersistedPointTrusted(ApproachPoint point)
    {
        return point.Status switch
        {
            ApproachPointStatus.Confirmed => point.SourceKind is ApproachPointSourceKind.Candidate
                or ApproachPointSourceKind.AutoCastFill,
            ApproachPointStatus.Disabled => point.SourceKind != ApproachPointSourceKind.AutoCastFill,
            _ => false,
        };
    }

    private static List<MixedRiskBlockRecord> NormalizeMixedRiskBlocks(IEnumerable<MixedRiskBlockRecord> records)
    {
        return records
            .Select(record => record with
            {
                SurfaceGroupId = string.Empty,
                CandidateIds = record.CandidateIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList(),
                ConflictingFishingSpotIds = record.ConflictingFishingSpotIds
                    .Where(id => id != 0)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList(),
                Note = string.Empty,
            })
            .Where(record =>
                !string.IsNullOrWhiteSpace(record.BlockId)
                || record.CandidateIds.Count > 0
                || record.ConflictingFishingSpotIds.Count > 0)
            .OrderBy(record => record.BlockId, StringComparer.Ordinal)
            .ThenBy(record => string.Join(",", record.CandidateIds), StringComparer.Ordinal)
            .ToList();
    }

    private static bool ShouldKeepSpot(SpotMaintenanceRecord spot)
    {
        return spot.ReviewDecision != default
            || spot.ApproachPoints.Count > 0
            || spot.MixedRiskBlocks.Count > 0;
    }

    private static SlimTerritoryMaintenanceDocument ToSlimDocument(TerritoryMaintenanceDocument document)
    {
        return new SlimTerritoryMaintenanceDocument
        {
            TerritoryId = document.TerritoryId,
            TerritoryName = string.IsNullOrWhiteSpace(document.TerritoryName) ? null : document.TerritoryName,
            Spots = document.Spots
                .Select(ToSlimSpot)
                .Where(spot => spot is not null)
                .Select(spot => spot!)
                .OrderBy(spot => spot.FishingSpotId)
                .ToList(),
        };
    }

    private static SlimSpotMaintenanceRecord? ToSlimSpot(SpotMaintenanceRecord spot)
    {
        var points = spot.ApproachPoints
            .Where(IsPersistedPointTrusted)
            .Select(ToSlimPoint)
            .Where(point => point is not null)
            .Select(point => point!)
            .OrderBy(point => point.CandidateId, StringComparer.Ordinal)
            .ThenBy(point => point.BlockId, StringComparer.Ordinal)
            .ThenBy(point => point.Position[0])
            .ThenBy(point => point.Position[1])
            .ThenBy(point => point.Position[2])
            .ThenBy(point => point.Rotation)
            .ToList();
        var risks = NormalizeMixedRiskBlocks(spot.MixedRiskBlocks)
            .Select(ToSlimMixedRisk)
            .ToList();

        if (spot.ReviewDecision == default && points.Count == 0 && risks.Count == 0)
            return null;

        return new SlimSpotMaintenanceRecord
        {
            FishingSpotId = spot.FishingSpotId,
            ReviewDecision = spot.ReviewDecision == default ? null : spot.ReviewDecision,
            Points = points.Count == 0 ? null : points,
            MixedRisks = risks.Count == 0 ? null : risks,
        };
    }

    private static SlimApproachPoint? ToSlimPoint(ApproachPoint point)
    {
        var candidateId = FirstNonEmpty(point.SourceCandidateId, point.SourceCandidateFingerprint);
        if (string.IsNullOrWhiteSpace(candidateId) && point.Status != ApproachPointStatus.Disabled)
            return null;

        return new SlimApproachPoint
        {
            CandidateId = string.IsNullOrWhiteSpace(candidateId) ? null : candidateId,
            BlockId = string.IsNullOrWhiteSpace(point.SourceBlockId) ? null : point.SourceBlockId,
            Position = [point.Position.X, point.Position.Y, point.Position.Z],
            Rotation = point.Rotation,
            State = point.Status,
            Source = ToSlimPointSource(point),
        };
    }

    private static SlimMixedRiskBlock ToSlimMixedRisk(MixedRiskBlockRecord record)
    {
        return new SlimMixedRiskBlock
        {
            BlockId = string.IsNullOrWhiteSpace(record.BlockId) ? null : record.BlockId,
            CandidateIds = record.CandidateIds.Count == 0 ? null : record.CandidateIds,
            ConflictingFishingSpotIds = record.ConflictingFishingSpotIds.Count == 0 ? null : record.ConflictingFishingSpotIds,
            RepeatCount = record.ResetPointCount == 0 ? null : record.ResetPointCount,
        };
    }

    private static TerritoryMaintenanceDocument FromSlimDocument(
        SlimTerritoryMaintenanceDocument slim,
        uint fallbackTerritoryId,
        string fallbackTerritoryName)
    {
        var territoryId = slim.TerritoryId != 0 ? slim.TerritoryId : fallbackTerritoryId;
        var territoryName = string.IsNullOrWhiteSpace(slim.TerritoryName) ? fallbackTerritoryName : slim.TerritoryName;
        return new TerritoryMaintenanceDocument
        {
            TerritoryId = territoryId,
            TerritoryName = territoryName ?? string.Empty,
            Spots = slim.Spots
                .Select(spot => FromSlimSpot(spot, territoryId))
                .Where(ShouldKeepSpot)
                .OrderBy(spot => spot.FishingSpotId)
                .ToList(),
        };
    }

    private static SpotMaintenanceRecord FromSlimSpot(SlimSpotMaintenanceRecord slim, uint territoryId)
    {
        return new SpotMaintenanceRecord
        {
            FishingSpotId = slim.FishingSpotId,
            ReviewDecision = slim.ReviewDecision ?? default,
            ApproachPoints = (slim.Points ?? [])
                .Select(point => FromSlimPoint(point, territoryId, slim.FishingSpotId))
                .Where(point => point is not null)
                .Select(point => point!)
                .OrderBy(point => point.PointId, StringComparer.Ordinal)
                .ToList(),
            MixedRiskBlocks = (slim.MixedRisks ?? [])
                .Select(FromSlimMixedRisk)
                .Where(record =>
                    !string.IsNullOrWhiteSpace(record.BlockId)
                    || record.CandidateIds.Count > 0
                    || record.ConflictingFishingSpotIds.Count > 0)
                .OrderBy(record => record.BlockId, StringComparer.Ordinal)
                .ThenBy(record => string.Join(",", record.CandidateIds), StringComparer.Ordinal)
                .ToList(),
        };
    }

    private static ApproachPoint? FromSlimPoint(SlimApproachPoint slim, uint territoryId, uint fishingSpotId)
    {
        if (slim.Position.Length < 3)
            return null;

        var position = new Point3(slim.Position[0], slim.Position[1], slim.Position[2]);
        var pointId = territoryId != 0 && fishingSpotId != 0
            ? SpotFingerprint.CreateApproachPointId(new SpotKey(territoryId, fishingSpotId), position, slim.Rotation)
            : string.Empty;
        var sourceKind = FromSlimPointSource(slim.Source);
        var candidateId = slim.CandidateId ?? string.Empty;
        return new ApproachPoint
        {
            PointId = pointId,
            Position = position,
            Rotation = slim.Rotation,
            Status = slim.State,
            SourceKind = slim.State == ApproachPointStatus.Disabled
                ? ApproachPointSourceKind.Candidate
                : sourceKind,
            SourceCandidateId = candidateId,
            SourceCandidateFingerprint = candidateId,
            SourceBlockId = slim.BlockId ?? string.Empty,
        };
    }

    private static MixedRiskBlockRecord FromSlimMixedRisk(SlimMixedRiskBlock slim)
    {
        return new MixedRiskBlockRecord
        {
            BlockId = slim.BlockId ?? string.Empty,
            CandidateIds = (slim.CandidateIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList(),
            ConflictingFishingSpotIds = (slim.ConflictingFishingSpotIds ?? [])
                .Where(id => id != 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList(),
            ResetPointCount = slim.RepeatCount ?? 0,
        };
    }

    private static SlimPointSource ToSlimPointSource(ApproachPoint point)
    {
        if (point.Status == ApproachPointStatus.Disabled)
            return SlimPointSource.ManualDisable;

        return point.SourceKind == ApproachPointSourceKind.AutoCastFill
            ? SlimPointSource.Chain
            : SlimPointSource.Cast;
    }

    private static ApproachPointSourceKind FromSlimPointSource(SlimPointSource source)
    {
        return source == SlimPointSource.Chain
            ? ApproachPointSourceKind.AutoCastFill
            : ApproachPointSourceKind.Candidate;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private sealed record SlimTerritoryMaintenanceDocument
    {
        public int Version { get; init; } = 2;
        public uint TerritoryId { get; init; }
        public string? TerritoryName { get; init; }
        public List<SlimSpotMaintenanceRecord> Spots { get; init; } = [];
    }

    private sealed record SlimSpotMaintenanceRecord
    {
        public uint FishingSpotId { get; init; }
        public SpotReviewDecision? ReviewDecision { get; init; }
        public List<SlimApproachPoint>? Points { get; init; }
        public List<SlimMixedRiskBlock>? MixedRisks { get; init; }
    }

    private sealed record SlimApproachPoint
    {
        public string? CandidateId { get; init; }
        public string? BlockId { get; init; }
        public float[] Position { get; init; } = [];
        public float Rotation { get; init; }
        public ApproachPointStatus State { get; init; }
        public SlimPointSource Source { get; init; }
    }

    private sealed record SlimMixedRiskBlock
    {
        public string? BlockId { get; init; }
        public List<string>? CandidateIds { get; init; }
        public List<uint>? ConflictingFishingSpotIds { get; init; }
        public int? RepeatCount { get; init; }
    }

    private enum SlimPointSource
    {
        Cast,
        Chain,
        ManualDisable,
    }
}
