namespace FishingPointGenerator.Core.Geometry;

internal sealed class DisjointSet
{
    private readonly int[] parent;
    private readonly int[] rank;

    internal DisjointSet(int count)
    {
        parent = new int[count];
        rank = new int[count];
        for (var i = 0; i < count; i++)
            parent[i] = i;
    }

    internal int Find(int value)
    {
        if (parent[value] != value)
            parent[value] = Find(parent[value]);
        return parent[value];
    }

    internal void Union(int left, int right)
    {
        var leftRoot = Find(left);
        var rightRoot = Find(right);
        if (leftRoot == rightRoot)
            return;

        if (rank[leftRoot] < rank[rightRoot])
        {
            parent[leftRoot] = rightRoot;
        }
        else if (rank[leftRoot] > rank[rightRoot])
        {
            parent[rightRoot] = leftRoot;
        }
        else
        {
            parent[rightRoot] = leftRoot;
            rank[leftRoot]++;
        }
    }
}
