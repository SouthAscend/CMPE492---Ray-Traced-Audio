using UnityEngine;

/// <summary>
/// Attach to any cube-shaped GameObject that should dynamically affect the voxel grid.
/// Self-registers with DynamismManager on Enable/Disable.
///
/// All job scheduling and voxel management is handled by DynamismManager.
/// This component only provides identity and transform data.
/// </summary>
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

    public Bounds GetWorldBounds()
    {
        // Compute AABB from transform without needing a Renderer
        // Encapsulates all 8 corners of the OBB
        Transform t = transform;
        Vector3 halfExtents = t.lossyScale * 0.5f;

        Vector3 right   = t.right   * halfExtents.x;
        Vector3 up      = t.up      * halfExtents.y;
        Vector3 forward = t.forward * halfExtents.z;

        // AABB of an OBB = center ± sum of absolute axis projections
        Vector3 absExtents = new Vector3(
            Mathf.Abs(right.x) + Mathf.Abs(up.x) + Mathf.Abs(forward.x),
            Mathf.Abs(right.y) + Mathf.Abs(up.y) + Mathf.Abs(forward.y),
            Mathf.Abs(right.z) + Mathf.Abs(up.z) + Mathf.Abs(forward.z));

        return new Bounds(t.position, absExtents * 2f);
    }

    // Not used directly anymore — DynamismManager schedules jobs itself
    public System.Collections.Generic.List<int> GetOccupiedVoxelIndices(VoxelGridData grid)
    {
        return new System.Collections.Generic.List<int>();
    }
}