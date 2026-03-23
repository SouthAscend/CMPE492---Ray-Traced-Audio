using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Attach to any cube-shaped GameObject that should dynamically affect the voxel grid.
/// Self-registers with DynamismManager on Enable/Disable.
///
/// Uses OBB math via Burst jobs — no Physics calls at runtime.
/// Works for any rotation/scale/position combination.
///
/// For other shapes: implement IDynamicObstacle and follow the same pattern.
/// </summary>
[RequireComponent(typeof(Renderer))]
public class DynamicCubeObstacle : MonoBehaviour, IDynamicObstacle
{
    private void OnEnable()
    {
        DynamismManager.Instance?.RegisterDynamic(this);
    }

    private void OnDisable()
    {
        DynamismManager.Instance?.UnregisterDynamic(this);
    }

    // -------------------------------------------------------------------------

    public Bounds GetWorldBounds()
    {
        return GetComponent<Renderer>().bounds;
    }

    /// <summary>
    /// Returns all voxel indices this cube currently occupies using a Burst OBB job.
    /// Runs the job synchronously (Complete immediately) since we need results before
    /// writing to the dynamic grid. The parallelism still speeds up large objects.
    /// </summary>
    public List<int> GetOccupiedVoxelIndices(VoxelGridData grid)
    {
        // Get the AABB of this cube in world space to bound the voxel region to test
        Bounds worldBounds = GetWorldBounds();

        Vector3Int min = WorldToVoxel(worldBounds.min, grid);
        Vector3Int max = WorldToVoxel(worldBounds.max, grid);

        min.x = Mathf.Clamp(min.x, 0, grid.gridSize.x - 1);
        min.y = Mathf.Clamp(min.y, 0, grid.gridSize.y - 1);
        min.z = Mathf.Clamp(min.z, 0, grid.gridSize.z - 1);
        max.x = Mathf.Clamp(max.x + 1, 0, grid.gridSize.x - 1);
        max.y = Mathf.Clamp(max.y + 1, 0, grid.gridSize.y - 1);
        max.z = Mathf.Clamp(max.z + 1, 0, grid.gridSize.z - 1);

        // Build list of voxel coords in the AABB region
        List<int3> coordList = new List<int3>();
        List<int> indexList = new List<int>();

        for (int z = min.z; z <= max.z; z++)
        for (int y = min.y; y <= max.y; y++)
        for (int x = min.x; x <= max.x; x++)
        {
            coordList.Add(new int3(x, y, z));
            indexList.Add(x + y * grid.gridSize.x + z * grid.gridSize.x * grid.gridSize.y);
        }

        if (coordList.Count == 0)
            return new List<int>();

        // Prepare job data
        NativeArray<int3> voxelCoords = new NativeArray<int3>(coordList.ToArray(), Allocator.TempJob);
        NativeArray<byte> results = new NativeArray<byte>(coordList.Count, Allocator.TempJob);

        Transform t = transform;
        Quaternion rotInv = Quaternion.Inverse(t.rotation);

        OBBVoxelFillJob job = new OBBVoxelFillJob
        {
            obbCenter = t.position,
            obbRotationInverse = new quaternion(rotInv.x, rotInv.y, rotInv.z, rotInv.w),
            obbHalfExtents = new float3(
                t.lossyScale.x * 0.5f,
                t.lossyScale.y * 0.5f,
                t.lossyScale.z * 0.5f),
            gridOrigin = grid.origin,
            voxelSize = grid.voxelSize,
            voxelCoords = voxelCoords,
            results = results
        };

        // Schedule and immediately complete — we need results now
        JobHandle handle = job.Schedule(coordList.Count, 64);
        handle.Complete();

        // Collect indices where the voxel is inside the OBB
        List<int> occupied = new List<int>();
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i] == 1)
                occupied.Add(indexList[i]);
        }

        voxelCoords.Dispose();
        results.Dispose();

        return occupied;
    }

    // -------------------------------------------------------------------------

    private Vector3Int WorldToVoxel(Vector3 worldPos, VoxelGridData grid)
    {
        Vector3 local = worldPos - grid.origin;
        return new Vector3Int(
            Mathf.FloorToInt(local.x / grid.voxelSize),
            Mathf.FloorToInt(local.y / grid.voxelSize),
            Mathf.FloorToInt(local.z / grid.voxelSize));
    }
}