using FishingPointGenerator.Core.Models;
using FishingPointGenerator.Plugin.Services.Scanning;

namespace FishingPointGenerator.Plugin.Services;

internal interface ICurrentTerritoryScanner
{
    string Name { get; }
    bool IsPlaceholder { get; }
    TerritoryScanCapture CaptureCurrentTerritory();
    TerritorySurveyDocument ScanCapturedTerritory(
        TerritoryScanCapture capture,
        IProgress<TerritoryScanProgress>? progress,
        CancellationToken cancellationToken);
    TerritorySurveyDocument ScanCurrentTerritory();
    NearbyScanDebugResult DebugScanNearby(float radiusMeters);
}
