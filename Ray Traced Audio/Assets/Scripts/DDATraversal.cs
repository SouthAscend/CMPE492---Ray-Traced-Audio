using System.Collections;
using UnityEngine;

/// <summary>
/// Stateless utility class that marches a ray through a voxel grid using the
/// Digital Differential Analyzer (DDA) algorithm.
/// </summary>
public static class DDATraversal
{
    public struct MarchResult
    {
        public bool hitSolid;      // Ray hit a solid voxel
        public bool hitProximity;  // Ray came within proximityRadius of the source
        public bool escaped;       // Ray left the grid without hitting anything
        public Vector3 hitPoint;   // World-space position of the hit
        public Vector3 normal;     // Surface normal of the face that was hit (for reflection)
    }

    /// <summary>
    /// March a ray through the voxel grid until it hits a solid voxel, comes within
    /// proximityRadius of sourcePos, escapes the grid, or exhausts maxSteps.
    /// Proximity check runs at every voxel step before the solid check.
    /// </summary>
    public static MarchResult March(
        Vector3 worldOrigin,
        Vector3 direction,
        VoxelGridData grid,
        BitArray voxelBits,
        int maxSteps,
        Vector3 sourcePos,
        float proximityRadius)
    {
        direction.Normalize();

        float proximityRadiusSq = proximityRadius * proximityRadius;

        Vector3 local = worldOrigin - grid.origin;

        int x = Mathf.FloorToInt(local.x / grid.voxelSize);
        int y = Mathf.FloorToInt(local.y / grid.voxelSize);
        int z = Mathf.FloorToInt(local.z / grid.voxelSize);

        int stepX = direction.x >= 0 ? 1 : -1;
        int stepY = direction.y >= 0 ? 1 : -1;
        int stepZ = direction.z >= 0 ? 1 : -1;

        float deltaX = direction.x != 0 ? Mathf.Abs(grid.voxelSize / direction.x) : float.MaxValue;
        float deltaY = direction.y != 0 ? Mathf.Abs(grid.voxelSize / direction.y) : float.MaxValue;
        float deltaZ = direction.z != 0 ? Mathf.Abs(grid.voxelSize / direction.z) : float.MaxValue;

        float nextBoundX = direction.x >= 0 ? (x + 1) * grid.voxelSize : x * grid.voxelSize;
        float nextBoundY = direction.y >= 0 ? (y + 1) * grid.voxelSize : y * grid.voxelSize;
        float nextBoundZ = direction.z >= 0 ? (z + 1) * grid.voxelSize : z * grid.voxelSize;

        float tMaxX = direction.x != 0 ? (nextBoundX - local.x) / direction.x : float.MaxValue;
        float tMaxY = direction.y != 0 ? (nextBoundY - local.y) / direction.y : float.MaxValue;
        float tMaxZ = direction.z != 0 ? (nextBoundZ - local.z) / direction.z : float.MaxValue;

        float tCurrent = 0f;
        Vector3 normal = Vector3.zero;

        for (int i = 0; i < maxSteps; i++)
        {
            if (x < 0 || x >= grid.gridSize.x ||
                y < 0 || y >= grid.gridSize.y ||
                z < 0 || z >= grid.gridSize.z)
            {
                return new MarchResult { escaped = true };
            }

            // Proximity check — voxel center distance to source, using squared distance
            // to avoid sqrt at every step
            Vector3 voxelCenter = grid.origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * grid.voxelSize;
            float dx = voxelCenter.x - sourcePos.x;
            float dy = voxelCenter.y - sourcePos.y;
            float dz = voxelCenter.z - sourcePos.z;
            if (dx * dx + dy * dy + dz * dz <= proximityRadiusSq)
            {
                return new MarchResult
                {
                    hitProximity = true,
                    hitPoint = voxelCenter,
                    normal = normal
                };
            }

            // Solid check
            int index = x + y * grid.gridSize.x + z * grid.gridSize.x * grid.gridSize.y;
            if (voxelBits[index])
            {
                return new MarchResult
                {
                    hitSolid = true,
                    hitPoint = worldOrigin + direction * tCurrent,
                    normal = normal
                };
            }

            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                tCurrent = tMaxX;
                x += stepX;
                tMaxX += deltaX;
                normal = new Vector3(-stepX, 0f, 0f);
            }
            else if (tMaxY < tMaxZ)
            {
                tCurrent = tMaxY;
                y += stepY;
                tMaxY += deltaY;
                normal = new Vector3(0f, -stepY, 0f);
            }
            else
            {
                tCurrent = tMaxZ;
                z += stepZ;
                tMaxZ += deltaZ;
                normal = new Vector3(0f, 0f, -stepZ);
            }
        }

        return new MarchResult { escaped = true };
    }

    /// <summary>
    /// Check if there is a clear line of sight between two world-space positions.
    /// Returns true if the path is unobstructed (no solid voxels in the way).
    /// </summary>
    public static bool HasLineOfSight(
        Vector3 from,
        Vector3 to,
        VoxelGridData grid,
        BitArray voxelBits,
        int maxSteps)
    {
        Vector3 direction = (to - from).normalized;
        float totalDistance = Vector3.Distance(from, to);

        Vector3 local = from - grid.origin;

        int x = Mathf.FloorToInt(local.x / grid.voxelSize);
        int y = Mathf.FloorToInt(local.y / grid.voxelSize);
        int z = Mathf.FloorToInt(local.z / grid.voxelSize);

        Vector3Int targetVoxel = new Vector3Int(
            Mathf.FloorToInt((to.x - grid.origin.x) / grid.voxelSize),
            Mathf.FloorToInt((to.y - grid.origin.y) / grid.voxelSize),
            Mathf.FloorToInt((to.z - grid.origin.z) / grid.voxelSize));

        int stepX = direction.x >= 0 ? 1 : -1;
        int stepY = direction.y >= 0 ? 1 : -1;
        int stepZ = direction.z >= 0 ? 1 : -1;

        float deltaX = direction.x != 0 ? Mathf.Abs(grid.voxelSize / direction.x) : float.MaxValue;
        float deltaY = direction.y != 0 ? Mathf.Abs(grid.voxelSize / direction.y) : float.MaxValue;
        float deltaZ = direction.z != 0 ? Mathf.Abs(grid.voxelSize / direction.z) : float.MaxValue;

        float nextBoundX = direction.x >= 0 ? (x + 1) * grid.voxelSize : x * grid.voxelSize;
        float nextBoundY = direction.y >= 0 ? (y + 1) * grid.voxelSize : y * grid.voxelSize;
        float nextBoundZ = direction.z >= 0 ? (z + 1) * grid.voxelSize : z * grid.voxelSize;

        float tMaxX = direction.x != 0 ? (nextBoundX - local.x) / direction.x : float.MaxValue;
        float tMaxY = direction.y != 0 ? (nextBoundY - local.y) / direction.y : float.MaxValue;
        float tMaxZ = direction.z != 0 ? (nextBoundZ - local.z) / direction.z : float.MaxValue;

        float tCurrent = 0f;

        for (int i = 0; i < maxSteps; i++)
        {
            if (x == targetVoxel.x && y == targetVoxel.y && z == targetVoxel.z)
                return true;

            if (x < 0 || x >= grid.gridSize.x ||
                y < 0 || y >= grid.gridSize.y ||
                z < 0 || z >= grid.gridSize.z)
                return false;

            if (tCurrent > totalDistance)
                return true;

            if (i > 0)  // skip the origin voxel always
            {
                int index = x + y * grid.gridSize.x + z * grid.gridSize.x * grid.gridSize.y;
                if (voxelBits[index])
                    return false; // or solidDistance += / solidVoxels++ in the other two
            }

            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                tCurrent = tMaxX;
                x += stepX;
                tMaxX += deltaX;
            }
            else if (tMaxY < tMaxZ)
            {
                tCurrent = tMaxY;
                y += stepY;
                tMaxY += deltaY;
            }
            else
            {
                tCurrent = tMaxZ;
                z += stepZ;
                tMaxZ += deltaZ;
            }
        }

        return false;
    }

    public static float GetPermeationFactor(
        Vector3 from,
        Vector3 to,
        VoxelGridData grid,
        BitArray voxelBits,
        int maxSteps,
        float absorptionCoefficient)
    {
        Vector3 direction = (to - from).normalized;
        float totalDistance = Vector3.Distance(from, to);

        Vector3 local = from - grid.origin;

        int x = Mathf.FloorToInt(local.x / grid.voxelSize);
        int y = Mathf.FloorToInt(local.y / grid.voxelSize);
        int z = Mathf.FloorToInt(local.z / grid.voxelSize);

        Vector3Int targetVoxel = new Vector3Int(
            Mathf.FloorToInt((to.x - grid.origin.x) / grid.voxelSize),
            Mathf.FloorToInt((to.y - grid.origin.y) / grid.voxelSize),
            Mathf.FloorToInt((to.z - grid.origin.z) / grid.voxelSize));

        int stepX = direction.x >= 0 ? 1 : -1;
        int stepY = direction.y >= 0 ? 1 : -1;
        int stepZ = direction.z >= 0 ? 1 : -1;

        float deltaX = direction.x != 0 ? Mathf.Abs(grid.voxelSize / direction.x) : float.MaxValue;
        float deltaY = direction.y != 0 ? Mathf.Abs(grid.voxelSize / direction.y) : float.MaxValue;
        float deltaZ = direction.z != 0 ? Mathf.Abs(grid.voxelSize / direction.z) : float.MaxValue;

        float nextBoundX = direction.x >= 0 ? (x + 1) * grid.voxelSize : x * grid.voxelSize;
        float nextBoundY = direction.y >= 0 ? (y + 1) * grid.voxelSize : y * grid.voxelSize;
        float nextBoundZ = direction.z >= 0 ? (z + 1) * grid.voxelSize : z * grid.voxelSize;

        float tMaxX = direction.x != 0 ? (nextBoundX - local.x) / direction.x : float.MaxValue;
        float tMaxY = direction.y != 0 ? (nextBoundY - local.y) / direction.y : float.MaxValue;
        float tMaxZ = direction.z != 0 ? (nextBoundZ - local.z) / direction.z : float.MaxValue;

        float tCurrent = 0f;
        float solidDistance = 0f;

        for (int i = 0; i < maxSteps; i++)
        {
            if (x == targetVoxel.x && y == targetVoxel.y && z == targetVoxel.z)
                break;

            if (x < 0 || x >= grid.gridSize.x ||
                y < 0 || y >= grid.gridSize.y ||
                z < 0 || z >= grid.gridSize.z)
                break;

            if (tCurrent > totalDistance)
                break;

            int index = x + y * grid.gridSize.x + z * grid.gridSize.x * grid.gridSize.y;
            if (voxelBits[index])
                solidDistance += grid.voxelSize;

            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                tCurrent = tMaxX;
                x += stepX;
                tMaxX += deltaX;
            }
            else if (tMaxY < tMaxZ)
            {
                tCurrent = tMaxY;
                y += stepY;
                tMaxY += deltaY;
            }
            else
            {
                tCurrent = tMaxZ;
                z += stepZ;
                tMaxZ += deltaZ;
            }
        }

        return Mathf.Exp(-solidDistance * absorptionCoefficient);
    }

    public static float GetSolidDistance(
    Vector3 from,
    Vector3 to,
    VoxelGridData grid,
    BitArray voxelBits,
    int maxSteps)
    {
        Vector3 direction = (to - from).normalized;
        float totalDistance = Vector3.Distance(from, to);

        Vector3 local = from - grid.origin;

        int x = Mathf.FloorToInt(local.x / grid.voxelSize);
        int y = Mathf.FloorToInt(local.y / grid.voxelSize);
        int z = Mathf.FloorToInt(local.z / grid.voxelSize);

        Vector3Int targetVoxel = new Vector3Int(
            Mathf.FloorToInt((to.x - grid.origin.x) / grid.voxelSize),
            Mathf.FloorToInt((to.y - grid.origin.y) / grid.voxelSize),
            Mathf.FloorToInt((to.z - grid.origin.z) / grid.voxelSize));

        int stepX = direction.x >= 0 ? 1 : -1;
        int stepY = direction.y >= 0 ? 1 : -1;
        int stepZ = direction.z >= 0 ? 1 : -1;

        float deltaX = direction.x != 0 ? Mathf.Abs(grid.voxelSize / direction.x) : float.MaxValue;
        float deltaY = direction.y != 0 ? Mathf.Abs(grid.voxelSize / direction.y) : float.MaxValue;
        float deltaZ = direction.z != 0 ? Mathf.Abs(grid.voxelSize / direction.z) : float.MaxValue;

        float nextBoundX = direction.x >= 0 ? (x + 1) * grid.voxelSize : x * grid.voxelSize;
        float nextBoundY = direction.y >= 0 ? (y + 1) * grid.voxelSize : y * grid.voxelSize;
        float nextBoundZ = direction.z >= 0 ? (z + 1) * grid.voxelSize : z * grid.voxelSize;

        float tMaxX = direction.x != 0 ? (nextBoundX - local.x) / direction.x : float.MaxValue;
        float tMaxY = direction.y != 0 ? (nextBoundY - local.y) / direction.y : float.MaxValue;
        float tMaxZ = direction.z != 0 ? (nextBoundZ - local.z) / direction.z : float.MaxValue;

        float tCurrent = 0f;
        int totalVoxels = 0;
        int solidVoxels = 0;

        for (int i = 0; i < maxSteps; i++)
        {
            if (x == targetVoxel.x && y == targetVoxel.y && z == targetVoxel.z)
                break;

            if (x < 0 || x >= grid.gridSize.x ||
                y < 0 || y >= grid.gridSize.y ||
                z < 0 || z >= grid.gridSize.z)
                break;

            if (tCurrent > totalDistance)
                break;

            totalVoxels++;
            int index = x + y * grid.gridSize.x + z * grid.gridSize.x * grid.gridSize.y;
            if (voxelBits[index])
                solidVoxels++;

            if (tMaxX < tMaxY && tMaxX < tMaxZ)
            {
                tCurrent = tMaxX;
                x += stepX;
                tMaxX += deltaX;
            }
            else if (tMaxY < tMaxZ)
            {
                tCurrent = tMaxY;
                y += stepY;
                tMaxY += deltaY;
            }
            else
            {
                tCurrent = tMaxZ;
                z += stepZ;
                tMaxZ += deltaZ;
            }
        }

        return solidVoxels * grid.voxelSize;
    }
}