using System.Text.Json;
using System.Text.Json.Serialization;
using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public sealed class SurveyJsonStore
{
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

        WriteJson(GetGeneratedSurveyPath(document.TerritoryId), document with { GeneratedAt = DateTimeOffset.UtcNow });
    }

    private void WriteJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(value, jsonOptions) + "\n");
    }
}
