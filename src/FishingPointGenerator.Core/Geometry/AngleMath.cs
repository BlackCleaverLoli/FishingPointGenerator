using System.Numerics;
using FishingPointGenerator.Core.Models;

namespace FishingPointGenerator.Core.Geometry;

public static class AngleMath
{
    public static float NormalizeRotation(float rotation)
    {
        var normalized = rotation % MathF.Tau;
        return normalized < 0f ? normalized + MathF.Tau : normalized;
    }

    public static float AngularDistance(float left, float right)
    {
        var delta = MathF.Abs(NormalizeRotation(left) - NormalizeRotation(right));
        return delta > MathF.PI ? MathF.Tau - delta : delta;
    }

    public static Vector3 RotationToDirection(float rotation)
    {
        var direction = new Vector3(MathF.Sin(rotation), 0f, MathF.Cos(rotation));
        return direction.LengthSquared() > 0.0001f ? Vector3.Normalize(direction) : Vector3.UnitZ;
    }

    public static float RotationFromTo(Point3 from, Point3 to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        if ((dx * dx) + (dz * dz) <= 0.0001f)
            return 0f;

        return NormalizeRotation(MathF.Atan2(dx, dz));
    }
}
