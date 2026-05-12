namespace FishingPointGenerator.Plugin.Services.Scanning;

[Flags]
internal enum ScenePrimitiveFlags
{
    None = 0,
    ForceUnwalkable = 1 << 0,
    FlyThrough = 1 << 1,
    Unlandable = 1 << 2,
    ForceWalkable = 1 << 3,
    Fishable = 1 << 4,
}

internal static class SceneMaterialFlags
{
    private static readonly ulong[] MaterialsFlyThrough =
    [
        0x100000,
        0x1000000,
        0x800000,
        0xB400,
    ];

    public static ScenePrimitiveFlags FromMaterial(ulong material)
    {
        var result = ScenePrimitiveFlags.None;

        foreach (var flyThrough in MaterialsFlyThrough)
        {
            if ((material & flyThrough) == flyThrough)
                result |= ScenePrimitiveFlags.FlyThrough;
        }

        if ((material & 0x200000) != 0)
            result |= ScenePrimitiveFlags.Unlandable;

        if ((material & 0x1F) == 0x11)
            result |= ScenePrimitiveFlags.Unlandable | ScenePrimitiveFlags.ForceUnwalkable;

        if ((material & 0x8000) != 0)
            result |= ScenePrimitiveFlags.Fishable;

        return result;
    }
}
