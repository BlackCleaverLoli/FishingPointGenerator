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
            WriteIndented = true,
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

        var loaded = JsonSerializer.Deserialize<TerritoryMaintenanceDocument>(File.ReadAllText(path), jsonOptions)
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
            document with { UpdatedAt = DateTimeOffset.UtcNow });
    }

    private TerritoryMaintenanceDocument? LoadTerritoryFile(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<TerritoryMaintenanceDocument>(File.ReadAllText(path), jsonOptions);
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
}
