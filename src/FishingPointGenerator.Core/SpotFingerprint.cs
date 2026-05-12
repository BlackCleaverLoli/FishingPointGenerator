using System.Security.Cryptography;
using System.Text;
using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core;

public static class SpotFingerprint
{
    public const float StandingPositionQuantumMeters = 0.5f;
    public const float TargetPointQuantumMeters = 1f;

    public static string CreateCandidateFingerprint(SpotKey key, Point3 position, Point3 targetPoint)
    {
        if (!key.IsValid)
            throw new ArgumentException("SpotKey must include a territory and fishing spot id.", nameof(key));

        var payload = string.Join(
            "|",
            key.TerritoryId.ToString(),
            key.FishingSpotId.ToString(),
            Quantize(position.X, StandingPositionQuantumMeters).ToString(),
            Quantize(position.Y, StandingPositionQuantumMeters).ToString(),
            Quantize(position.Z, StandingPositionQuantumMeters).ToString(),
            Quantize(targetPoint.X, TargetPointQuantumMeters).ToString(),
            Quantize(targetPoint.Y, TargetPointQuantumMeters).ToString(),
            Quantize(targetPoint.Z, TargetPointQuantumMeters).ToString());

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return "sp_" + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private static int Quantize(float value, float quantum)
    {
        return (int)Math.Round(value / quantum, MidpointRounding.AwayFromZero);
    }
}
