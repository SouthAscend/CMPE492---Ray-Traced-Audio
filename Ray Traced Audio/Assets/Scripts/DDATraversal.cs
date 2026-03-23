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
        public bool hitSolid;    // Ray hit a solid voxel
        public bool escaped;     // Ray left the grid without hitting anything
        public Vector3 hitPoint; // World-space position where the ray entered the solid voxel
        public Vector3 normal;   // Surface normal of the face that was hit (for reflection)
    }

    /// <summary>
    /// March a ray through the voxel grid until it hits a solid voxel, escapes the grid,
    /// or exhausts maxSteps. Used for the main bouncing rays.
    /// </summary>
    public static MarchResult March(
        Vector3 worldOrigin,
        Vector3 direction,
        VoxelGridData grid,
        BitArray voxelBits,
        int maxSteps)
    {
        direction.Normalize();

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
            // Left the grid — ray escaped
            if (x < 0 || x >= grid.gridSize.x ||
                y < 0 || y >= grid.gridSize.y ||
                z < 0 || z >= grid.gridSize.z)
            {
                return new MarchResult { escaped = true };
            }

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

            // Step to the next voxel boundary
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
            // Reached the target voxel — LOS is clear
            if (x == targetVoxel.x && y == targetVoxel.y && z == targetVoxel.z)
                return true;

            // Left the grid — no LOS
            if (x < 0 || x >= grid.gridSize.x ||
                y < 0 || y >= grid.gridSize.y ||
                z < 0 || z >= grid.gridSize.z)
                return false;

            // Travelled past the target distance — LOS is clear
            if (tCurrent > totalDistance)
                return true;

            int index = x + y * grid.gridSize.x + z * grid.gridSize.x * grid.gridSize.y;
            if (voxelBits[index])
                return false; // Solid voxel blocking LOS

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
}