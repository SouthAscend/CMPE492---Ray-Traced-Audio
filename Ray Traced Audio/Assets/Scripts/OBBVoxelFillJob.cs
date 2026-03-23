using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

/// <summary>
/// Burst-compiled parallel job that tests each voxel in a dirty region
/// against an Oriented Bounding Box (OBB).
///
/// Each job index corresponds to one voxel coord in the dirty region.
/// Result: 1 = voxel is inside the OBB, 0 = outside.
///
/// This runs entirely on background threads with no Unity API calls —
/// pure math only, fully parallelizable.
/// </summary>
[BurstCompile]
public struct OBBVoxelFillJob : IJobParallelFor
{
    // OBB parameters
    [ReadOnly] public float3 obbCenter;
    [ReadOnly] public quaternion obbRotationInverse; // precomputed inverse rotation
    [ReadOnly] public float3 obbHalfExtents;

    // Grid parameters
    [ReadOnly] public float3 gridOrigin;
    [ReadOnly] public float voxelSize;

    // Voxel coordinates to test — one per job index
    [ReadOnly] public NativeArray<int3> voxelCoords;

    // Results — 1 if inside OBB, 0 if outside
    [WriteOnly] public NativeArray<byte> results;

    public void Execute(int i)
    {
        int3 coord = voxelCoords[i];

        // World-space center of this voxel
        float3 voxelCenter = gridOrigin +
            new float3(coord.x + 0.5f, coord.y + 0.5f, coord.z + 0.5f) * voxelSize;

        // Transform voxel center into OBB local space
        float3 local = math.mul(obbRotationInverse, voxelCenter - obbCenter);

        // Inside test — all three axes must be within half-extents
        bool inside = math.abs(local.x) <= obbHalfExtents.x &&
                      math.abs(local.y) <= obbHalfExtents.y &&
                      math.abs(local.z) <= obbHalfExtents.z;

        results[i] = inside ? (byte)1 : (byte)0;
    }
}