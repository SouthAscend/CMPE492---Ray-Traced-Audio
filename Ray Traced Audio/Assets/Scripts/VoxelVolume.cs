using UnityEngine;

public class VoxelVolume : MonoBehaviour
{
    public float voxelSize = 0.5f;
    public LayerMask obstacleMask;
    public bool createVisuals = true;
    public VoxelGridData voxelData;

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.3f);
        Gizmos.DrawCube(transform.position, transform.localScale);
        Gizmos.color = Color.green;
        Gizmos.DrawWireCube(transform.position, transform.localScale);
    }
}