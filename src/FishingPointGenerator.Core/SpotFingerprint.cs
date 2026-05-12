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
        return "sp_" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
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
