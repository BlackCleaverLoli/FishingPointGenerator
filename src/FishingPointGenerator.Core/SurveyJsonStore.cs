using System.Text.Json;
using System.Text.Json.Serialization;
using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class SurveyJsonStore
{
    public const string ExportFileName = "FishingSpotApproachPoints.json";

    private readonly string rootDirectory;
    private readonly JsonSerializerOptions jsonOptions;

    public SurveyJsonStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        this.rootDirectory = rootDirectory;
        jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public string GetGeneratedSurveyPath(uint territoryId)
    {
        return Path.Combine(rootDirectory, "data", "generated", $"territory_{territoryId}.json");
    }

    public string GetLabelsPath(uint territoryId)
    {
        return Path.Combine(rootDirectory, "data", "labels", $"territory_{territoryId}.labels.json");
    }

    public string GetExportPath()
    {
        return Path.Combine(rootDirectory, "data", "exports", ExportFileName);
    }

    public TerritorySurveyDocument LoadGeneratedSurvey(uint territoryId)
    {
        var path = GetGeneratedSurveyPath(territoryId);
        if (!File.Exists(path))
            return new TerritorySurveyDocument { TerritoryId = territoryId };

        return JsonSerializer.Deserialize<TerritorySurveyDocument>(File.ReadAllText(path), jsonOptions)
            ?? new TerritorySurveyDocument { TerritoryId = territoryId };
    }

    public void SaveGeneratedSurvey(TerritorySurveyDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        WriteJson(GetGeneratedSurveyPath(document.TerritoryId), document);
    }

    public TerritoryLabelsDocument LoadLabels(uint territoryId)
    {
        var path = GetLabelsPath(territoryId);
        if (!File.Exists(path))
            return new TerritoryLabelsDocument { TerritoryId = territoryId };

        return JsonSerializer.Deserialize<TerritoryLabelsDocument>(File.ReadAllText(path), jsonOptions)
            ?? new TerritoryLabelsDocument { TerritoryId = territoryId };
    }

    public void SaveLabels(TerritoryLabelsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        WriteJson(GetLabelsPath(document.TerritoryId), document with { UpdatedAt = DateTimeOffset.UtcNow });
    }

    public ExportDocument LoadExport()
    {
        var path = GetExportPath();
        if (!File.Exists(path))
            return new ExportDocument();

        return JsonSerializer.Deserialize<ExportDocument>(File.ReadAllText(path), jsonOptions)
            ?? new ExportDocument();
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
}
