using System.Numerics;

namespace FishingPointGenerator.Core.Models;

public readonly record struct Point3(float X, float Y, float Z)
{
    public Vector3 ToVector3() => new(X, Y, Z);

    public static Point3 From(Vector3 value) => new(value.X, value.Y, value.Z);

    public float DistanceTo(Point3 other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        var dz = Z - other.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    public float HorizontalDistanceTo(Point3 other)
    {
        var dx = X - other.X;
        var dz = Z - other.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    public Point3 Add(Vector3 value) => new(X + value.X, Y + value.Y, Z + value.Z);
}
