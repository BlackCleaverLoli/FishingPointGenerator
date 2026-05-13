using System.Reflection;
using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Plugin.Services.Scanning;

internal sealed class TerritoryGeometryCache
{
    private readonly ICurrentTerritoryScanner scanner;
    private TerritorySurveyDocument? cachedSurvey;

    public TerritoryGeometryCache(ICurrentTerritoryScanner scanner)
    {
        this.scanner = scanner;
    }

    public string ScannerName => scanner.Name;

    public string ScannerVersion =>
        scanner.GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? scanner.GetType().Assembly.GetName().Version?.ToString()
        ?? string.Empty;

    public TerritorySurveyDocument ScanCurrentTerritory(bool forceRefresh)
    {
        if (!forceRefresh && cachedSurvey is not null)
            return cachedSurvey;

        cachedSurvey = scanner.ScanCurrentTerritory();
        return cachedSurvey;
    }

    public TerritoryScanCapture CaptureCurrentTerritory()
    {
        return scanner.CaptureCurrentTerritory();
    }

    public TerritorySurveyDocument ScanCapturedTerritory(
        TerritoryScanCapture capture,
        IProgress<TerritoryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        cachedSurvey = scanner.ScanCapturedTerritory(capture, progress, cancellationToken);
        return cachedSurvey;
    }

    public NearbyScanDebugResult DebugScanNearby(float radiusMeters)
    {
        return scanner.DebugScanNearby(radiusMeters);
    }

    public void Clear()
    {
        cachedSurvey = null;
    }
}
