using System.Text.Json;
using System.Text.Json.Serialization;
using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class SpotJsonStore
{
    public const string CatalogFileName = "fishing_spots.json";
    public const string ExportFileName = "FishingSpotApproachPoints.json";

    private readonly string rootDirectory;
    private readonly JsonSerializerOptions jsonOptions;

    public SpotJsonStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        this.rootDirectory = rootDirectory;
        jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public string GetCatalogPath()
    {
        return Path.Combine(rootDirectory, "data", "catalog", CatalogFileName);
    }

    public string GetLegacySpotScanPath(SpotKey key)
    {
        ValidateKey(key);
        return Path.Combine(rootDirectory, "data", "scans", $"territory_{key.TerritoryId}", $"spot_{key.FishingSpotId}.scan.json");
    }

    public string GetLegacyLedgerPath(SpotKey key)
    {
        ValidateKey(key);
        return Path.Combine(rootDirectory, "data", "labels", $"territory_{key.TerritoryId}", $"spot_{key.FishingSpotId}.ledger.json");
    }

    public string GetLegacyReviewPath(SpotKey key)
    {
        ValidateKey(key);
        return Path.Combine(rootDirectory, "data", "review", $"territory_{key.TerritoryId}", $"spot_{key.FishingSpotId}.review.json");
    }

    public string GetReportPath(SpotKey key)
    {
        ValidateKey(key);
        return Path.Combine(rootDirectory, "data", "reports", $"territory_{key.TerritoryId}", $"spot_{key.FishingSpotId}.validation.json");
    }

    public string GetExportPath()
    {
        return Path.Combine(rootDirectory, "data", "exports", ExportFileName);
    }

    public FishingSpotCatalogDocument LoadCatalog()
    {
        var path = GetCatalogPath();
        if (!File.Exists(path))
            return new FishingSpotCatalogDocument();

        return JsonSerializer.Deserialize<FishingSpotCatalogDocument>(File.ReadAllText(path), jsonOptions)
            ?? new FishingSpotCatalogDocument();
    }

    public void SaveCatalog(FishingSpotCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        WriteJson(GetCatalogPath(), document with { GeneratedAt = DateTimeOffset.UtcNow });
    }

    public SpotScanDocument LoadLegacySpotScan(SpotKey key)
    {
        var path = GetLegacySpotScanPath(key);
        if (!File.Exists(path))
            return new SpotScanDocument { Key = key };

        return JsonSerializer.Deserialize<SpotScanDocument>(File.ReadAllText(path), jsonOptions)
            ?? new SpotScanDocument { Key = key };
    }

    public bool DeleteLegacySpotScan(SpotKey key)
    {
        return DeleteFile(GetLegacySpotScanPath(key));
    }

    public bool DeleteLegacyLedger(SpotKey key)
    {
        return DeleteFile(GetLegacyLedgerPath(key));
    }

    public bool DeleteLegacyReview(SpotKey key)
    {
        return DeleteFile(GetLegacyReviewPath(key));
    }

    public SpotLabelLedger LoadLegacyLedger(SpotKey key)
    {
        var path = GetLegacyLedgerPath(key);
        if (!File.Exists(path))
            return new SpotLabelLedger { Key = key };

        return JsonSerializer.Deserialize<SpotLabelLedger>(File.ReadAllText(path), jsonOptions)
            ?? new SpotLabelLedger { Key = key };
    }

    public SpotReviewDocument LoadLegacyReview(SpotKey key)
    {
        var path = GetLegacyReviewPath(key);
        if (!File.Exists(path))
            return new SpotReviewDocument { Key = key };

        return JsonSerializer.Deserialize<SpotReviewDocument>(File.ReadAllText(path), jsonOptions)
            ?? new SpotReviewDocument { Key = key };
    }

    public void SaveLegacyReview(SpotReviewDocument review)
    {
        ArgumentNullException.ThrowIfNull(review);
        ValidateKey(review.Key);

        WriteJson(GetLegacyReviewPath(review.Key), review with { UpdatedAt = DateTimeOffset.UtcNow });
    }

    public SpotValidationReport LoadReport(SpotKey key)
    {
        var path = GetReportPath(key);
        if (!File.Exists(path))
            return new SpotValidationReport { Key = key };

        return JsonSerializer.Deserialize<SpotValidationReport>(File.ReadAllText(path), jsonOptions)
            ?? new SpotValidationReport { Key = key };
    }

    public void SaveReport(SpotValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ValidateKey(report.Key);

        WriteJson(GetReportPath(report.Key), report with { GeneratedAt = DateTimeOffset.UtcNow });
    }

    public void SaveExport(List<ExportedApproachPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        WriteJson(GetExportPath(), points);
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

    private static void ValidateKey(SpotKey key)
    {
        if (!key.IsValid)
            throw new ArgumentException("SpotKey 必须包含 TerritoryId 和 FishingSpotId。", nameof(key));
    }
}
