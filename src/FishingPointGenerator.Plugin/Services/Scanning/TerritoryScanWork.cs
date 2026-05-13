using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Plugin.Services.Scanning;

internal sealed record TerritoryScanCapture(
    uint CurrentTerritoryId,
    string TerritoryName,
    ActiveLayoutScene Scene)
{
    public uint TerritoryId => Scene.TerritoryId != 0 ? Scene.TerritoryId : CurrentTerritoryId;
}

internal sealed record TerritoryScanProgress(
    string Stage,
    int Current,
    int Total,
    string Message)
{
    public float Fraction => Total <= 0 ? 0f : Math.Clamp((float)Current / Total, 0f, 1f);
}

internal sealed record TerritoryScanWorkResult(
    bool Success,
    TerritorySurveyDocument? Survey,
    IReadOnlyList<SurveyBlock> Blocks,
    string Message);
