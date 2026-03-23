/// <summary>
/// Interface for any dynamic obstacle shape that can mark voxels as occupied.
/// Implement this for each primitive shape (cube, sphere, capsule, etc).
/// DynamismManager calls GetOccupiedVoxelIndices to know which voxels to fill.
/// </summary>
public interface IDynamicObstacle
{
    /// <summary>
    /// Returns the flat voxel indices this shape currently occupies in the grid.
    /// </summary>
    System.Collections.Generic.List<int> GetOccupiedVoxelIndices(VoxelGridData grid);

    /// <summary>
    /// Returns the world-space AABB of this shape — used to compute dirty regions.
    /// </summary>
    UnityEngine.Bounds GetWorldBounds();
}