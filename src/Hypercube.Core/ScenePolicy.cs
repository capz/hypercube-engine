using System;

namespace Hypercube;

public sealed class CellReadiness
{
    private readonly bool[] collision = new bool[24], coarse = new bool[24];
    public int BlockedMoves;
    public void CollisionReady(int cell) => collision[cell] = true;
    public void CollisionMissing(int cell) => collision[cell] = false;
    public bool HasCollision(int cell)=>collision[cell];
    public void CoarseReady(int cell) => coarse[cell] = true;
    public bool CanEnter(int cell)
    {
        bool ready = cell >= 0 && cell < 24 && collision[cell] && coarse[cell];
        if (!ready) BlockedMoves++;
        return ready;
    }
}

public static class ScenePolicy
{
    public static int Lod(float distance, int previous)
    {
        // Separate enter/leave thresholds prevent boundary oscillation.
        if (previous == 0) return distance > 10 ? 1 : 0;
        if (previous == 1) return distance < 8 ? 0 : distance > 18 ? 2 : 1;
        return distance < 15 ? 1 : 2;
    }
    public static bool Visible(float minX, float minZ, float maxX, float maxZ, float x, float z, float radius)
        => maxX >= x - radius && minX <= x + radius && maxZ >= z - radius && minZ <= z + radius;
    public static int ParentFallback(AssetCatalog catalog, int id, Func<int, bool> ready)
    {
        for (int depth = 0; depth < 8 && id != 0; depth++)
        {
            if (ready(id)) return id;
            id = catalog.Get(id).Parent;
        }
        return 0;
    }
}
