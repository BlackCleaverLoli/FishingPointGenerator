using FishingPointGenerator.Core.Models;
using FishingPointGenerator.Plugin.Services.Scanning;

namespace FishingPointGenerator.Plugin.Services;

internal interface ICurrentTerritoryScanner
{
    string Name { get; }
    bool IsPlaceholder { get; }
    TerritorySurveyDocument ScanCurrentTerritory();
    NearbyScanDebugResult DebugScanNearby(float radiusMeters);
}
