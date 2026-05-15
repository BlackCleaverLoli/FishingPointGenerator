using System.Security.Cryptography;
using System.Text;
using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public static class SpotFingerprint
{
    public const float StandingPositionQuantumMeters = 0.5f;
    public const float RotationQuantumRadians = 0.05f;

    public static string CreateCandidateFingerprint(SpotKey key, Point3 position, float rotation)
    {
        return CreateFingerprint("sp_", key, position, rotation);
    }

    public static string CreateTerritoryCandidateFingerprint(uint territoryId, Point3 position, float rotation)
    {
        if (territoryId == 0)
            throw new ArgumentException("TerritoryId 必须非 0。", nameof(territoryId));

        var normalizedRotation = NormalizeRotation(rotation);
        var payload = string.Join(
            "|",
            territoryId.ToString(),
            Quantize(position.X, StandingPositionQuantumMeters).ToString(),
            Quantize(position.Y, StandingPositionQuantumMeters).ToString(),
            Quantize(position.Z, StandingPositionQuantumMeters).ToString(),
            Quantize(normalizedRotation, RotationQuantumRadians).ToString());

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return "tc_" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    public static string CreateApproachPointId(SpotKey key, Point3 position, float rotation)
    {
        return CreateFingerprint("ap_", key, position, rotation);
    }

    private static string CreateFingerprint(string prefix, SpotKey key, Point3 position, float rotation)
    {
        if (!key.IsValid)
            throw new ArgumentException("SpotKey 必须包含 TerritoryId 和 FishingSpotId。", nameof(key));

        var normalizedRotation = NormalizeRotation(rotation);
        var payload = string.Join(
            "|",
            key.TerritoryId.ToString(),
            key.FishingSpotId.ToString(),
            Quantize(position.X, StandingPositionQuantumMeters).ToString(),
            Quantize(position.Y, StandingPositionQuantumMeters).ToString(),
            Quantize(position.Z, StandingPositionQuantumMeters).ToString(),
            Quantize(normalizedRotation, RotationQuantumRadians).ToString());

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return prefix + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static int Quantize(float value, float quantum)
    {
        return (int)Math.Round(value / quantum, MidpointRounding.AwayFromZero);
    }

    private static float NormalizeRotation(float rotation)
    {
        var normalized = rotation % MathF.Tau;
        return normalized < 0f ? normalized + MathF.Tau : normalized;
    }
}
