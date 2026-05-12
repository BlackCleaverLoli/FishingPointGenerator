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

    public string GetScanPath(SpotKey key)
    {
        ValidateKey(key);
        return Path.Combine(rootDirectory, "data", "scans", $"territory_{key.TerritoryId}", $"spot_{key.FishingSpotId}.scan.json");
    }

    public string GetLedgerPath(SpotKey key)
    {
        ValidateKey(key);
        return Path.Combine(rootDirectory, "data", "labels", $"territory_{key.TerritoryId}", $"spot_{key.FishingSpotId}.ledger.json");
    }

    public string GetReviewPath(SpotKey key)
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

    public SpotScanDocument LoadScan(SpotKey key)
    {
        var path = GetScanPath(key);
        if (!File.Exists(path))
            return new SpotScanDocument { Key = key };

        return JsonSerializer.Deserialize<SpotScanDocument>(File.ReadAllText(path), jsonOptions)
            ?? new SpotScanDocument { Key = key };
    }

    public void SaveScan(SpotScanDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateKey(document.Key);

        WriteJson(GetScanPath(document.Key), document with { GeneratedAt = DateTimeOffset.UtcNow });
    }

    public SpotLabelLedger LoadLedger(SpotKey key)
    {
        var path = GetLedgerPath(key);
        if (!File.Exists(path))
            return new SpotLabelLedger { Key = key };

        return JsonSerializer.Deserialize<SpotLabelLedger>(File.ReadAllText(path), jsonOptions)
            ?? new SpotLabelLedger { Key = key };
    }

    public void SaveLedger(SpotLabelLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ValidateKey(ledger.Key);

        WriteJson(GetLedgerPath(ledger.Key), ledger with { UpdatedAt = DateTimeOffset.UtcNow });
    }

    public SpotReviewDocument LoadReview(SpotKey key)
    {
        var path = GetReviewPath(key);
        if (!File.Exists(path))
            return new SpotReviewDocument { Key = key };

        return JsonSerializer.Deserialize<SpotReviewDocument>(File.ReadAllText(path), jsonOptions)
            ?? new SpotReviewDocument { Key = key };
    }

    public void SaveReview(SpotReviewDocument review)
    {
        ArgumentNullException.ThrowIfNull(review);
        ValidateKey(review.Key);

        WriteJson(GetReviewPath(review.Key), review with { UpdatedAt = DateTimeOffset.UtcNow });
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

    public void SaveExport(ExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        WriteJson(GetExportPath(), document with { GeneratedAt = DateTimeOffset.UtcNow });
    }

    private void WriteJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(value, jsonOptions) + "\n");
    }

    private static void ValidateKey(SpotKey key)
    {
        if (!key.IsValid)
            throw new ArgumentException("SpotKey 必须包含 TerritoryId 和 FishingSpotId。", nameof(key));
    }
}
