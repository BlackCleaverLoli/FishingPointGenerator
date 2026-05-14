using Dalamud.Configuration;

namespace FishingPointGenerator.Plugin.Services;

[Serializable]
public sealed class PluginConfiguration : IPluginConfiguration
{
    internal const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public bool AutoRecordCastsEnabled { get; set; } = true;
    public bool OverlayEnabled { get; set; } = true;
    public bool OverlayShowCandidates { get; set; } = true;
    public bool OverlayShowTerritoryCache { get; set; } = true;
    public bool OverlayShowTargetRadius { get; set; } = true;
    public bool OverlayShowFishableDebug { get; set; } = true;
    public bool OverlayShowWalkableDebug { get; set; } = true;
    public float CastBlockSnapDistanceMeters { get; set; } = 6f;
    public float CastBlockFillRangeMeters { get; set; } = 120f;
    public float OverlayMaxDistanceMeters { get; set; } = 90f;
    public int OverlayCandidateLimit { get; set; } = 160;
}
