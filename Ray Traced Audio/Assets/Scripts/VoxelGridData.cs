using System.Collections;
using UnityEngine;

[CreateAssetMenu(fileName = "NewVoxelGrid", menuName = "RaytracedAudio/VoxelGridData")]
public class VoxelGridData : ScriptableObject
{
    public Vector3Int gridSize;
    public float voxelSize;
    public Vector3 origin;

    [HideInInspector]
    public int[] isOccupiedBits;

    // Hash of all obstacle collider bounds at the time of the last bake.
    // If the live scene hash matches this, the cached voxel data is still valid.
    [HideInInspector]
    public string sceneHash;

    public BitArray GetBitArray()
    {
        return new BitArray(isOccupiedBits);
    }
}