using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Plugin.Services;

internal interface ICurrentTerritoryScanner
{
    string Name { get; }
    bool IsPlaceholder { get; }
    TerritorySurveyDocument ScanCurrentTerritory();
}
